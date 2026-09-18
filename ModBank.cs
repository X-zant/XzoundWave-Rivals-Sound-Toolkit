using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

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
    }

    /// <summary>
    /// What came out of an open. <see cref="Skipped"/> names containers that could
    /// not be read -- an encrypted mod among fifteen good ones must not sink the
    /// whole folder, but the author still needs to know it was left out.
    /// </summary>
    public sealed record Opened(List<Bank> Banks, List<string> Skipped);

    public static bool IsBank(string p) => p.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase);
    public static bool IsPak(string p) =>
        p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
        p.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase);
    public static bool CanOpen(string p) => IsBank(p) || IsPak(p);

    /// <summary>
    /// Every bank reachable from this path. A .bnk is itself; a .pak is mounted and
    /// searched. Throws with a message worth showing when nothing can be read.
    /// </summary>
    public static Opened Open(string path, Settings settings)
    {
        if (IsBank(path))
            return new Opened([new Bank(Path.GetFileName(path), path, File.ReadAllBytes(path))], []);

        // A folder of mod files is mounted where it sits -- nothing to copy, and it is
        // how an unpacked mod usually arrives.
        if (Directory.Exists(path))
            return FromMount(path, path, settings);

        if (!IsPak(path))
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a .bnk or .pak");

        // Mount the pak ALONE. Pointing the provider at the file's own folder would
        // pull in every other mod sitting next to it, and the author would be looking
        // at banks they did not open.
        var temp = Path.Combine(Path.GetTempPath(), "MRAudioKit", "modpak_" + Guid.NewGuid().ToString("N")[..8]);
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

            return FromMount(temp, path, settings);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    /// <summary>Mount a folder of containers and collect every .bnk in it.</summary>
    private static Opened FromMount(string dir, string sourceLabel, Settings settings)
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

        if (provider.Files.Count == 0)
        {
            // Nothing at all opened -- say which containers were seen and why they
            // refused, rather than a flat "0 files" that gives nothing to act on.
            var present = string.Join(", ", Directory.EnumerateFiles(dir).Select(Path.GetFileName));
            throw new InvalidDataException(
                $"{Path.GetFileName(sourceLabel)} mounted 0 files." +
                (skipped.Count > 0
                    ? $"{Environment.NewLine}Could not open: {string.Join(", ", skipped)}"
                    : "") +
                (string.IsNullOrEmpty(present) ? "" : $"{Environment.NewLine}Files present: {present}") +
                keyNote);
        }

        var banks = new List<Bank>();
        foreach (var (key, file) in provider.Files)
        {
            if (!key.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)) continue;
            try { banks.Add(new Bank(Path.GetFileName(key), sourceLabel, file.Read())); }
            catch { }
        }
        if (banks.Count == 0)
            throw new InvalidDataException(
                $"{Path.GetFileName(sourceLabel)} holds no .bnk files " +
                $"({provider.Files.Count:N0} files mounted)." +
                (skipped.Count > 0
                    ? $"{Environment.NewLine}{skipped.Count} container(s) could not be opened and " +
                      $"were skipped: {string.Join(", ", skipped)}"
                    : ""));

        return new Opened(banks.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(), skipped);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string to, string from, IntPtr reserved);

    /// <summary>Hard-link when we can — a mod pak can be hundreds of megabytes and
    /// copying it just to read a bank is a needless wait.</summary>
    private static void Link(string from, string to)
    {
        if (CreateHardLinkW(to, from, IntPtr.Zero)) return;
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
            var seconds = Seconds(wem, info);

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
                Bytes = e.Size,
                Seconds = seconds,
                Category = known?.Category ?? "",
                Subtitle = known?.Subtitle ?? "",
                Origin = bank.Name,
                // What this bank changed. Sounds it left alone are not interesting;
                // these are the reason someone opened it.
                ModTag = vanilla is null ? null
                    : !vanilla.TryGetValue(e.Id, out var stock) ? "NEW"
                    : !stock.AsSpan().SequenceEqual(wem) ? "MODDED"
                    : "",
            });
            result.Raw[e.Id] = wem;
            result.BankOfMedia[e.Id] = stem;
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
                var didx = BnkBuilder.ReadSections(session.Provider.Files[key].Read())
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
            var raw = session.Provider.Files[best].Read();
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
    private static Dictionary<uint, SoundRow> _index;

    private static Dictionary<uint, SoundRow> VanillaIndex(GameSession session, string stem)
    {
        if (ReferenceEquals(_indexedFor, session) && _indexedStem == stem) return _index;

        _indexedFor = session;
        _indexedStem = stem;
        _index = [];

        var m = Regex.Match(stem, @"(\d{7})");
        if (!m.Success) return _index;
        var skinId = m.Groups[1].Value;
        var charId = skinId[..4];

        var ch = session.Characters.FirstOrDefault(c => c.CharId == charId);
        var skin = ch?.Skins.FirstOrDefault(s => s.SkinId == skinId) ?? ch?.Skins.FirstOrDefault();
        if (skin is null) return _index;

        try
        {
            foreach (var r in SoundIndex.Build(session, skin, charId, _ => { }).Rows)
                _index.TryAdd(r.MediaId, r);
        }
        catch { }
        return _index;
    }

    private static double? Seconds(byte[] wem, BnkBuilder.WemInfo info)
    {
        if (!info.IsRiff || info.SampleRate == 0) return null;
        var p = 12;
        while (p + 8 <= wem.Length)
        {
            var id = Encoding.ASCII.GetString(wem, p, 4);
            var sz = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(wem.AsSpan(p + 4, 4));
            if (sz < 0 || p + 8 + sz > wem.Length) break;
            if (id == "fmt " && info.FormatTag == 0xFFFF && sz >= 18 + 48)
                return System.Buffers.Binary.BinaryPrimitives
                           .ReadUInt32LittleEndian(wem.AsSpan(p + 8 + 18 + 0x06, 4)) / (double)info.SampleRate;
            if (id == "data" && info.FormatTag != 0xFFFF)
                return sz / (double)(info.SampleRate * Math.Max((ushort)1, info.Channels) * 2);
            p += 8 + sz + (sz & 1);
        }
        return null;
    }
}
