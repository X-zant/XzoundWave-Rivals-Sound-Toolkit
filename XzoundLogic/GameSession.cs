using System.IO;
using System.Text.RegularExpressions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Internationalization;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.UE4.Wwise;

namespace MRAudioKit;

public sealed record SkinEntry(string SkinId, string Label, List<GameFile> Banks);
public sealed record CharacterEntry(string CharId, string Name, List<SkinEntry> Skins);

/// <summary>
/// One mounted view of the game. Everything the tool knows comes from here and
/// nothing is written to disk: the soundKit workflow's 6.4 GB of hand-exported
/// folders is replaced by this object.
/// </summary>
public sealed class GameSession
{
    public DefaultFileProvider Provider { get; private set; }
    public string Language { get; private set; } = "English(US)";

    // FNV-1(assetName) -> assetName, built from the shipped WwiseEvent uassets.
    // Soundbanks store only the hash; these assets are the plaintext it was made from.
    private readonly Dictionary<uint, string> _nameByHash = new();

    // Loose streamed media, indexed once. A linear scan of ~890k paths per lookup
    // is fine for one probe and hopeless for a list of 500 rows.
    private readonly Dictionary<string, GameFile> _mediaByLangId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, GameFile> _mediaNeutral = new();

    private readonly Dictionary<(string, string), string> _loc = new();
    private readonly Dictionary<string, string> _tableNamespace = new();

    // Every .bnk path, resolved to the one copy the engine would load. Built once per
    // mount because three separate walks used to settle the same question their own way.
    private readonly Dictionary<string, GameFile> _bankByPath = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CharacterEntry> Characters { get; private set; } = [];

    /// <summary>
    /// Every bank that is not a character's: ambience, music, UI, maps, announcers.
    /// They were dropped entirely because the catalogue keyed on a 7-digit skin id,
    /// which most of the game's banks do not have -- roughly half the shipped audio
    /// was unreachable in the picker.
    /// </summary>
    public IReadOnlyList<CharacterEntry> OtherBanks { get; private set; } = [];

    public static readonly string[] Languages = ["English(US)", "Japanese(JP)", "Chinese(CN)"];

    private static string LocresCode(string language) => language switch
    {
        "Japanese(JP)" => "ja",
        "Chinese(CN)" => "zh-Hans-CN",
        _ => "en",
    };

    /// <summary>
    /// Bumped by every mount. Anything caching work derived from this session has to
    /// include it in its key: the window mounts one session object in place rather
    /// than replacing it, so object identity alone cannot tell "before the game was
    /// loaded" from "after", and a cache keyed on identity keeps serving the empty
    /// answer it computed first.
    /// </summary>
    public int Generation { get; private set; }

    // media id -> the bank whose Sound streams it, and the bank whose music track plays it.
    // Built together on first use (one pass over every bank's objects), and dropped by a
    // mount, which is another game.
    private Dictionary<uint, string> _streamedBy, _trackedBy;
    private readonly object _streamedGate = new();
    // Every Sound and music track that plays each medium, from the same pass: what a snapshot
    // of this version is made of.
    private Dictionary<uint, List<GameSnapshot.Player>> _players;
    private List<string> _playerBanks;
    private GameSnapshot _snapshot;

    /// <summary>Banks the last walk for <see cref="Snapshot"/> could not read; a snapshot with any is not kept.</summary>
    public int SnapshotFailures { get; private set; }

    /// <summary>The Paks folder this session mounted.</summary>
    public string PaksDir { get; private set; }

    /// <summary>Every bank path the engine would load, one each.</summary>
    public IEnumerable<string> BankPaths => _bankByPath.Keys;

    /// <summary>The bank set as a short hash, which names this version when the install has no version.json.</summary>
    public string BankFingerprint() => GameSnapshot.FingerprintOf(_bankByPath.Select(kv => (kv.Key, kv.Value.Size)));

    /// <summary>
    /// Which Sound or music track plays every medium in this version, with its version and bank
    /// fingerprint. Reads every bank the first time (shared with the lookups above).
    /// </summary>
    public GameSnapshot Snapshot(string source = "this install")
    {
        lock (_streamedGate)
        {
            IndexPlayers();
            if (_snapshot is not null) return _snapshot;
            var (version, changelist) = GameSnapshot.VersionOf(PaksDir);
            return _snapshot = new GameSnapshot
            {
                Version = version, Changelist = changelist, Fingerprint = BankFingerprint(),
                Taken = DateTime.UtcNow, Source = source, Banks = _playerBanks,
                Media = _players.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
            };
        }
    }

    /// <summary>
    /// The bank whose Sound plays this medium, for a medium no bank embeds (a loose Media/
    /// file): its map, menu or skin, which nothing else about the file says. Null for one
    /// no Sound plays, which in this game is a music track's (see <see cref="MusicBank"/>).
    /// </summary>
    public string StreamingBank(uint mediaId)
    {
        lock (_streamedGate)
        {
            IndexPlayers();
            return _streamedBy.GetValueOrDefault(mediaId);
        }
    }

    /// <summary>The bank whose music track plays this medium, or null when no track does.</summary>
    public string MusicBank(uint mediaId)
    {
        lock (_streamedGate)
        {
            IndexPlayers();
            return _trackedBy.GetValueOrDefault(mediaId);
        }
    }

    /// <summary>
    /// The bank that plays a medium, however it does: embeds it, streams it from a loose file,
    /// or places it on a music track. Null for one nothing in the game plays. The first call
    /// that gets past the embedded index reads every bank once.
    /// </summary>
    public string PlayingBank(uint mediaId) =>
        BanksForMedia(mediaId).FirstOrDefault() ?? StreamingBank(mediaId) ?? MusicBank(mediaId);

    private void IndexPlayers()
    {
        if (_streamedBy is not null) return;
        var streamed = new Dictionary<uint, string>();
        var tracked = new Dictionary<uint, string>();
        var players = new Dictionary<uint, List<GameSnapshot.Player>>();
        var banks = new List<string>();
        void Add(uint media, GameSnapshot.Player p)
        {
            if (!players.TryGetValue(media, out var list)) players[media] = list = [];
            list.Add(p);
        }
        // In path order, so every install of one version records the same thing in the same order.
        var failures = 0;
        foreach (var (path, file) in _bankByPath.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            // A bank's players are gathered on their own and taken only once the whole bank has
            // read, so one that fails part-way leaves nothing behind under another bank's index.
            var sounds = new List<(uint Media, uint Sound)>();
            var tracks = new List<(uint Media, uint Track)>();
            try
            {
                var bytes = file.Read();
                var shape = ModUpdate.ReadStructure(bytes);
                foreach (var (sound, media) in shape.SoundSource)
                    if (shape.SoundBodies.TryGetValue(sound, out var body) && body.PlaysMedia) sounds.Add((media, sound));
                // A track's sources are not in the structure walk; the full parse is paid only
                // by the banks that have tracks at all.
                if (shape.MusicTracks > 0)
                    foreach (var (track, media) in ModUpdate.Lines.Of(bytes, path).TrackMedia)
                        foreach (var m in media) tracks.Add((m, track));
            }
            catch { failures++; continue; /* one unreadable bank must not stop the rest */ }
            if (sounds.Count + tracks.Count == 0) continue;
            var at = banks.Count;
            banks.Add(path);
            foreach (var (media, sound) in sounds)
            {
                Prefer(streamed, media, path);
                Add(media, new GameSnapshot.Player(at, GameSnapshot.Sound, sound));
            }
            foreach (var (media, track) in tracks)
            {
                Prefer(tracked, media, path);
                Add(media, new GameSnapshot.Player(at, GameSnapshot.Track, track));
            }
        }
        SnapshotFailures = failures;
        _players = players;
        _playerBanks = banks;
        _trackedBy = tracked;
        _streamedBy = streamed;
    }

    // The first bank found keeps a medium, unless it is another language's and this one is the
    // active language's or none: the tree lists the active language's banks, and a row is found
    // under the bank named here.
    private void Prefer(Dictionary<uint, string> map, uint media, string path)
    {
        if (!map.TryGetValue(media, out var had)) { map[media] = path; return; }
        if (Foreign(had) && !Foreign(path)) map[media] = path;
    }

    private bool Foreign(string path) =>
        LanguageOf(path) is { } l && !l.Equals(Language, StringComparison.OrdinalIgnoreCase);

    public void Mount(Settings s, Action<string> progress)
    {
        Generation++;
        lock (_streamedGate) { _streamedBy = null; _trackedBy = null; _players = null; _playerBanks = null; _snapshot = null; }
        Language = s.Language;
        PaksDir = s.PaksDir;

        progress("Mounting paks...");
        Provider = new DefaultFileProvider(s.PaksDir, SearchOption.TopDirectoryOnly,
                                          new VersionContainer(EGame.GAME_MarvelRivals),
                                          StringComparer.OrdinalIgnoreCase);
        Provider.Initialize();
        Provider.SubmitKey(new FGuid(), new FAesKey(s.AesKey));

        if (!string.IsNullOrWhiteSpace(s.UsmapPath) && File.Exists(s.UsmapPath))
            Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(s.UsmapPath);

        if (Provider.Files.Count == 0)
            throw new InvalidOperationException(
                "Mounted 0 files. Check the Paks folder and the AES key.");

        progress($"Mounted {Provider.Files.Count:N0} files. Indexing media...");
        IndexBankPaths();
        // Again, now the banks are this game's: a walk that ran while the mount was under way
        // would otherwise answer for the previous one until the next mount.
        lock (_streamedGate) { _streamedBy = null; _trackedBy = null; _players = null; _playerBanks = null; _snapshot = null; }
        IndexMedia();

        progress("Reading event names...");
        IndexEventNames();

        progress("Loading localization...");
        LoadLocres(LocresCode(s.Language));

        progress("Building hero list...");
        BuildCatalog();

        progress($"Ready — {Characters.Count} characters, {_nameByHash.Count:N0} event names, " +
                 $"{_mediaByLangId.Count + _mediaNeutral.Count:N0} media files.");
    }

    /// <summary>
    /// Which copy of a file the engine would load, when several containers ship the same
    /// path. Higher read order first, which is how the parser already separates a patch
    /// (103) from a base chunk (3); then the higher container name, because containers of
    /// equal order are mounted in name order and the last one mounted is the one in play.
    ///
    /// The tie is not hypothetical. The game ships TWO patch paks at read order 103, and
    /// four .bnk paths sit in both of them, so the aggregated provider hands back whichever
    /// registered first and that is a race between mounts. music_formal.bnk is one of the
    /// four: the older patch's hierarchy times 627 media and the newer one times 655, the
    /// newer being a strict superset, so losing the race hid 28 pieces of music the game
    /// plays and would have built a mod on a hierarchy 21,808 bytes out of date. The same
    /// four differ by a few kilobytes of Init, ambience and display banks.
    /// </summary>
    private static GameFile Engine(GameFile a, GameFile b)
    {
        if (a is null) return b;
        if (b is null) return a;
        var byOrder = Order(a).CompareTo(Order(b));
        if (byOrder != 0) return byOrder > 0 ? a : b;
        return string.CompareOrdinal(Container(a), Container(b)) >= 0 ? a : b;

        static long Order(GameFile f) => (f as VfsEntry)?.Vfs?.ReadOrder ?? 0;
        static string Container(GameFile f) => (f as VfsEntry)?.Vfs?.Name ?? "";
    }

    /// <summary>
    /// One entry per .bnk path, so every walk that needs a bank asks the same question
    /// once. The catalogue, the media index and the builder each used to deduplicate the
    /// provider's repeated paths on their own and each kept whichever copy it met first,
    /// which meant they could disagree about what "the bank" was within one mount.
    /// </summary>
    private void IndexBankPaths()
    {
        _bankByPath.Clear();
        foreach (var (path, file) in Provider.Files)
        {
            if (!path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)) continue;
            _bankByPath[path] = Engine(file, _bankByPath.GetValueOrDefault(path));
        }
    }

    /// <summary>The copy of a bank the game would load, or null when no container has it.</summary>
    public GameFile Bank(string path) => _bankByPath.GetValueOrDefault(path);

    private void IndexMedia()
    {
        const string root = "Marvel/Content/WwiseAudio/Media/";
        foreach (var (path, file) in Provider.Files)
        {
            if (!path.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = path[root.Length..].Split('/');
            if (rest.Length == 0) continue;
            var leaf = rest[^1];
            if (!uint.TryParse(Path.GetFileNameWithoutExtension(leaf), out var id)) continue;

            // Media/<lang>/<bucket>/<id>.wem  vs  Media/<bucket>/<id>.wem
            // Resolved the same way a bank is, because one loose media path is shipped by
            // two containers as well and last-wins would pick by mount order.
            if (rest.Length == 3)
            {
                var key = $"{rest[0]}|{id}";
                _mediaByLangId[key] = Engine(file, _mediaByLangId.GetValueOrDefault(key));
            }
            else _mediaNeutral[id] = Engine(file, _mediaNeutral.GetValueOrDefault(id));
        }
    }

    private void IndexEventNames()
    {
        foreach (var path in Provider.Files.Keys)
        {
            // Every asset under the Wwise asset root is named after the Wwise object it
            // stands for, which is what makes its stem the plaintext a hash was made from.
            // Reading only WwiseEvents/ was one folder away from the truth: the root holds
            // WwiseEvents (41,097 assets), Events (261), Effects (48) and WwiseBus (19),
            // and the game's MUSIC events live in Events/Music/music_new/event/. None of
            // music_formal.bnk's 61 events resolved because of that, so no music row could
            // be named. Widening to the root adds 322 hashes, changes none of the names
            // the narrower scope already produced, collides with none of them, and names
            // 50 of those 61 events.
            //
            // The root and not the whole pak: outside it a stem like Dead belongs to an
            // animation asset, and hashing that would let an unrelated file name a sound.
            if (path.IndexOf("/Wwise/Assets/", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            _nameByHash[WwiseFnv.GetHash(name)] = name;
        }
    }

    private void LoadLocres(string code)
    {
        _loc.Clear();
        var path = $"Marvel/Content/Localization/Game/{code}/Game.locres";
        if (!Provider.Files.TryGetValue(path, out var f))
        {
            path = "Marvel/Content/Localization/Game/en/Game.locres";
            if (!Provider.Files.TryGetValue(path, out f)) return;
        }
        using var ar = f.CreateReader();
        foreach (var (ns, keys) in new FTextLocalizationResource(ar).Entries)
            foreach (var (k, e) in keys)
                _loc[(ns.Str, k.Str)] = e.LocalizedString;
    }

    public string Loc(string ns, string key)
        => _loc.TryGetValue((ns, key), out var v) && v.Length > 0 ? v : null;

    /// <summary>
    /// Voice lines are not plain FTexts -- they are StringTable entries whose
    /// namespace comes from the referenced UStringTable, and only then does the
    /// locres lookup succeed. Reading .Text alone yields the Chinese source string.
    /// </summary>
    public string Localize(FText t)
    {
        if (t is null) return "";
        switch (t.TextHistory)
        {
            case FTextHistory.Base b:
                return Loc(b.Namespace, b.Key) ?? t.Text;
            case FTextHistory.StringTableEntry se:
            {
                var id = se.TableId.Text;
                if (!_tableNamespace.TryGetValue(id, out var ns))
                {
                    ns = id;
                    if (Provider.TryLoadPackageObject<UStringTable>(id, out var st))
                        ns = st.StringTable.TableNamespace;
                    _tableNamespace[id] = ns;
                }
                return Loc(ns, se.Key) ?? t.Text;
            }
            default:
                return t.Text;
        }
    }

    public string EventName(uint hash) => _nameByHash.GetValueOrDefault(hash);

    // ---- every voice line in the game --------------------------------------

    private Dictionary<string, (string Category, string Subtitle)> _allLines;

    /// <summary>
    /// Category and subtitle for any event, from EVERY voice table in the game.
    ///
    /// A character's own tables are read first and separately, because a skin's
    /// wording should win for its own lines. This is the backstop for everything
    /// else: boss, NPC, system and tutorial banks belong to no character at all, so
    /// without it they showed no dialogue whatsoever.
    ///
    /// Built once, on first use -- 173 tables and 25,000 events, about 1.7 seconds,
    /// which is worth paying only if something actually asks.
    /// </summary>
    public (string Category, string Subtitle)? VoiceLine(string eventName)
    {
        if (string.IsNullOrEmpty(eventName)) return null;
        _allLines ??= BuildAllLines();
        return _allLines.TryGetValue(eventName, out var v) ? v : null;
    }

    private Dictionary<string, (string, string)> BuildAllLines()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var tables = Provider.Files.Keys
            .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                        (k.Contains("HeroVoice", StringComparison.OrdinalIgnoreCase) ||
                         k.EndsWith("/MarvelHeroVoiceTable.uasset", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in tables)
        {
            try
            {
                var pkg = path[..^".uasset".Length];
                var dt = Provider.LoadPackageObject<UDataTable>(pkg + "." + Path.GetFileName(pkg));
                if (dt is null) continue;
                foreach (var (_, row) in dt.RowMap)
                {
                    var ev = row.GetOrDefault<FPackageIndex>("Event")?.Name;
                    if (string.IsNullOrEmpty(ev) || map.ContainsKey(ev)) continue;
                    map[ev] = (row.GetOrDefault<FName>("Category").Text ?? "",
                               Localize(row.GetOrDefault<FText>("Lines")));
                }
            }
            catch { /* one unreadable table must not lose the rest */ }
        }
        return map;
    }

    /// <summary>Loose streamed copy of a media id, in the active language then neutral.</summary>
    public GameFile LooseMedia(uint id)
    {
        if (_mediaByLangId.TryGetValue($"{Language}|{id}", out var f)) return f;
        foreach (var lang in Languages)
            if (_mediaByLangId.TryGetValue($"{lang}|{id}", out f)) return f;
        return _mediaNeutral.GetValueOrDefault(id);
    }

    /// <summary>
    /// Loose streamed copy of a media id as ONE bank would stream it: from its own
    /// language's folder, then the neutral one, and never another language's.
    ///
    /// The overload above falls through every language because a row only needs some
    /// copy to play. Something that writes a file for a particular bank cannot do that:
    /// an English bank's line written over the Japanese copy is a change to a bank the
    /// mod never touched, and the English one is left playing the old audio.
    /// </summary>
    /// <param name="language">The bank's language folder, or null for a bank that has none.</param>
    public GameFile LooseMedia(uint id, string language)
    {
        if (language is not null && _mediaByLangId.TryGetValue($"{language}|{id}", out var f)) return f;
        return _mediaNeutral.GetValueOrDefault(id);
    }

    /// <summary>
    /// The game's file at exactly this path, as the engine loads it: the copy the media index
    /// chose when two containers ship one path, else whatever the provider holds there. Null
    /// when the game ships nothing at that path.
    /// </summary>
    public GameFile GameFileAt(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (uint.TryParse(Path.GetFileNameWithoutExtension(path), out var id) &&
            LooseMedia(id, LanguageOf(path)) is { } loose && loose.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            return loose;
        return Provider?.Files.TryGetValue(path, out var file) == true ? file : null;
    }

    /// <summary>The language folder a game path sits in, or null when it sits in none.</summary>
    public static string LanguageOf(string path) =>
        path is null ? null
        : Languages.FirstOrDefault(l => path.Contains($"/{l}/", StringComparison.OrdinalIgnoreCase));

    // media id -> the pak path of the bank that embeds it, across EVERY bank in the
    // game. Without this the tool can only name a bank once you have opened its skin,
    // which is backwards: you stage a file precisely because you don't know yet.
    // ONE TO MANY on purpose: 265 of Luna's media turned out to be embedded in more
    // than one bank. Last-wins would name the wrong file in the UI and, worse, rebuild
    // the wrong file at build time while silently leaving the real one stale.
    private readonly Dictionary<uint, List<string>> _mediaBank = new();
    public bool BankIndexReady { get; private set; }
    public IReadOnlyDictionary<uint, List<string>> MediaBank => _mediaBank;

    /// <summary>
    /// Banks embedding this media, restricted to the active language. Localised vo
    /// banks share media ids -- 963530753 sits in both the English and Japanese
    /// bnk_vo_1031001 -- and rebuilding the Japanese one for an English mod is exactly
    /// the unnecessary edit this tool is meant to prevent.
    /// </summary>
    public IReadOnlyList<string> BanksForMedia(uint id)
    {
        if (!_mediaBank.TryGetValue(id, out var all)) return [];
        var kept = all.Where(p =>
        {
            var other = Languages.FirstOrDefault(l => p.Contains($"/{l}/", StringComparison.OrdinalIgnoreCase));
            return other is null || other.Equals(Language, StringComparison.OrdinalIgnoreCase);
        }).ToList();
        return kept.Count > 0 ? kept : all;
    }

    /// <summary>
    /// Only DIDX is needed for this, not the HIRC graph — 1,437 banks / 2 GB in about
    /// five seconds, so it runs once in the background after mount rather than being
    /// something the user has to trigger.
    /// </summary>
    public void IndexBanks(Action<string> progress)
    {
        var banks = _bankByPath.Values.ToList();

        var n = 0;
        foreach (var b in banks)
        {
            try
            {
                var raw = b.Read();
                var didx = BnkBuilder.ReadSections(raw).FirstOrDefault(s => s.Tag == "DIDX");
                if (didx is not null)
                    foreach (var m in BnkBuilder.ReadDidx(didx.Body))
                    {
                        if (!_mediaBank.TryGetValue(m.Id, out var list))
                            _mediaBank[m.Id] = list = [];
                        if (!list.Contains(b.Path, StringComparer.OrdinalIgnoreCase))
                            list.Add(b.Path);
                    }
            }
            catch { /* one unreadable bank must not stop the index */ }
            if (++n % 200 == 0) progress($"indexing banks… {n}/{banks.Count}");
        }
        BankIndexReady = true;
        progress($"bank index ready — {_mediaBank.Count:N0} media ids across {banks.Count} banks.");
    }

    private static readonly Regex BankId = new(@"(\d{7})", RegexOptions.Compiled);

    /// <summary>A hero's name in the active language, or the id when the game names it nowhere.</summary>
    public string HeroName(string charId) =>
        Loc($"601_HeroUIAsset_{charId}_ST", $"UIHeroTable_{charId}0_HeroBasic_TName") ??
        Loc($"123_Customize_{charId}_ST", $"MarvelItemTable_{charId}_ItemName") ??
        charId;

    /// <summary>A skin's name in the active language (skin id as in 1024001), or the id.</summary>
    public string SkinName(string charId, string skinId) =>
        Loc($"123_Customize_{charId}_ST", $"MarvelItemTable_{skinId}_ItemName") ??
        Loc($"601_HeroUIAsset_{charId}_ST", $"UISkinTable_{skinId}0_SkinBasic_SkinName") ??
        skinId;

    private void BuildCatalog()
    {
        var bySkin = new Dictionary<string, List<GameFile>>();
        foreach (var (path, file) in _bankByPath)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var m = BankId.Matches(name);
            if (m.Count == 0) continue;
            var skinId = m[^1].Value;

            // A vo bank exists once per shipped language; take only the active one.
            var langFolder = Languages.FirstOrDefault(l =>
                path.Contains($"/{l}/", StringComparison.OrdinalIgnoreCase));
            if (langFolder is not null && !langFolder.Equals(Language, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!bySkin.TryGetValue(skinId, out var list)) bySkin[skinId] = list = [];
            list.Add(file);
        }

        var chars = new List<CharacterEntry>();
        foreach (var group in bySkin.GroupBy(kv => kv.Key[..4]).OrderBy(g => g.Key))
        {
            var charId = group.Key;
            var heroName = HeroName(charId);

            var skins = new List<SkinEntry>();
            foreach (var kv in group.OrderBy(k => k.Key))
            {
                var skinId = kv.Key;
                skins.Add(new SkinEntry(skinId, $"{skinId}  {SkinName(charId, skinId)}", kv.Value));
            }
            chars.Add(new CharacterEntry(charId, $"{charId}  {heroName}", skins));
        }

        Characters = chars.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        BuildOtherBanks();
    }

    /// <summary>
    /// The banks with no skin id, grouped by what they are. Each one becomes its own
    /// entry so the rest of the window -- rows, playback, test and silent builds --
    /// works on them unchanged.
    /// </summary>
    private void BuildOtherBanks()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byGroup = new Dictionary<string, List<GameFile>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, file) in _bankByPath)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (BankId.IsMatch(name)) continue;          // a character's, already listed

            // A vo bank exists once per shipped language; take only the active one.
            var langFolder = Languages.FirstOrDefault(l =>
                path.Contains($"/{l}/", StringComparison.OrdinalIgnoreCase));
            if (langFolder is not null && !langFolder.Equals(Language, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!seen.Add(Path.GetFileName(path))) continue;
            var group = GroupOf(name);
            if (!byGroup.TryGetValue(group, out var list)) byGroup[group] = list = [];
            list.Add(file);
        }

        OtherBanks = byGroup
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CharacterEntry(
                "",
                $"{g.Key}  ({g.Value.Count} bank{(g.Value.Count == 1 ? "" : "s")})",
                g.Value.OrderBy(b => Path.GetFileName(b.Path), StringComparer.OrdinalIgnoreCase)
                       .Select(b => new SkinEntry(
                           Path.GetFileNameWithoutExtension(b.Path),
                           Path.GetFileName(b.Path),
                           [b]))
                       .ToList()))
            .ToList();
    }

    /// <summary>What kind of bank this is, from the name the game gave it.</summary>
    private static string GroupOf(string bankName)
    {
        var n = bankName.ToLowerInvariant();
        // The prefix is the game's own naming, not a word in anyone's language, so it
        // is matched literally rather than through the machine's collation.
        if (n.StartsWith("bnk_", StringComparison.Ordinal)) n = n[4..];
        var cut = n.IndexOf('_');
        var head = cut > 0 ? n[..cut] : n;
        return head switch
        {
            "amb" => "Ambience",
            "mus" or "music" => "Music",
            "ui" => "UI",
            "vo" => "Voice",
            "sfx" => "SFX",
            "map" or "level" or "scene" => "Maps",
            "display" => "Display",
            _ => char.ToUpperInvariant(head[0]) + head[1..],
        };
    }
}
