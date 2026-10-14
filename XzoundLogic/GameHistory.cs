using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MRAudioKit;

/// <summary>
/// Which sound played every media id in one version of the game.
///
/// A mod of loose Media files names each file by nothing but a media id, and a patch can give
/// a sound a new id -- or hand an old id to another line. The mod carries no bank to say which
/// sound a file was, so the only way to bring it onto a later patch is a record of the game it
/// was made for: this. Sound ids hold across patches where media ids do not, so an old id's
/// Sound (or music track) is followed to whatever that Sound plays today.
///
/// Taken from the banks alone, so every install of one version takes the same one, and one can
/// be handed to someone who never had that version.
/// </summary>
public sealed class GameSnapshot
{
    public const byte Sound = 1, Track = 2;

    /// <summary>One thing that plays a medium: a Sound or a music track, in a bank.</summary>
    public readonly record struct Player(int Bank, byte Kind, uint Id);

    /// <summary>The game's own version string ("1.1.3922109"), or null when the install had none.</summary>
    public string Version { get; init; }
    public long Changelist { get; init; }
    /// <summary>The bank set, path and size: what identifies a version when nothing else does.</summary>
    public string Fingerprint { get; init; } = "";
    public DateTime Taken { get; init; }
    /// <summary>Where it came from: this install, or a folder of an older one.</summary>
    public string Source { get; init; } = "";
    public IReadOnlyList<string> Banks { get; init; } = [];
    public IReadOnlyDictionary<uint, Player[]> Media { get; init; } = new Dictionary<uint, Player[]>();

    public string Label => Version is not null ? $"game {Version}" : $"game {Fingerprint[..Math.Min(8, Fingerprint.Length)]}";

    /// <summary>
    /// What plays a medium in banks a file in this language folder serves: banks of that
    /// language and banks with none. A file in the neutral folder serves them all.
    /// </summary>
    public IReadOnlyList<Player> PlayersOf(uint media, string language)
    {
        if (!Media.TryGetValue(media, out var all)) return [];
        if (language is null) return all;
        return all.Where(p => GameSession.LanguageOf(Banks[p.Bank]) is not { } l
                              || l.Equals(language, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    // Each track's media, built when first asked: a track can place several clips.
    private Dictionary<uint, uint[]> _trackMedia;
    public IReadOnlyList<uint> TrackMedia(uint track)
    {
        _trackMedia ??= Media.SelectMany(kv => kv.Value.Where(p => p.Kind == Track).Select(p => (p.Id, kv.Key)))
                             .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Select(x => x.Key).Distinct().ToArray());
        return _trackMedia.TryGetValue(track, out var m) ? m : [];
    }

    // (kind, player id) -> the media it plays and the bank each is played in, built when first asked.
    private Dictionary<(byte, uint), (uint Media, int Bank)[]> _byPlayer;

    /// <summary>
    /// What a Sound or track plays, in the banks a file of this language serves (its own
    /// language's and those with none; all of them for null). A voice Sound plays one medium per
    /// language, so without the language a Sound has several answers and only one is the file's.
    /// </summary>
    public IReadOnlyList<uint> MediaOf(byte kind, uint id, string language = null)
    {
        _byPlayer ??= Media.SelectMany(kv => kv.Value.Select(p => (Key: (p.Kind, p.Id), Media: kv.Key, p.Bank)))
                           .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Select(x => (x.Media, x.Bank)).ToArray());
        if (!_byPlayer.TryGetValue((kind, id), out var all)) return [];
        return all.Where(x => language is null || GameSession.LanguageOf(Banks[x.Bank]) is not { } l ||
                              l.Equals(language, StringComparison.OrdinalIgnoreCase))
                  .Select(x => x.Media).Distinct().ToList();
    }

    private const uint Magic = 0x53485A58;   // "XZHS"
    private const int FormatVersion = 1;

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".part";
        using (var file = File.Create(temp))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
        using (var w = new BinaryWriter(gz, Encoding.UTF8))
        {
            w.Write(Magic); w.Write(FormatVersion);
            w.Write(Version ?? ""); w.Write(Changelist); w.Write(Fingerprint); w.Write(Taken.ToBinary()); w.Write(Source);
            w.Write(Banks.Count);
            foreach (var b in Banks) w.Write(b);
            w.Write(Media.Count);
            foreach (var (id, players) in Media.OrderBy(kv => kv.Key))
            {
                w.Write(id);
                w.Write(players.Length);
                foreach (var p in players) { w.Write(p.Bank); w.Write(p.Kind); w.Write(p.Id); }
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    public static GameSnapshot Load(string path)
    {
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8);
        if (r.ReadUInt32() != Magic || r.ReadInt32() != FormatVersion)
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a game snapshot this version reads.");
        var version = r.ReadString();
        var changelist = r.ReadInt64();
        var fingerprint = r.ReadString();
        var taken = DateTime.FromBinary(r.ReadInt64());
        var source = r.ReadString();
        var banks = new string[r.ReadInt32()];
        for (var i = 0; i < banks.Length; i++) banks[i] = r.ReadString();
        var count = r.ReadInt32();
        var media = new Dictionary<uint, Player[]>(count);
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadUInt32();
            var players = new Player[r.ReadInt32()];
            for (var j = 0; j < players.Length; j++) players[j] = new Player(r.ReadInt32(), r.ReadByte(), r.ReadUInt32());
            media[id] = players;
        }
        return new GameSnapshot
        {
            Version = version.Length == 0 ? null : version, Changelist = changelist, Fingerprint = fingerprint,
            Taken = taken, Source = source, Banks = banks, Media = media,
        };
    }

    /// <summary>The bank set as a short hash: path and size of every bank, in order.</summary>
    public static string FingerprintOf(IEnumerable<(string Path, long Size)> banks)
    {
        var text = string.Join("\n", banks.OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase)
                                          .Select(b => $"{b.Path.ToLowerInvariant()}|{b.Size}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// The game's own version, from the version.json the install keeps beside its content: a few
    /// folders above the Paks folder. Null when there is none (a folder of paks copied elsewhere).
    /// </summary>
    public static (string Version, long Changelist) VersionOf(string paksDir)
    {
        try
        {
            // Only the install's own shape: <root>\Marvel\Content\Paks with <root>\version.json. A copy of
            // an older Paks folder kept anywhere else must not borrow the version of the install above it.
            var paks = string.IsNullOrEmpty(paksDir) ? null : new DirectoryInfo(Path.TrimEndingDirectorySeparator(paksDir));
            if (paks is { Name: var p, Parent: { Name: var c, Parent: { Name: var m, Parent: { } root } } } &&
                p.Equals("Paks", StringComparison.OrdinalIgnoreCase) && c.Equals("Content", StringComparison.OrdinalIgnoreCase) &&
                m.Equals("Marvel", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(root.FullName, "version.json")))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "version.json")));
                var json = doc.RootElement;
                var version = json.TryGetProperty("version", out var v) ? v.GetString() : null;
                var changelist = json.TryGetProperty("changelist", out var cl) && cl.TryGetInt64(out var n) ? n : 0;
                return (version, changelist);
            }
        }
        catch { /* no version is still a snapshot, named by its fingerprint */ }
        return (null, 0);
    }
}

/// <summary>One line of a SoundKit subtitle list: a voice line's media id in that version, its event, its subtitle.</summary>
public sealed record SubsLine(uint Media, string Event, string Subtitle);

/// <summary>
/// One version's voice lines as SoundKit lists them (SUBS-OLD/en-10/AdamWarlock-en.txt and the
/// like): <c>1000312076-vo_10460077_ko_sp_01_10430_play, "Your legend has come to an end, Star-Lord."</c>.
/// The history of voice lines from before anything here took snapshots.
/// </summary>
public sealed class SubsSet
{
    public string Label { get; init; } = "";
    public string Language { get; init; } = "English(US)";
    public IReadOnlyList<SubsLine> Lines { get; init; } = [];
    private ILookup<uint, SubsLine> _byMedia;
    public IReadOnlyList<SubsLine> Of(uint media) => [.. (_byMedia ??= Lines.ToLookup(l => l.Media))[media]];

    // id-event, then an optional quoted subtitle: breath and effort lines have none. A subtitle
    // that runs onto the next line keeps what is on this one.
    private static readonly Regex LineRx = new(@"^(\d+)-([^\s,]+)(?:\s*,\s*""(.*?)""?)?\s*$", RegexOptions.Compiled);

    /// <summary>Every list in a folder of them; the folder's name is the version ("en-10").</summary>
    public static SubsSet Read(string dir)
    {
        var lines = new List<SubsLine>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            foreach (var raw in File.ReadLines(file))
                if (LineRx.Match(raw.Trim().TrimStart('﻿')) is { Success: true } m && uint.TryParse(m.Groups[1].Value, out var id))
                    lines.Add(new SubsLine(id, m.Groups[2].Value, m.Groups[3].Value.Replace("\"\"", "\"")));
        var label = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        return new SubsSet { Label = label, Language = LanguageOf(label), Lines = lines };
    }

    /// <summary>"en-10" is English(US); SoundKit names a list's language by its first part.</summary>
    private static string LanguageOf(string label) => label.Split('-')[0].ToLowerInvariant() switch
    {
        "jp" or "ja" or "jpn" => "Japanese(JP)",
        "cn" or "zh" or "chs" => "Chinese(CN)",
        _ => "English(US)",
    };
}

/// <summary>
/// Everything known about earlier versions of the game: the snapshots taken of them, and the
/// subtitle lists imported from SoundKit. Kept under the app's data folder, plus any shipped
/// beside the program, so a version someone else snapshotted can be handed out with a release.
/// </summary>
public sealed class GameHistory
{
    public static string Dir => Path.Combine(Settings.AppDataDir, "history");
    public static string ShippedDir => Path.Combine(AppContext.BaseDirectory, "history");

    public List<GameSnapshot> Snapshots { get; } = [];
    public List<SubsSet> Subs { get; } = [];
    public List<string> Problems { get; } = [];

    /// <summary>The history in these folders (by default, the app's and the one shipped beside it).</summary>
    public static GameHistory Load(params string[] dirs)
    {
        var history = new GameHistory();
        if (dirs is null || dirs.Length == 0) dirs = [ShippedDir, Dir];
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.xzh"))
            {
                try
                {
                    var s = GameSnapshot.Load(file);
                    if (history.Snapshots.All(x => x.Fingerprint != s.Fingerprint)) history.Snapshots.Add(s);
                }
                catch (Exception ex) { history.Problems.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
            }
            var subs = Path.Combine(dir, "subs");
            if (Directory.Exists(subs))
                foreach (var set in Directory.EnumerateDirectories(subs))
                {
                    try
                    {
                        var s = SubsSet.Read(set);
                        if (s.Lines.Count > 0 && history.Subs.All(x => x.Label != s.Label)) history.Subs.Add(s);
                    }
                    catch (Exception ex) { history.Problems.Add($"{Path.GetFileName(set)}: {ex.Message}"); }
                }
        }
        return history;
    }

    /// <summary>Where a snapshot of this version is kept.</summary>
    public static string PathFor(string version, string fingerprint, string dir = null) =>
        Path.Combine(dir ?? Dir, $"snapshot-{Regex.Replace(version ?? "unversioned", @"[^\w.\-]", "_")}-{fingerprint[..Math.Min(8, fingerprint.Length)]}.xzh");

    /// <summary>
    /// Take a snapshot of the mounted game unless one of this version is already kept. Reads
    /// every bank the first time, so it runs behind everything else. Null when one was kept.
    /// </summary>
    public static GameSnapshot EnsureSnapshot(GameSession session, string source = "this install", string dir = null)
    {
        var fingerprint = session.BankFingerprint();
        var (version, _) = GameSnapshot.VersionOf(session.PaksDir);
        var path = PathFor(version, fingerprint, dir);
        static bool Readable(string f) { try { GameSnapshot.Load(f); return true; } catch { return false; } }
        if (File.Exists(path) && Readable(path)) return null;
        // A shipped snapshot of this version counts as kept too.
        if (Directory.Exists(ShippedDir) && Directory.EnumerateFiles(ShippedDir, $"*-{fingerprint[..8]}.xzh").Any(Readable)) return null;
        var snapshot = session.Snapshot(source);
        // A bank that could not be read leaves a gap every later update would trust: not kept.
        if (session.SnapshotFailures > 0) return null;
        snapshot.Save(path);
        return snapshot;
    }

    /// <summary>
    /// Copy SoundKit's subtitle lists into the history: a SoundKit folder (its SUBS-OLD versions
    /// are found under it), a SUBS-OLD folder, or one version's folder of lists. Returns the
    /// versions copied.
    /// </summary>
    public static List<string> ImportSubs(string folder, string dir = null)
    {
        var target = Path.Combine(dir ?? Dir, "subs");
        var found = new List<string>();
        static bool HasLists(string d) => SubsSet.Read(d).Lines.Count > 0;
        IEnumerable<string> Versions(string d)
        {
            // SoundKit's own folder and its 0-TXTs hold other .txt files (its key, its config): a
            // folder is one version only when its .txt files are subtitle lists.
            if (HasLists(d)) return [d];
            var old = Directory.EnumerateDirectories(d, "SUBS-OLD", SearchOption.AllDirectories).FirstOrDefault();
            return Directory.EnumerateDirectories(old ?? d);
        }
        foreach (var version in Versions(folder))
        {
            if (!HasLists(version)) continue;
            var label = Path.GetFileName(Path.TrimEndingDirectorySeparator(version));
            var into = Directory.CreateDirectory(Path.Combine(target, label)).FullName;
            foreach (var file in Directory.EnumerateFiles(version, "*.txt"))
                File.Copy(file, Path.Combine(into, Path.GetFileName(file)), overwrite: true);
            found.Add(label);
        }
        return found;
    }

    // ---- following a mod's loose files onto the current game -----------------------------

    /// <param name="Verdict">
    /// "same": the sound it was still plays this id. "moved": it now plays <see cref="NewId"/>.
    /// "unverified": no record of what the id was, but it is still one the game plays.
    /// "ambiguous": the records disagree, or its sounds now play different media.
    /// "gone": the sound it was is no longer in the game. "embedded": that sound's audio now
    /// sits whole in a bank, where a loose file is not read. "unknown": nothing knows the id.
    /// </param>
    /// <param name="From">The record that said which sound it was.</param>
    public sealed record Followed(ModBank.LooseMedia File, uint Id, string Verdict, uint NewId, string NewPath,
                                  string Reason, string From);

    /// <param name="MadeFor">The version the files were read as made for, when one was decided.</param>
    /// <param name="Versions">The versions to choose between when the records disagree.</param>
    public sealed record FollowResult(List<Followed> Files, string MadeFor, IReadOnlyList<string> Versions);

    /// <summary>
    /// Where each loose file belongs on the mounted game.
    ///
    /// The version a mod was made for is read from the EARLIER versions recorded -- the one that
    /// knows the most of its ids, or the one named -- and each id's sound in it is followed to
    /// what that sound plays today, in the file's language. Today's own record does not choose
    /// the version: it is a record of the present, so it only checks each id, and an id it gives
    /// to a different sound than the earlier record, when the mod could as well be made for today,
    /// is put to the person rather than decided. An id no earlier version knows is tried against
    /// SoundKit's voice lists by event; one nothing records but the game still plays stays where
    /// it is, unverified.
    /// </summary>
    /// <param name="madeFor">The version the mod was made for: a label, a version string or a bank-set prefix.</param>
    /// <exception cref="ArgumentException">When <paramref name="madeFor"/> names no recorded version.</exception>
    public FollowResult Follow(GameSession session, IReadOnlyList<ModBank.LooseMedia> files, string madeFor = null,
                               Action<string> progress = null)
    {
        progress ??= _ => { };
        // Whether a sound's audio now sits whole in a bank is read from the bank index.
        if (!session.BankIndexReady) { progress("indexing the game's banks..."); session.IndexBanks(_ => { }); }
        var items = files.Select(f => (File: f, Id: ModUpdate.MediaIdOf(f.MountedAt), Lang: GameSession.LanguageOf(f.MountedAt)))
                         .Where(x => x.Id is not null).Select(x => (x.File, Id: x.Id.Value, x.Lang)).ToList();
        progress("reading which sound plays every medium today...");
        var today = session.Snapshot("this install");
        var olds = Snapshots.Where(s => s.Fingerprint != today.Fingerprint).ToList();
        var all = olds.Append(today).ToList();
        string Key(GameSnapshot s) => all.Count(r => r.Label == s.Label) > 1 ? $"{s.Label} ({s.Fingerprint[..Math.Min(8, s.Fingerprint.Length)]})" : s.Label;
        int Coverage(GameSnapshot s) => items.Count(x => s.PlayersOf(x.Id, x.Lang).Count > 0);

        List<GameSnapshot> made;
        var madeToday = false;
        // A SoundKit list's season can be the version named too ("en-8.5"), for the voice lines it records.
        var subsNamed = madeFor is null ? null : Subs.FirstOrDefault(x => x.Label.Equals(madeFor, StringComparison.OrdinalIgnoreCase));
        if (madeFor is not null)
        {
            var named = all.Where(s => Key(s) == madeFor || s.Label == madeFor || s.Version == madeFor ||
                                       (madeFor.Length >= 6 && s.Fingerprint.StartsWith(madeFor, StringComparison.OrdinalIgnoreCase))).ToList();
            if (named.Count == 0 && subsNamed is null)
                throw new ArgumentException($"no recorded version is \"{madeFor}\". The versions are: " +
                                            string.Join(", ", all.Select(s => $"\"{Key(s)}\"").Concat(Subs.Select(x => $"\"{x.Label}\""))));
            madeToday = named.Contains(today);
            made = named.Where(s => s != today).ToList();
        }
        else
        {
            var scored = olds.Select(s => (S: s, N: Coverage(s))).Where(x => x.N > 0).ToList();
            var top = scored.Count == 0 ? 0 : scored.Max(x => x.N);
            made = scored.Where(x => x.N == top).Select(x => x.S).ToList();
        }
        var bestOld = made.Count == 0 ? 0 : made.Max(Coverage);
        var todayCoverage = Coverage(today);
        string madeForLabel = madeToday ? Key(today) : made.Count == 1 ? Key(made[0]) : subsNamed?.Label;

        var lines = new LineFinder(session);
        var subsChosen = ChooseSubs(items.Select(x => (x.Id, x.Lang)).ToList(), subsNamed);
        var results = new List<Followed>();
        var versions = new HashSet<string>();
        static string Sig(IReadOnlyList<GameSnapshot.Player> p) =>
            string.Join(",", p.Select(x => $"{x.Kind}:{x.Id}").Distinct().OrderBy(x => x, StringComparer.Ordinal));

        foreach (var (file, id, lang) in items)
        {
            var todayPlayers = today.PlayersOf(id, lang);
            if (madeToday)
            {
                // Made for this version, the person says: every id means what it means today.
                results.Add(todayPlayers.Count > 0
                    ? Place(session, today, file, id, lang, today, todayPlayers, verified: true, Key(today))
                    : new Followed(file, id, "unknown", 0, null, "the game today does not play this id", Key(today)));
                continue;
            }

            // What the earlier version(s) say played it: the made-for one(s), else any that knows it.
            var answers = Answers(made, id, lang);
            if (answers.Count == 0 && madeFor is null) answers = Answers(olds, id, lang);
            if (answers.Count > 1)
            {
                foreach (var g in answers) foreach (var a in g) versions.Add(Key(a.S));
                results.Add(new Followed(file, id, "ambiguous", 0, null,
                    "the recorded versions disagree about which sound it was: " +
                    string.Join("; ", answers.Select(g => $"{string.Join(", ", g.Select(a => Key(a.S)))}: {Players(g.First().S, g.First().P)}")) +
                    ". Say which version the mod was made for.", null));
                continue;
            }
            if (answers.Count == 1)
            {
                var (s, players) = answers[0].First();
                // Today gives the id to another sound, and the mod could as well be made for today:
                // which is meant is the person's to say.
                if (madeFor is null && todayPlayers.Count > 0 && Sig(todayPlayers) != Sig(players) && todayCoverage >= bestOld)
                {
                    versions.Add(Key(s));
                    versions.Add(Key(today));
                    results.Add(new Followed(file, id, "ambiguous", 0, null,
                        $"in {Key(s)} it was {Players(s, players)}; today it is {Players(today, todayPlayers)}. " +
                        "Say which version the mod was made for.", null));
                    continue;
                }
                results.Add(Place(session, today, file, id, lang, s, players, verified: true, Key(s)));
                continue;
            }

            // No earlier snapshot knows it: a voice line SoundKit's lists know, by its event.
            if (subsChosen.TryGetValue(id, out var said) && said is { Count: > 0 })
            {
                var (followed, seasons) = FromSubs(session, lines, file, id, lang, said);
                versions.UnionWith(seasons);
                results.Add(followed);
                continue;
            }

            // Nothing earlier records it. Still a medium the game plays: kept where the game reads it, unverified.
            if (todayPlayers.Count > 0 || session.LooseMedia(id, lang) is not null)
                results.Add(Destination(session, file, id, lang, id, "unverified",
                    "no record says which sound this id was when the mod was made; the game still plays it, " +
                    "so it stays on this id -- listen to it in place", null));
            else
                results.Add(new Followed(file, id, "unknown", 0, null,
                    "no record knows this id and the game no longer plays it; stage it on the right sound by hand", null));
        }
        // A version is only "the one it was made for" when nothing was left for the person to choose.
        return new FollowResult(results, versions.Count > 0 ? null : madeForLabel, versions.OrderBy(v => v).ToList());
    }

    /// <summary>What these records say played an id, kept apart by what they say.</summary>
    private static List<IGrouping<string, (GameSnapshot S, IReadOnlyList<GameSnapshot.Player> P)>> Answers(
        IEnumerable<GameSnapshot> records, uint id, string lang) =>
        records.Select(s => (S: s, P: s.PlayersOf(id, lang)))
               .Where(a => a.P.Count > 0)
               .GroupBy(a => string.Join(",", a.P.Select(p => $"{p.Kind}:{p.Id}").Distinct().OrderBy(x => x, StringComparer.Ordinal)))
               .ToList();

    private static string Players(GameSnapshot s, IReadOnlyList<GameSnapshot.Player> players) =>
        string.Join(" / ", players.Select(p => $"{(p.Kind == GameSnapshot.Track ? "music track" : "Sound")} {p.Id} in " +
                                               Path.GetFileNameWithoutExtension(s.Banks[p.Bank])).Distinct().Take(3));

    /// <summary>One id's sound in a record, followed to what it plays today in the file's language.</summary>
    private static Followed Place(GameSession session, GameSnapshot today, ModBank.LooseMedia file, uint id, string lang,
                                  GameSnapshot s, IReadOnlyList<GameSnapshot.Player> players, bool verified, string label)
    {
        var said = $"{Players(s, players)} ({label})";
        var now = new HashSet<uint>();
        var unsure = new List<string>();
        foreach (var p in players.DistinctBy(p => (p.Kind, p.Id)))
        {
            // Only the banks this file's language reads: a voice Sound plays one id per language.
            var plays = today.MediaOf(p.Kind, p.Id, lang);
            if (plays.Count == 0) continue;
            if (plays.Contains(id)) { now.Add(id); continue; }
            if (p.Kind == GameSnapshot.Sound && plays.Count == 1) { now.Add(plays[0]); continue; }
            // A track places several clips; which one this was is known only when there is one then and now.
            if (p.Kind == GameSnapshot.Track && s.TrackMedia(p.Id).Count == 1 && plays.Count == 1) { now.Add(plays[0]); continue; }
            unsure.Add(p.Kind == GameSnapshot.Track
                ? $"music track {p.Id} places {plays.Count} clips today, and which of them this was is not recorded"
                : $"Sound {p.Id} plays {plays.Count} media today in this language");
        }
        if (now.Count == 0)
            return new Followed(file, id, unsure.Count > 0 ? "ambiguous" : "gone", 0, null,
                unsure.Count > 0 ? string.Join("; ", unsure) : $"it was {said}, which the game no longer has", label);
        if (now.Count > 1 || unsure.Count > 0)
            return new Followed(file, id, "ambiguous", 0, null,
                $"it was {said}, and those now play different media ({string.Join(", ", now)}" +
                (unsure.Count > 0 ? "; " + string.Join("; ", unsure) : "") + ")", label);
        var newId = now.First();
        return Destination(session, file, id, lang, newId,
            verified ? (newId == id ? "same" : "moved") : "unverified",
            newId == id ? $"{said} still plays it" : $"{said} plays {newId} now", label);
    }

    /// <summary>Where the game reads a medium now, for a file of this language; or why a loose file would not be read.</summary>
    private static Followed Destination(GameSession session, ModBank.LooseMedia file, uint id, string lang, uint newId,
                                        string verdict, string reason, string from)
    {
        var path = session.LooseMedia(newId, lang)?.Path;
        var inBank = session.MediaBank.ContainsKey(newId);
        if (path is not null)
            // A prefetched line: the bank holds its opening and the rest streams from this file,
            // which is how the game ships it and how a Media mod of it always played.
            return new Followed(file, id, verdict, newId, path,
                reason + (inBank ? " (a bank also holds the line's opening, as the game ships it)" : ""), from);
        if (inBank)
            return new Followed(file, id, "embedded", newId, null,
                reason + $", but the game holds {newId} whole in a bank now, where a loose file is not read -- stage it on that sound", from);
        // Streamed, and the game ships no file: written where the sound looks for one.
        return new Followed(file, id, verdict, newId, MediaLayout.PathFor(newId, lang), reason, from);
    }

    /// <summary>
    /// For each id, the SoundKit lines of the version(s) that know the most of the mod's ids,
    /// in the file's language; or, for an id those do not have, any list that has it.
    /// </summary>
    private Dictionary<uint, List<(SubsSet Set, SubsLine Line)>> ChooseSubs(List<(uint Id, string Lang)> items, SubsSet named = null)
    {
        var found = new Dictionary<uint, List<(SubsSet, SubsLine)>>();
        foreach (var group in items.GroupBy(x => x.Lang ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var sets = (named is not null ? [named] : Subs)
                .Where(s => group.Key.Length == 0 || s.Language.Equals(group.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            var scored = sets.Select(s => (S: s, N: group.Count(x => s.Of(x.Id).Count > 0))).ToList();
            var best = scored.Count == 0 ? 0 : scored.Max(x => x.N);
            if (best == 0) continue;
            var chosen = scored.Where(x => x.N == best).Select(x => x.S).ToList();
            foreach (var (id, _) in group)
            {
                var lines = chosen.SelectMany(s => s.Of(id).Select(l => (s, l))).ToList();
                if (lines.Count == 0) lines = sets.SelectMany(s => s.Of(id).Select(l => (s, l))).ToList();
                if (lines.Count > 0) found[id] = lines;
            }
        }
        return found;
    }

    /// <summary>
    /// A voice line by the event SoundKit's lists name for its id, followed to what that event
    /// plays today. Several events for one id are fine when they all play the same thing today.
    /// </summary>
    /// <returns>The placement, and the seasons to choose between when the lists disagree.</returns>
    private static (Followed, IReadOnlyList<string>) FromSubs(GameSession session, LineFinder lines, ModBank.LooseMedia file, uint id,
                                                              string lang, List<(SubsSet Set, SubsLine Line)> said)
    {
        var from = $"SoundKit {string.Join(", ", said.Select(x => x.Set.Label).Distinct())}";
        var language = lang ?? said[0].Set.Language;
        var plays = said.GroupBy(x => x.Line.Event, StringComparer.OrdinalIgnoreCase)
                        .Select(g => (Event: g.Key, Seasons: g.Select(x => x.Set.Label).Distinct().ToList(),
                                      Subtitle: g.First().Line.Subtitle, Media: lines.MediaOf(g.Key, language)))
                        .ToList();
        if (plays.Select(p => string.Join(",", p.Media)).Distinct().Count() > 1)
            return (new Followed(file, id, "ambiguous", 0, null,
                "SoundKit's lists name events for it that play different things today: " +
                string.Join("; ", plays.Select(p => $"{string.Join(", ", p.Seasons)}: {p.Event} plays " +
                                                    (p.Media.Count == 0 ? "nothing" : string.Join(", ", p.Media)))) +
                ". Say which season the mod was made for.", from),
                plays.SelectMany(p => p.Seasons).Distinct().ToList());
        var line = plays[0];
        var described = line.Event + (string.IsNullOrEmpty(line.Subtitle) ? "" : $" (\"{line.Subtitle}\")");
        if (line.Media.Count == 0)
            return (new Followed(file, id, "gone", 0, null, $"it was {described}, which the game no longer plays", from), []);
        if (line.Media.Count > 1 && !line.Media.Contains(id))
            return (new Followed(file, id, "ambiguous", 0, null,
                $"it was {described}, which now has {line.Media.Count} takes -- which one this was is not recorded", from), []);
        var newId = line.Media.Contains(id) ? id : line.Media[0];
        var now = session.VoiceLine(line.Event)?.Subtitle;
        var reason = newId == id ? $"{line.Event} still plays it" : $"{line.Event} plays {newId} now";
        if (!string.IsNullOrEmpty(now) && !string.IsNullOrEmpty(line.Subtitle) && now != line.Subtitle)
            reason += $" (its subtitle now reads \"{now}\")";
        return (Destination(session, file, id, lang, newId, newId == id ? "same" : "moved", reason, from), []);
    }

    /// <summary>
    /// A voice event's media today: the event is looked up in the voice banks of the hero its
    /// name carries (vo_1046..., hero 1046), in the language asked for, and every Sound a Play
    /// action of it reaches gives one. Each bank is parsed once.
    /// </summary>
    private sealed class LineFinder(GameSession session)
    {
        private readonly Dictionary<string, ModUpdate.Lines> _parsed = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<uint> MediaOf(string eventName, string language)
        {
            var hero = Regex.Match(eventName, @"vo_(\d{4})", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
            if (hero is null) return [];
            var hash = VoiceLine.Fnv(eventName);
            var media = new HashSet<uint>();
            foreach (var path in session.BankPaths.Where(p =>
                         Path.GetFileName(p).Contains("vo_" + hero, StringComparison.OrdinalIgnoreCase) &&
                         (GameSession.LanguageOf(p) is not { } l || l.Equals(language, StringComparison.OrdinalIgnoreCase))))
            {
                if (!_parsed.TryGetValue(path, out var bank))
                    _parsed[path] = bank = ModUpdate.Lines.Of(session.Bank(path).Read(), path);
                // Play actions only: a Stop or a Seek naming a container would bring in other lines.
                if (!bank.PlaySounds.TryGetValue(hash, out var sounds)) continue;
                foreach (var sound in sounds)
                    if (bank.SoundMedia.TryGetValue(sound, out var id) && id != 0) media.Add(id);
            }
            return media.OrderBy(x => x).ToList();
        }
    }
}
