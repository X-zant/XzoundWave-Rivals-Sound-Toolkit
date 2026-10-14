using System.Globalization;
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

    /// <summary>A loose Media file to scale: where it goes in the mod, and its bytes.</summary>
    public sealed record LooseSource(string Path, byte[] Bytes);

    /// <param name="gainOf">The level for a media id; 1.0 means leave it alone.</param>
    /// <param name="muted">
    /// Silenced outright rather than scaled. A mute means the same thing in every build,
    /// and scaling silence to 80% would be a slower way of producing the same silence.
    /// </param>
    public static Result Build(GameSession session, IEnumerable<string> bankPaths,
                               Func<uint, double> gainOf, string outRoot, SkinSounds context,
                               string vgmstreamPath, Action<string> progress = null,
                               IReadOnlySet<uint> muted = null,
                               BankBase baseBanks = null,
                               IReadOnlyCollection<uint> looseIds = null,
                               Func<uint, LooseSource> modLoose = null)
    {
        var log = new StringBuilder();
        var legend = new List<Row>();
        int banks = 0, scaled = 0, skipped = 0, failed = 0;
        // Every id a bank below embeds, whole or as a stub: the bank route handles those, and
        // only the rest are scaled as loose files after it.
        var seen = new HashSet<uint>();

        foreach (var bankPath in bankPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var orig = (baseBanks ?? BankBase.None).Read(session, bankPath);
            if (orig is null) continue;

            var sections = BnkBuilder.ReadSections(orig);
            var didx = sections.FirstOrDefault(s => s.Tag == "DIDX");
            var data = sections.FirstOrDefault(s => s.Tag == "DATA");
            if (didx is null || data is null) continue;

            var bankName = Path.GetFileName(bankPath);
            var stem = Path.GetFileNameWithoutExtension(bankName);
            var entries = BnkBuilder.ReadDidx(didx.Body);
            foreach (var e in entries) seen.Add(e.Id);

            // What a sound really plays. Most voice lines stream from a loose Media/ file and
            // the bank embeds only its opening (a prefetch stub, a byte-prefix of that file):
            // scaling or silencing the stub changed a fraction of a second and left the rest
            // of the line streaming as shipped. The whole clip is scaled instead and embedded
            // whole, the route the regular build takes for these and the one known to work.
            byte[] Clip(BnkBuilder.MediaEntry e)
            {
                var held = data.Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();
                // A loaded mod's loose file whose opening the bank holds: a prefetch-mode mod, whose
                // stub is its own clip's start and so no prefix of the game's file.
                if (modLoose?.Invoke(e.Id)?.Bytes is { } theirs && theirs.Length > held.Length
                    && theirs.AsSpan(0, held.Length).SequenceEqual(held))
                    return theirs;
                try
                {
                    // A loaded mod's loose file is what the stub streams into now.
                    if (session?.LooseMedia(e.Id)?.Read() is { } full && full.Length > held.Length
                        && full.AsSpan(0, held.Length).SequenceEqual(held))
                        return modLoose?.Invoke(e.Id)?.Bytes ?? full;
                }
                catch { /* the stub is still something to work on */ }
                return held;
            }

            // Muting wins over any level: there is no useful gain to apply to silence.
            var quiet = new Dictionary<uint, byte[]>();
            if (muted is { Count: > 0 })
                foreach (var e in entries.Where(e => muted.Contains(e.Id)))
                {
                    var wem = Clip(e);
                    if (SilentBankBuilder.Like(wem) is { } s2) quiet[e.Id] = s2;
                }

            var todo = entries.Where(e => !quiet.ContainsKey(e.Id)
                                          && Math.Abs(gainOf(e.Id) - 1.0) > 0.001).ToList();
            skipped += entries.Count - todo.Count - quiet.Count;
            if (todo.Count == 0 && quiet.Count == 0)
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
                var wem = Clip(e);
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

            // A bank whose only change is a mute is still a bank to write.
            if (done.Count == 0 && quiet.Count == 0) continue;
            var payload = done.ToDictionary(k => k.Key, v => v.Value.Bytes);
            foreach (var (id, bytes) in quiet) payload[id] = bytes;
            if (quiet.Count > 0) log.AppendLine($"{bankName}: {quiet.Count} muted.");
            var built = BnkBuilder.Build(orig, payload);
            var dest = Path.Combine(outRoot, bankPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, built.Bytes);
            banks++;
            log.AppendLine($"{bankName}: {built.Replaced} scaled, " +
                           $"{orig.Length:N0} B -> {built.Bytes.Length:N0} B");
        }

        // Sounds no bank holds: their loose file, scaled or silenced, written at the path the game
        // reads it from -- a loaded mod's file where one is loaded, else the game's own.
        var loose = new List<(uint Id, string Path, byte[] Wem)>();
        foreach (var id in (looseIds ?? []).Where(id => !seen.Contains(id)).Distinct().OrderBy(x => x))
        {
            var mod = modLoose?.Invoke(id);
            var game = mod is null ? session?.LooseMedia(id, session.Language) ?? session?.LooseMedia(id) : null;
            if (mod is null && game is null) continue;
            try { loose.Add((id, mod?.Path ?? game.Path, mod?.Bytes ?? game.Read())); }
            catch { failed++; log.AppendLine($"media {id}: its loose file could not be read."); }
        }
        if (loose.Count > 0)
        {
            progress?.Invoke($"scaling {loose.Count} loose file(s)…");
            var out_ = new ConcurrentDictionary<uint, (byte[] Bytes, double Clipped)>();
            var errors = new ConcurrentBag<string>();
            Parallel.ForEach(loose, f =>
            {
                if (muted?.Contains(f.Id) == true)
                {
                    if (SilentBankBuilder.Like(f.Wem) is { } quiet) out_[f.Id] = (quiet, 0);
                    else errors.Add($"{f.Id}: muted, but its header could not be read");
                    return;
                }
                if (Math.Abs(gainOf(f.Id) - 1.0) <= 0.001) return;
                var r = VolumeTool.Scale(f.Wem, gainOf(f.Id), vgmstreamPath);
                if (r is null) errors.Add($"{f.Id}: could not be decoded, left as shipped");
                else out_[f.Id] = (r.Bytes, r.ClippedPercent);
            });
            foreach (var f in loose)
            {
                // Not written: left at 1.0, or failed and counted with the failures below.
                if (!out_.TryGetValue(f.Id, out var done))
                {
                    if (muted?.Contains(f.Id) != true && Math.Abs(gainOf(f.Id) - 1.0) <= 0.001) skipped++;
                    continue;
                }
                var dest = Path.Combine(outRoot, f.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, done.Bytes);
                var rows = context?.Rows.Where(x => x.MediaId == f.Id).ToList() ?? [];
                var muteIt = muted?.Contains(f.Id) == true;
                if (!muteIt)
                {
                    scaled++;
                    legend.Add(new Row(f.Id, rows.FirstOrDefault()?.Bank ?? Path.GetFileName(f.Path),
                        string.Join(" | ", rows.Select(x => x.Display).Distinct()), gainOf(f.Id), done.Clipped));
                }
                log.AppendLine($"{f.Path}: {(muteIt ? "muted" : $"scaled {gainOf(f.Id):0.0##}x")} (loose file, no bank holds it).");
            }
            failed += errors.Count;
            foreach (var err in errors.Take(5)) log.AppendLine("loose: " + err);
        }

        Directory.CreateDirectory(outRoot);
        File.WriteAllText(Path.Combine(outRoot, "volume-legend.csv"), LegendCsv(legend),
                          new UTF8Encoding(true));

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

    /// <summary>
    /// The legend as it is written to disk.
    ///
    /// Gain and the clipped percentage are formatted with the invariant culture, never
    /// the machine's own. A legend written on a German-locale machine would otherwise
    /// carry "0,85" into a comma-separated file, which reads as a truncated 0 to
    /// anything that parses it and as two columns to anything that splits before it
    /// unquotes. The file is a build artefact people pass around, so it has to say the
    /// same thing wherever it was produced.
    /// </summary>
    public static string LegendCsv(IEnumerable<Row> legend)
    {
        var csv = new StringBuilder();
        csv.AppendLine("MediaId,Bank,Event(s),Gain,ClippedPercent");
        // Ordinal, not the machine's collation: these are identifiers, and a legend
        // whose rows come out in a different order on a German machine than on an
        // English one is not a file two people can compare.
        foreach (var r in legend.OrderBy(r => r.Bank, StringComparer.Ordinal).ThenBy(r => r.MediaId))
            csv.AppendLine(string.Join(',', new[]
            {
                r.MediaId.ToString(CultureInfo.InvariantCulture), r.Bank, r.Events,
                r.Gain.ToString("0.0##", CultureInfo.InvariantCulture),
                r.ClippedPercent.ToString("0.00", CultureInfo.InvariantCulture),
            }.Select(x => $"\"{(x ?? "").Replace("\"", "\"\"")}\"")));
        return csv.ToString();
    }
}
