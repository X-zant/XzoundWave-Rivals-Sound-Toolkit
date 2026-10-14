using System.Collections.Concurrent;
using System.IO;

using Xzound;

namespace MRAudioKit;

/// <summary>
/// Re-encodes a bank's uncompressed media to Vorbis in place.
///
/// Mods built by hand tend to embed replacements as PCM, because until XzoundCore
/// existed producing Vorbis meant installing Wwise. PCM plays fine, it is just very
/// large -- a voice pack can run to hundreds of megabytes where the game's own
/// entries are a rounding error. Nothing but the audio bytes changes here: the HIRC
/// graph, event ids and media ids are untouched, so the bank behaves identically.
/// </summary>
public static class BankRecompress
{
    public sealed record Result(
        byte[] Bytes, int Converted, int Skipped, int Failed,
        long MediaBefore, long MediaAfter, List<string> Notes);

    /// <summary>
    /// Convert every PCM entry in <paramref name="bnk"/> to Vorbis and rebuild.
    /// Entries that are already compressed, or that fail to encode, are left exactly
    /// as they were -- a bank that only partly converts is still a valid bank.
    /// </summary>
    public static Result Run(byte[] bnk, Action<string> progress = null)
    {
        var notes = new List<string>();
        var sections = BnkBuilder.ReadSections(bnk);
        var didx = sections.FirstOrDefault(s => s.Tag == "DIDX");
        var data = sections.FirstOrDefault(s => s.Tag == "DATA");
        if (didx is null || data is null)
            return new Result(bnk, 0, 0, 0, 0, 0, ["bank has no embedded media"]);

        var entries = BnkBuilder.ReadDidx(didx.Body);
        var todo = new List<BnkBuilder.MediaEntry>();
        long before = 0, alreadyVorbis = 0;
        foreach (var e in entries)
        {
            var wem = data.Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();
            var info = BnkBuilder.Inspect(wem);
            if (info.FormatTag == 0xFFFE) { todo.Add(e); before += e.Size; }
            else alreadyVorbis += e.Size;
        }
        progress?.Invoke($"{todo.Count} PCM of {entries.Count} media " +
                         $"({before:N0} B to convert, {alreadyVorbis:N0} B already compressed)");
        if (todo.Count == 0)
            return new Result(bnk, 0, entries.Count, 0, 0, 0, ["nothing to convert"]);

        var done = new ConcurrentDictionary<uint, byte[]>();
        var failures = new ConcurrentBag<string>();
        var n = 0;
        Parallel.ForEach(todo, e =>
        {
            var wem = data.Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();
            if (EncodeOne(wem, out var why) is { } enc) done[e.Id] = enc;
            else failures.Add($"{e.Id}: {why}");
            var i = Interlocked.Increment(ref n);
            if (i % 25 == 0) progress?.Invoke($"encoded {i}/{todo.Count}");
        });

        long after = done.Values.Sum(v => (long)v.Length)
                   + todo.Where(e => !done.ContainsKey(e.Id)).Sum(e => (long)e.Size);
        var built = BnkBuilder.Build(bnk, done);
        notes.AddRange(failures.Take(10));
        if (failures.Count > 10) notes.Add($"... and {failures.Count - 10} more");
        notes.AddRange(built.Notes);

        return new Result(built.Bytes, built.Replaced, entries.Count - todo.Count,
                          failures.Count, before, after, notes);
    }

    private static bool IsPcm(byte[] wem)
    {
        try { return BnkBuilder.Inspect(wem).FormatTag == 0xFFFE; }
        catch { return false; }
    }

    /// <summary>
    /// One wem to Vorbis. Null when it is not PCM (with <paramref name="failure"/> null: nothing
    /// to do) or could not be made smaller (with the reason). The same rule a bank's entries get.
    /// </summary>
    public static byte[] EncodeOne(byte[] wem, out string failure)
    {
        failure = null;
        if (!IsPcm(wem)) return null;
        try
        {
            // A PCM wem is a RIFF/WAVE, so the wav reader takes it directly.
            var src = WwiseWem.ReadWav(wem);
            if (src.Bits != 16) { failure = $"{src.Bits}-bit, only 16-bit is supported"; return null; }
            var enc = XzoundCore.Encode(src);
            // Refuse a "saving" that is not one. Very short or noisy clips can come out no
            // smaller, and swapping them would cost quality for free.
            if (enc.Length < wem.Length) return enc;
            failure = $"encoded larger ({enc.Length:N0} >= {wem.Length:N0}), kept as PCM";
            return null;
        }
        catch (Exception ex) { failure = ex.Message; return null; }
    }

    /// <summary>What a recompress of several inputs wrote, in totals and a line per input.</summary>
    public sealed record Summary(List<string> Written, int Converted, int Kept, int Failed,
                                 long Before, long After, List<string> Lines);

    /// <summary>
    /// Recompress what was picked into <paramref name="outFolder"/>: a .bnk or a loose .wem under
    /// its own name, a folder's .bnk and .wem files at their place under it, and a mod .pak or
    /// IoStore bundle as a .pak of its own name, holding every file the mod ships with the PCM
    /// ones re-encoded. Only what changed is written, and nothing over its own input.
    /// </summary>
    public static Summary RunPicked(IReadOnlyList<string> inputs, Settings settings, string outFolder,
                                    Action<string> progress = null)
    {
        var written = new List<string>();
        var lines = new List<string>();
        int converted = 0, kept = 0, failed = 0;
        long before = 0, after = 0;
        Directory.CreateDirectory(outFolder);

        // (the file, where it goes): a folder's files keep their place under it.
        var files = new List<(string Path, string Out)>();
        var mods = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
                files.AddRange(Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
                    .Where(f => ModBank.IsBank(f) || f.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => (f, Path.Combine(outFolder, Path.GetRelativePath(input, f)))));
            else if (ModBank.IsPak(input)) mods.Add(input);
            else files.Add((input, Path.Combine(outFolder, Path.GetFileName(input))));
        }

        foreach (var (path, dest) in files)
        {
            var name = Path.GetFileName(path);
            if (AppPaths.SamePath(dest, path)) { lines.Add($"{name}: would overwrite itself, skipped"); continue; }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { lines.Add($"{name}: {ex.Message}"); failed++; continue; }

            byte[] result = null;
            if (ModBank.IsBank(path))
            {
                var r = Run(bytes, m => progress?.Invoke($"{name}: {m}"));
                converted += r.Converted; kept += r.Skipped; failed += r.Failed;
                if (r.Converted > 0) result = r.Bytes;
                lines.Add($"{name}: {r.Converted} converted, {r.Skipped} already compressed" +
                          (r.Failed > 0 ? $", {r.Failed} left as PCM ({string.Join("; ", r.Notes.Take(2))})" : ""));
            }
            else
            {
                result = EncodeOne(bytes, out var why);
                if (result is not null) converted++;
                else if (why is null) kept++;
                else failed++;
                lines.Add($"{name}: " + (result is not null ? "converted" : why ?? "already compressed"));
            }
            before += bytes.LongLength;
            after += (result ?? bytes).LongLength;
            if (result is null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
            File.WriteAllBytes(dest, result);
            written.Add(dest);
        }

        foreach (var mod in mods)
        {
            var name = Path.GetFileName(mod);
            var outBase = Path.Combine(outFolder, Path.GetFileNameWithoutExtension(mod));
            if (AppPaths.SamePath(outBase + ".pak", Path.ChangeExtension(mod, ".pak")))
            { lines.Add($"{name}: would overwrite itself, skipped"); continue; }
            var work = Path.Combine(Path.GetTempPath(), "XzoundWave", "recompress-" + Guid.NewGuid().ToString("N"));
            try
            {
                var r = RunMod(mod, settings, work, m => progress?.Invoke($"{name}: {m}"));
                converted += r.Converted; kept += r.Kept; failed += r.Failed;
                before += r.Before; after += r.After;
                if (r.Converted == 0)
                {
                    lines.Add($"{name}: nothing to convert ({r.Kept} already compressed" + (r.Failed > 0 ? $", {r.Failed} left as PCM" : "") + ")");
                    continue;
                }
                var (ok, message) = ModPackager.PackagePak(work, r.Outputs, outBase);
                if (!ok) throw new InvalidDataException(message);
                // A bundle under this name would load beside the .pak.
                foreach (var e in new[] { ".utoc", ".ucas" }) if (File.Exists(outBase + e)) File.Delete(outBase + e);
                written.Add(outBase + ".pak");
                lines.Add($"{name}: {r.Converted} converted, {r.Kept} already compressed" +
                          (r.Failed > 0 ? $", {r.Failed} left as PCM" : "") + $" -> {Path.GetFileName(outBase)}.pak");
                lines.AddRange(r.Notes.Take(5).Select(n => "    " + n));
            }
            catch (InvalidDataException ex) { lines.Add($"{name}: {ex.Message}"); failed++; }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        return new Summary(written, converted, kept, failed, before, after, lines);
    }

    /// <summary>What <see cref="RunMod"/> wrote: every file the mod ships, at its path.</summary>
    public sealed record ModResult(List<string> Outputs, int Converted, int Kept, int Failed,
                                   long Before, long After, List<string> Notes);

    /// <summary>
    /// A whole mod: every PCM entry of its banks and every PCM loose Media wem, re-encoded, and
    /// every file it ships written at its path under <paramref name="outRoot"/>, changed or not,
    /// so they pack into a mod that replaces it. Refused for a mod that ships anything else --
    /// cooked assets, say -- which a copy made of its audio alone would drop, and for one with a
    /// container that would not open.
    /// </summary>
    public static ModResult RunMod(string modPath, Settings settings, string outRoot, Action<string> progress = null)
    {
        var opened = ModBank.Open(modPath, settings, withMedia: true);
        var name = Path.GetFileName(modPath);
        if (opened.Others.Count > 0)
            throw new InvalidDataException(
                $"also ships {opened.Others.Count} file(s) that are not audio ({string.Join(", ", opened.Others.Take(3).Select(Path.GetFileName))}" +
                $"{(opened.Others.Count > 3 ? ", ..." : "")}), which a recompressed copy would leave out -- recompress its " +
                ".bnk and .wem files from an unpacked copy instead.");
        if (opened.Skipped.Count > 0)
            throw new InvalidDataException($"{opened.Skipped.Count} container(s) would not open ({string.Join(", ", opened.Skipped)}), " +
                                           "so a copy would be missing them.");

        var outputs = new List<string>();
        var notes = new List<string>();
        int converted = 0, kept = 0, failed = 0;
        long before = 0, after = 0;
        void Write(string mountedAt, byte[] bytes)
        {
            var rel = mountedAt.Replace('\\', '/').TrimStart('/');
            var dest = Path.Combine(outRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            outputs.Add(rel);
        }

        foreach (var bank in opened.Banks)
        {
            if (bank.MountedAt is null) throw new InvalidDataException($"{bank.Name} has no path in the mod.");
            var r = Run(bank.Bytes, m => progress?.Invoke($"{bank.Name}: {m}"));
            converted += r.Converted; kept += r.Skipped; failed += r.Failed;
            before += bank.Bytes.LongLength; after += r.Bytes.LongLength;
            notes.AddRange(r.Failed > 0 ? r.Notes.Take(3).Select(n => $"{bank.Name}: {n}") : []);
            Write(bank.MountedAt, r.Converted > 0 ? r.Bytes : bank.Bytes);
        }

        var pcm = opened.Media.Count(m => IsPcm(m.Bytes));
        if (opened.Media.Count > 0)
            progress?.Invoke($"{pcm} PCM of {opened.Media.Count} loose wem(s)");
        var encoded = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var failures = new ConcurrentBag<string>();
        var n = 0;
        Parallel.ForEach(opened.Media.Where(m => IsPcm(m.Bytes)), m =>
        {
            if (EncodeOne(m.Bytes, out var why) is { } enc) encoded[m.MountedAt] = enc;
            else failures.Add($"{Path.GetFileName(m.MountedAt)}: {why}");
            var i = Interlocked.Increment(ref n);
            if (i % 25 == 0) progress?.Invoke($"encoded {i}/{pcm}");
        });
        foreach (var m in opened.Media)
        {
            var bytes = encoded.GetValueOrDefault(m.MountedAt) ?? m.Bytes;
            before += m.Bytes.LongLength; after += bytes.LongLength;
            Write(m.MountedAt, bytes);
        }
        converted += encoded.Count;
        kept += opened.Media.Count - pcm;
        failed += failures.Count;
        notes.AddRange(failures.Take(5));
        return new ModResult(outputs, converted, kept, failed, before, after, notes);
    }
}
