using System.IO;
using System.Text;
using System.Collections.Concurrent;

namespace MRAudioKit;

/// <summary>
/// Writes banks with every sound's level applied.
///
/// Each sound is scaled from the audio the game shipped, using the level stored
/// against it, so exporting at 1.0 then 0.8 then 0.6 gives three independent results
/// rather than three stacked round trips through a lossy codec.
/// </summary>
public static class VolumeBankBuilder
{
    public sealed record Row(uint MediaId, string Bank, string Events, double Gain, double ClippedPercent);

    public sealed record Result(int Banks, int Scaled, int Skipped, int Failed,
                                List<Row> Legend, string Log);

    /// <param name="gainOf">The level for a media id; 1.0 means leave it alone.</param>
    public static Result Build(GameSession session, IEnumerable<string> bankPaths,
                               Func<uint, double> gainOf, string outRoot, SkinSounds context,
                               string vgmstreamPath, Action<string> progress = null)
    {
        var log = new StringBuilder();
        var legend = new List<Row>();
        int banks = 0, scaled = 0, skipped = 0, failed = 0;

        foreach (var bankPath in bankPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!session.Provider.Files.TryGetValue(bankPath, out var file)) continue;
            byte[] orig;
            try { orig = file.Read(); } catch { continue; }

            var sections = BnkBuilder.ReadSections(orig);
            var didx = sections.FirstOrDefault(s => s.Tag == "DIDX");
            var data = sections.FirstOrDefault(s => s.Tag == "DATA");
            if (didx is null || data is null) continue;

            var bankName = Path.GetFileName(bankPath);
            var stem = Path.GetFileNameWithoutExtension(bankName);
            var entries = BnkBuilder.ReadDidx(didx.Body);

            var todo = entries.Where(e => Math.Abs(gainOf(e.Id) - 1.0) > 0.001).ToList();
            skipped += entries.Count - todo.Count;
            if (todo.Count == 0)
            {
                log.AppendLine($"{bankName}: nothing to scale.");
                continue;
            }
            progress?.Invoke($"{bankName}: scaling {todo.Count} of {entries.Count}…");

            var done = new ConcurrentDictionary<uint, VolumeTool.Result>();
            var errors = new ConcurrentBag<string>();
            var n = 0;
            Parallel.ForEach(todo, e =>
            {
                var wem = data.Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();
                var r = VolumeTool.Scale(wem, gainOf(e.Id), vgmstreamPath);
                if (r is null) errors.Add($"{e.Id}: could not be decoded, left as shipped");
                else done[e.Id] = r;
                var i = Interlocked.Increment(ref n);
                if (i % 25 == 0) progress?.Invoke($"{bankName}: {i}/{todo.Count}");
            });

            foreach (var (id, r) in done)
            {
                var rows = context?.Rows
                    .Where(x => x.MediaId == id &&
                                x.Bank.Equals(stem, StringComparison.OrdinalIgnoreCase))
                    .ToList() ?? [];
                legend.Add(new Row(id, bankName,
                    string.Join(" | ", rows.Select(x => x.Display).Distinct()),
                    gainOf(id), r.ClippedPercent));
            }
            scaled += done.Count;
            failed += errors.Count;
            foreach (var err in errors.Take(5)) log.AppendLine($"{bankName}: {err}");

            if (done.Count == 0) continue;
            var built = BnkBuilder.Build(orig, done.ToDictionary(k => k.Key, v => v.Value.Bytes));
            var dest = Path.Combine(outRoot, bankPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, built.Bytes);
            banks++;
            log.AppendLine($"{bankName}: {built.Replaced} scaled, " +
                           $"{orig.Length:N0} B -> {built.Bytes.Length:N0} B");
        }

        var csv = new StringBuilder();
        csv.AppendLine("MediaId,Bank,Event(s),Gain,ClippedPercent");
        foreach (var r in legend.OrderBy(r => r.Bank).ThenBy(r => r.MediaId))
            csv.AppendLine(string.Join(',', new[]
            {
                r.MediaId.ToString(), r.Bank, r.Events,
                r.Gain.ToString("0.0##"), r.ClippedPercent.ToString("0.00"),
            }.Select(x => $"\"{(x ?? "").Replace("\"", "\"\"")}\"")));
        Directory.CreateDirectory(outRoot);
        File.WriteAllText(Path.Combine(outRoot, "volume-legend.csv"), csv.ToString(), new UTF8Encoding(true));

        // Clipping is the one thing that makes a louder mod sound broken rather than
        // louder, so it is reported rather than left for someone to notice in game.
        var clipping = legend.Where(r => r.ClippedPercent > 0.1)
                             .OrderByDescending(r => r.ClippedPercent).ToList();

        var header = new StringBuilder();
        header.AppendLine($"Volume build — {scaled} sound(s) scaled, {skipped} left at 1.0.");
        if (failed > 0) header.AppendLine($"{failed} could not be decoded and were left as shipped.");
        if (clipping.Count > 0)
        {
            header.AppendLine($"{clipping.Count} sound(s) clipped. Loudest offenders:");
            foreach (var r in clipping.Take(5))
                header.AppendLine($"  {r.MediaId} {r.Events}  {r.ClippedPercent:0.0}% of samples clipped");
        }
        header.AppendLine();
        header.Append(log);
        File.WriteAllText(Path.Combine(outRoot, "build-log.txt"), header.ToString(), new UTF8Encoding(true));

        return new Result(banks, scaled, skipped, failed, legend, header.ToString());
    }
}
