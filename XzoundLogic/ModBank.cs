using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;

namespace MRAudioKit;

/// <summary>
/// Opens somebody else's soundbank for inspection: a loose .bnk, or the banks inside
/// a mod .pak.
///
/// The rows are built from the bank's own media list, then enriched from the game:
/// a mod reuses the shipped media ids, so the vanilla index still knows the event
/// name and the subtitle line for each one. That is what makes a stranger's bank
/// readable instead of a list of numbers.
/// </summary>
public static class ModBank
{
    public sealed record Bank(string Name, string SourcePath, byte[] Bytes)
    {
        public string Label => string.IsNullOrEmpty(SourcePath) || SourcePath == Name
            ? Name : $"{Name}  ({Path.GetFileName(SourcePath)})";

        /// <summary>
        /// Where the bank sits inside the mod, language folder included, or null for a
        /// loose .bnk that never said. A voice bank ships once per language under the same
        /// file name, so the name alone cannot say which of the game's copies a mod
        /// replaces, and comparing an English bank with the Chinese one makes every voice
        /// line look different.
        /// </summary>
        public string MountedAt { get; init; }
    }

    /// <summary>A loose media file a mod ships under WwiseAudio/Media/, by its path in the mod.</summary>
    public sealed record LooseMedia(string MountedAt, byte[] Bytes);

    /// <summary>
    /// What came out of an open. <see cref="Skipped"/> names containers that could
    /// not be read -- an encrypted mod among fifteen good ones must not sink the
    /// whole folder, but the author still needs to know it was left out.
    /// </summary>
    public sealed record Opened(List<Bank> Banks, List<string> Skipped)
    {
        /// <summary>
        /// The loose media the mod ships beside its banks. Filled only when the caller
        /// asks for it, because a mod can carry hundreds of megabytes of them and the
        /// window, which opens a mod to read its banks, has no use for any of it.
        /// </summary>
        public List<LooseMedia> Media { get; init; } = [];

        /// <summary>
        /// What a container holds besides banks and (when asked for) Media wems -- cooked assets,
        /// say. Anything that rewrites the mod as a whole from its audio alone would drop these.
        /// </summary>
        public List<string> Others { get; init; } = [];
    }

    public static bool IsBank(string p) => p.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase);
    public static bool IsPak(string p) =>
        p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
        p.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase);
    public static bool CanOpen(string p) => IsBank(p) || IsPak(p);

    /// <summary>
    /// Every bank reachable from this path. A .bnk is itself; a .pak is mounted and
    /// searched. Throws with a message worth showing when nothing can be read.
    /// </summary>
    /// <param name="withMedia">
    /// Also read the loose .wem files the mod ships under WwiseAudio/Media/. A mod built
    /// in prefetch mode, or one that replaces a sound the game only streams, carries its
    /// audio there rather than in a bank, and anything that rewrites the mod as a whole
    /// would otherwise drop that half of it.
    /// </param>
    public static Opened Open(string path, Settings settings, bool withMedia = false)
    {
        if (IsBank(path))
            return new Opened([new Bank(Path.GetFileName(path), path, File.ReadAllBytes(path))], []);

        // A folder of mod files is read where it sits -- nothing to copy, and it is
        // how an unpacked mod usually arrives.
        if (Directory.Exists(path))
            return FromFolder(path, settings, withMedia);

        if (!IsPak(path))
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a .bnk or .pak");

        // Mount the pak ALONE. Pointing the provider at the file's own folder would
        // pull in every other mod sitting next to it, and the author would be looking
        // at banks they did not open.
        var temp = Path.Combine(Path.GetTempPath(), "XzoundWave", "modpak_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            var stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
                                    Path.GetFileNameWithoutExtension(path));
            // .pak alone for legacy mods; IoStore mods need their .ucas/.utoc siblings.
            foreach (var ext in new[] { ".pak", ".ucas", ".utoc", ".sig" })
            {
                var src = stem + ext;
                if (File.Exists(src)) Link(src, Path.Combine(temp, Path.GetFileName(src)));
            }

            // No global container is copied. This game's global.utoc is encrypted and
            // does not mount even when the game's own Paks folder is opened, yet its
            // 39 IoStore containers mount regardless -- so it is not needed, and
            // linking it only produced a "skipped" warning on every open.

            return FromMount(temp, path, settings, withMedia);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    /// <summary>
    /// A folder of mod files: every loose bank in it, and the loose media under a WwiseAudio/Media
    /// folder when asked for, at any depth, read from disk; and the containers at its top, mounted
    /// as a pak is.
    ///
    /// The provider names a loose file by the folder it was found in, so a bank at the top of a
    /// folder called S1_single_bare was said to sit at S1_single_bare/bnk_sfx_1053303.bnk, a path
    /// the game does not have, and it looked only at the top of the folder, so one laid out the way
    /// the game lays its files out mounted nothing at all. So loose files are read here instead. One
    /// under a Marvel/Content folder sits where the game would load it from, and that is its path,
    /// as a pak would name it; one anywhere else never said where it goes, the same as a bare .bnk,
    /// and is matched the way one is.
    /// </summary>
    private static Opened FromFolder(string dir, Settings settings, bool withMedia)
    {
        var banks = new List<Bank>();
        var media = new List<LooseMedia>();
        var others = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var at = GamePathOf(dir, file);
            if (IsBank(file))
                banks.Add(new Bank(Path.GetFileName(file), dir, File.ReadAllBytes(file)) { MountedAt = at });
            else if (withMedia && at is not null && file.EndsWith(".wem", StringComparison.OrdinalIgnoreCase) &&
                     at.Contains("/WwiseAudio/Media/", StringComparison.OrdinalIgnoreCase))
                media.Add(new LooseMedia(at, File.ReadAllBytes(file)));
        }

        // The containers at the top of the folder, mounted where they sit, as they always were. A
        // container that does not open is named, and is only fatal when nothing else was found.
        var skipped = new List<string>();
        if (Directory.EnumerateFiles(dir).Any(IsPak))
        {
            var mounted = Mount(dir, dir, settings, withMedia);
            if (mounted.Problem is not null && banks.Count == 0 && media.Count == 0)
                throw new InvalidDataException(mounted.Problem);
            // The folder a command-line build writes holds its mod twice: unpacked under Marvel/,
            // and packed into the .pak beside it. A file a container ships at the very path, and
            // with the very bytes, of one already read from the folder is that file again, and
            // taken twice it made an update carry every sound twice and a merge find a mod
            // colliding with itself. Different bytes at one path are two files and both are kept.
            bool Again(string at, byte[] bytes, IEnumerable<(string At, byte[] Bytes)> read) =>
                at is not null && read.Any(r => at.Equals(r.At, StringComparison.OrdinalIgnoreCase) && bytes.AsSpan().SequenceEqual(r.Bytes));
            var readBanks = banks.Select(b => (b.MountedAt, b.Bytes)).ToList();
            var readMedia = media.Select(m => (m.MountedAt, m.Bytes)).ToList();
            banks.AddRange(mounted.Banks.Where(b => !Again(b.MountedAt, b.Bytes, readBanks)));
            media.AddRange(mounted.Media.Where(m => !Again(m.MountedAt, m.Bytes, readMedia)));
            skipped.AddRange(mounted.Skipped);
            others.AddRange(mounted.Others);
        }

        if (banks.Count == 0 && media.Count == 0)
            throw new InvalidDataException(
                $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(dir))} holds no .bnk files, loose or in a container" +
                (withMedia ? ", and no .wem files under a WwiseAudio/Media folder" : "") + "." +
                (skipped.Count > 0
                    ? $"{Environment.NewLine}{skipped.Count} container(s) could not be opened and " +
                      $"were skipped: {string.Join(", ", skipped)}"
                    : ""));

        return new Opened(banks.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                               .ThenBy(b => b.MountedAt, StringComparer.OrdinalIgnoreCase).ToList(), skipped)
        {
            Others = others,
            Media = media.OrderBy(m => m.MountedAt, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>
    /// Where a loose file of a mod folder sits in the game: its path from a Marvel/Content folder
    /// on, the way a pak names it, or null when it sits under none.
    /// </summary>
    private static string GamePathOf(string root, string file)
    {
        var parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i + 1 < parts.Length; i++)
            if (parts[i].Equals("Marvel", StringComparison.OrdinalIgnoreCase) &&
                parts[i + 1].Equals("Content", StringComparison.OrdinalIgnoreCase))
                return string.Join('/', parts[i..]);
        return null;
    }

    /// <summary>Mount a folder of containers and collect every .bnk in it.</summary>
    private static Opened FromMount(string dir, string sourceLabel, Settings settings,
                                    bool withMedia = false)
    {
        var mounted = Mount(dir, sourceLabel, settings, withMedia);
        if (mounted.Problem is not null) throw new InvalidDataException(mounted.Problem);

        // A mod made only of loose media is still a mod when the caller asked for media,
        // so it is refused only when there is nothing of either kind.
        if (mounted.Banks.Count == 0 && mounted.Media.Count == 0)
            throw new InvalidDataException(
                $"{Path.GetFileName(sourceLabel)} holds no .bnk files " +
                $"({mounted.Files:N0} files mounted)." +
                (mounted.Skipped.Count > 0
                    ? $"{Environment.NewLine}{mounted.Skipped.Count} container(s) could not be opened and " +
                      $"were skipped: {string.Join(", ", mounted.Skipped)}"
                    : ""));

        return new Opened(mounted.Banks.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(), mounted.Skipped)
        {
            Media = mounted.Media.OrderBy(m => m.MountedAt, StringComparer.OrdinalIgnoreCase).ToList(),
            Others = mounted.Others,
        };
    }

    /// <summary>
    /// What the containers in a folder hold. Only what is inside a container is taken: the
    /// provider also registers the loose files beside them, which <see cref="FromFolder"/> reads
    /// from disk with the path the game would give them.
    /// </summary>
    /// <param name="Problem">Why nothing at all could be mounted, in words worth showing; null when something was.</param>
    private sealed record Mounted(List<Bank> Banks, List<LooseMedia> Media, List<string> Skipped, int Files, string Problem)
    {
        public List<string> Others { get; init; } = [];
    }

    private static Mounted Mount(string dir, string sourceLabel, Settings settings, bool withMedia)
    {
        var provider = new DefaultFileProvider(dir, SearchOption.TopDirectoryOnly,
            new VersionContainer(EGame.GAME_MarvelRivals), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();

        var keyNote = "";
        if (!string.IsNullOrWhiteSpace(settings?.AesKey))
        {
            try { provider.SubmitKey(new FGuid(), new FAesKey(settings.AesKey)); }
            catch (Exception ex) { keyNote = $"{Environment.NewLine}AES key rejected: {ex.Message}"; }
        }
        else keyNote = $"{Environment.NewLine}No AES key set, so encrypted containers stay closed.";

        // Containers we could not open are skipped, not fatal. A folder of mods where
        // one is encrypted should still show the other fourteen.
        var skipped = provider.UnloadedVfs
            .Select(v => v.Name + (v.IsEncrypted ? " (encrypted)" : ""))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Only what a container holds. The loose files beside the containers are registered as
        // well, named by the folder they sit in rather than where the game would load them, and
        // the folder reader takes those from disk with the right path.
        var contained = provider.Files.Where(kv => kv.Value is VfsEntry).ToList();
        if (contained.Count == 0)
        {
            // Nothing at all opened -- say which containers were seen and why they
            // refused, rather than a flat "0 files" that gives nothing to act on.
            var present = string.Join(", ", Directory.EnumerateFiles(dir).Select(Path.GetFileName));
            return new Mounted([], [], skipped, 0,
                $"{Path.GetFileName(sourceLabel)} mounted 0 files." +
                (skipped.Count > 0
                    ? $"{Environment.NewLine}Could not open: {string.Join(", ", skipped)}"
                    : "") +
                (string.IsNullOrEmpty(present) ? "" : $"{Environment.NewLine}Files present: {present}") +
                keyNote);
        }

        var banks = new List<Bank>();
        var media = new List<LooseMedia>();
        var others = new List<string>();
        foreach (var (key, file) in contained)
        {
            if (withMedia && key.EndsWith(".wem", StringComparison.OrdinalIgnoreCase) &&
                file.Path.Contains("/WwiseAudio/Media/", StringComparison.OrdinalIgnoreCase))
            {
                try { media.Add(new LooseMedia(file.Path, file.Read())); }
                catch { }
                continue;
            }
            if (!key.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)) { others.Add(file.Path); continue; }
            // The file's own path rather than the key, which is what keeps the casing the
            // game uses and so compares cleanly against the game's paths.
            try { banks.Add(new Bank(Path.GetFileName(key), sourceLabel, file.Read()) { MountedAt = file.Path }); }
            catch { }
        }
        return new Mounted(banks, media, skipped, contained.Count, null) { Others = others };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string to, string from, IntPtr reserved);

    /// <summary>
    /// Hard-link when we can — a mod pak can be hundreds of megabytes and copying it
    /// just to read a bank is a needless wait.
    ///
    /// The call is Windows' own, and off Windows it throws rather than returning false,
    /// so the copy underneath it would never have run. A symbolic link buys the same
    /// thing everywhere else; if neither works, copying always does.
    /// </summary>
    private static void Link(string from, string to)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (CreateHardLinkW(to, from, IntPtr.Zero)) return;
            }
            else
            {
                File.CreateSymbolicLink(to, Path.GetFullPath(from));
                return;
            }
        }
        catch { /* a link we cannot make is not a failure, only a slower path */ }

        File.Copy(from, to, true);
    }

    // ---- rows --------------------------------------------------------------

    /// <summary>
    /// Turn a bank into the same row model the rest of the window uses, so playback,
    /// export, durations, filtering and notes all work on it unchanged.
    /// </summary>
    public static SkinSounds ToSounds(GameSession session, Bank bank)
    {
        var result = new SkinSounds();
        var sections = BnkBuilder.ReadSections(bank.Bytes);
        var didx = sections.FirstOrDefault(s => s.Tag == "DIDX");
        var data = sections.FirstOrDefault(s => s.Tag == "DATA");
        if (didx is null || data is null) return result;

        var stem = Path.GetFileNameWithoutExtension(bank.Name);
        var ids = BnkBuilder.ReadDidx(didx.Body).Select(x => x.Id).ToHashSet();
        var vanilla = VanillaMedia(session, bank.Name, ids);

        foreach (var e in BnkBuilder.ReadDidx(didx.Body))
        {
            var wem = data.Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();
            var info = BnkBuilder.Inspect(wem);
            var seconds = BnkBuilder.Seconds(wem);

            // What this bank changed. Sounds it left alone are not interesting; these
            // are the reason someone opened it.
            var tag = vanilla is null ? null
                : !vanilla.TryGetValue(e.Id, out var stock) ? "NEW"
                : !stock.AsSpan().SequenceEqual(wem) ? "MODDED"
                : "";

            // A sound the mod left alone is the game's own PREFETCH STUB when the real
            // audio streams from Media/ -- a few hundred bytes of the opening moment.
            // Its length is not the sound's length (0.24 s where the line runs ten),
            // and its size is not the sound's size. Say nothing rather than something
            // wrong: Measure durations reads the streamed file and fills it in.
            var streamed = tag == "" ? session?.LooseMedia(e.Id) : null;
            var isStub = streamed is not null && streamed.Size > e.Size;

            // The vanilla bank of the same name is the best source of names: a mod
            // keeps the media ids, so whatever the game calls this sound still applies.
            var known = Vanilla(session, stem, e.Id);

            result.Rows.Add(new SoundRow
            {
                Kind = Kind(stem),
                Bank = stem,
                EventHash = known?.EventHash ?? 0,
                EventName = known?.EventName,
                MediaId = e.Id,
                Codec = info.Codec,
                Bytes = isStub ? (uint)streamed.Size : e.Size,
                Seconds = isStub ? null : seconds,
                // What the game applies belongs to the .bnk's structure, which a mod
                // that only swapped audio has not touched -- so it still applies, and
                // is exactly what the author needs to see before swapping.
                Applied = known?.Applied,
                Category = known?.Category ?? "",
                Subtitle = known?.Subtitle ?? "",
                Origin = isStub ? streamed.Path : bank.Name,
                ModTag = tag,
            });
            result.Raw[e.Id] = wem;
            // The FILE NAME, which is what SoundIndex stores and what every reader
            // compares against -- the node's media count, and the .bnk a staged file
            // says it lands in. A stem here matched none of them.
            result.BankOfMedia[e.Id] = bank.Name;
        }
        return result;
    }

    /// <summary>
    /// The shipped copy of this bank, as media id -> bytes, or null when the game
    /// has no counterpart.
    ///
    /// Candidates are chosen by how many media ids they share with the opened bank,
    /// not by which folder they sit in. Matching on the language folder is what makes
    /// soundKit send Chinese and Japanese banks to the English one and mismatch
    /// everything; overlap does not care what a file is called or where it lives.
    /// </summary>
    public static Dictionary<uint, byte[]> VanillaMedia(GameSession session, string bankName,
                                                        HashSet<uint> ids) =>
        VanillaMedia(session, bankName, ids, out _);

    public static Dictionary<uint, byte[]> VanillaMedia(GameSession session, string bankName,
                                                        HashSet<uint> ids, out string chosenPath)
    {
        chosenPath = null;
        if (session?.Provider is null || ids.Count == 0) return null;

        var candidates = session.Provider.Files.Keys
            .Where(k => k.EndsWith("/" + bankName, StringComparison.OrdinalIgnoreCase) ||
                        k.Equals(bankName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return null;

        string best = null;
        var bestOverlap = -1;
        foreach (var key in candidates)
        {
            try
            {
                var didx = BnkBuilder.ReadSections(session.Bank(key).Read())
                                     .FirstOrDefault(s => s.Tag == "DIDX");
                if (didx is null) continue;
                var overlap = BnkBuilder.ReadDidx(didx.Body).Count(e => ids.Contains(e.Id));
                if (overlap > bestOverlap) { bestOverlap = overlap; best = key; }
            }
            catch { }
        }
        if (best is null || bestOverlap <= 0) return null;
        chosenPath = best;

        try
        {
            var raw = session.Bank(best).Read();
            var sections = BnkBuilder.ReadSections(raw);
            var didx = sections.FirstOrDefault(s => s.Tag == "DIDX");
            var data = sections.FirstOrDefault(s => s.Tag == "DATA");
            if (didx is null || data is null) return null;

            var map = new Dictionary<uint, byte[]>();
            foreach (var e in BnkBuilder.ReadDidx(didx.Body))
                map[e.Id] = data.Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();
            return map;
        }
        catch { return null; }
    }

    /// <summary>What a bank changed: its own bytes for each entry, and the shipped
    /// bytes to compare against.</summary>
    public sealed record Comparison(Dictionary<uint, byte[]> Changed,
                                    Dictionary<uint, byte[]> Shipped,
                                    SkinSounds Rows, string BankName, string ShippedPath);

    /// <summary>
    /// Isolate the entries a bank actually replaced. Everything the mod left alone is
    /// dropped, which is the whole point: a mod's own work is a few dozen sounds
    /// inside a bank of hundreds.
    /// </summary>
    public static Comparison Compare(GameSession session, Bank bank)
    {
        var rows = ToSounds(session, bank);
        var ids = rows.Rows.Select(r => r.MediaId).ToHashSet();
        var shipped = VanillaMedia(session, bank.Name, ids, out var shippedPath) ?? [];

        var changed = new Dictionary<uint, byte[]>();
        foreach (var r in rows.Rows)
        {
            if (r.ModTag is not ("MODDED" or "NEW")) continue;
            if (rows.Raw.TryGetValue(r.MediaId, out var bytes)) changed[r.MediaId] = bytes;
        }
        return new Comparison(changed, shipped, rows, bank.Name, shippedPath);
    }

    private static string Kind(string stem) =>
        stem.Contains("_vo_", StringComparison.OrdinalIgnoreCase) ? "VO"
        : stem.Contains("_sfx_", StringComparison.OrdinalIgnoreCase) ? "SFX"
        : stem.Contains("_display", StringComparison.OrdinalIgnoreCase) ? "DISPLAY" : "";

    /// <summary>
    /// The shipped row for this media id. A bank is named after the skin it belongs
    /// to (bnk_vo_1014001.bnk -> 1014001), so only that one skin has to be indexed --
    /// walking every hero to name one bank would cost seconds for nothing.
    /// </summary>
    private static SoundRow Vanilla(GameSession session, string stem, uint mediaId)
    {
        if (session is null) return null;
        var index = VanillaIndex(session, stem);
        return index?.GetValueOrDefault(mediaId);
    }

    private static GameSession _indexedFor;
    private static string _indexedStem;
    private static int _indexedGeneration = -1;
    private static Dictionary<uint, SoundRow> _index;

    private static Dictionary<uint, SoundRow> VanillaIndex(GameSession session, string stem)
    {
        // Generation, not just identity: the window mounts one session in place, so a
        // lookup made before the game was loaded would otherwise cache an empty index
        // against that same object and every later lookup would match the key and get
        // the empty answer back -- for ever, with the diff beside it working fine.
        if (ReferenceEquals(_indexedFor, session) && _indexedStem == stem
            && _indexedGeneration == (session?.Generation ?? -1)) return _index;

        _indexedFor = session;
        _indexedStem = stem;
        _indexedGeneration = session?.Generation ?? -1;
        _index = [];

        var m = Regex.Match(stem, @"(\d{7})");
        var skinId = m.Success ? m.Groups[1].Value : "";
        var charId = m.Success ? skinId[..4] : "";

        SkinEntry skin;
        if (m.Success)
        {
            var ch = session.Characters.FirstOrDefault(c => c.CharId == charId);
            skin = ch?.Skins.FirstOrDefault(s => s.SkinId == skinId) ?? ch?.Skins.FirstOrDefault();
        }
        else
        {
            // A bank belonging to no character is catalogued under its own file name, so
            // the stem IS the entry to index. Requiring a seven-digit skin id left every
            // music, ambience, UI and map .bnk someone opened as a list of numbers, with
            // the same bank's rows named the moment it was browsed instead.
            skin = session.OtherBanks.SelectMany(g => g.Skins)
                          .FirstOrDefault(s => s.SkinId.Equals(stem, StringComparison.OrdinalIgnoreCase));
        }
        if (skin is null) return _index;

        try
        {
            foreach (var r in SoundIndex.Build(session, skin, charId, _ => { }).Rows)
                _index.TryAdd(r.MediaId, r);
        }
        catch { }
        return _index;
    }

}
