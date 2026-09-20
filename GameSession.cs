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

    public void Mount(Settings s, Action<string> progress)
    {
        Generation++;
        Language = s.Language;

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
            if (rest.Length == 3) _mediaByLangId[$"{rest[0]}|{id}"] = file;
            else _mediaNeutral[id] = file;
        }
    }

    private void IndexEventNames()
    {
        foreach (var path in Provider.Files.Keys)
        {
            if (path.IndexOf("/WwiseEvents/", StringComparison.OrdinalIgnoreCase) < 0) continue;
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
        var banks = Provider.Files
            .Where(kv => kv.Key.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();

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

    private void BuildCatalog()
    {
        var bySkin = new Dictionary<string, List<GameFile>>();
        foreach (var (path, file) in Provider.Files)
        {
            if (!path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)) continue;
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
            // The aggregated provider can surface the same bank path more than once
            // (base pak + patch pak). Parsing it twice doubles the load for nothing.
            if (!list.Any(b => b.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)))
                list.Add(file);
        }

        var chars = new List<CharacterEntry>();
        foreach (var group in bySkin.GroupBy(kv => kv.Key[..4]).OrderBy(g => g.Key))
        {
            var charId = group.Key;
            var heroName =
                Loc($"601_HeroUIAsset_{charId}_ST", $"UIHeroTable_{charId}0_HeroBasic_TName") ??
                Loc($"123_Customize_{charId}_ST", $"MarvelItemTable_{charId}_ItemName") ??
                charId;

            var skins = new List<SkinEntry>();
            foreach (var kv in group.OrderBy(k => k.Key))
            {
                var skinId = kv.Key;
                var label =
                    Loc($"123_Customize_{charId}_ST", $"MarvelItemTable_{skinId}_ItemName") ??
                    Loc($"601_HeroUIAsset_{charId}_ST", $"UISkinTable_{skinId}0_SkinBasic_SkinName") ??
                    skinId;
                skins.Add(new SkinEntry(skinId, $"{skinId}  {label}", kv.Value));
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

        foreach (var (path, file) in Provider.Files)
        {
            if (!path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)) continue;
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
        if (n.StartsWith("bnk_")) n = n[4..];
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
