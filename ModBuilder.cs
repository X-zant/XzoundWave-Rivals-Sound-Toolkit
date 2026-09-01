using System.IO;
using System.Text;

namespace MRAudioKit;

/// <summary>
/// Turns staged .wem files into a repak-ready folder. Lives outside the window so the
/// same code path can be driven headlessly and verified.
/// </summary>
public static class ModBuilder
{
    public sealed record Result(int Banks, int Loose, int PrefetchTrimmed, string Log,
                                List<string> Outputs);

    /// <param name="prefetchMode">
    /// OFF (default): the full custom clip goes INTO the bank, replacing the original
    /// entry outright, and nothing is written under Media/. This is what modders ship
    /// and what is known to work in-game — so the engine evidently sizes the media from
    /// the DIDX entry rather than the HIRC's inMemorySize, which is left untouched
    /// exactly as BNK-Pack leaves it.
    ///
    /// ON: keep the vanilla prefetch length in the bank and ship the full clip as a
    /// loose Media/ file. Structurally this is what the shipped data looks like
    /// (SourceType=PrefetchStreaming, stub == file[0..n]), but it makes a bigger mod
    /// and is not the community's route. Kept as an option, not the default.
    /// </param>
    public static Result Build(GameSession session, SkinEntry skin,
                               IReadOnlyList<PendingWem> pending, string outRoot,
                               ISet<string> bankPaths, bool prefetchMode = false)
    {
        var log = new StringBuilder();
        var outputs = new List<string>();
        var payload = new Dictionary<uint, byte[]>();
        foreach (var p in pending) payload[p.MediaId] = File.ReadAllBytes(p.FullPath);

        var banksWritten = 0;
        var trimmed = 0;
        var placed = new HashSet<uint>();

        foreach (var bankPath in bankPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!session.Provider.Files.TryGetValue(bankPath, out var bank)) continue;
            byte[] orig;
            try { orig = bank.Read(); } catch { continue; }

            var didxSection = BnkBuilder.ReadSections(orig).FirstOrDefault(s => s.Tag == "DIDX");
            if (didxSection is null) continue;

            var perBank = new Dictionary<uint, byte[]>();
            foreach (var entry in BnkBuilder.ReadDidx(didxSection.Body))
            {
                if (!payload.TryGetValue(entry.Id, out var custom)) continue;
                if (prefetchMode && session.LooseMedia(entry.Id) is not null)
                {
                    var n = Math.Min((int)entry.Size, custom.Length);
                    perBank[entry.Id] = custom[..n];
                    trimmed++;
                    log.AppendLine($"  media {entry.Id}: prefetch mode — bank keeps the first " +
                                   $"{n:N0} B, the full {custom.Length:N0} B ships loose.");
                }
                else
                {
                    // The whole clip replaces the entry. The DIDX size grows from the
                    // vanilla prefetch length to the real length, which is the swap
                    // modders actually ship.
                    perBank[entry.Id] = custom;
                    log.AppendLine($"  media {entry.Id}: {entry.Size:N0} B -> {custom.Length:N0} B in the bank.");
                }
                placed.Add(entry.Id);
            }
            if (perBank.Count == 0) continue;

            var result = BnkBuilder.Build(orig, perBank);
            if (result.Replaced == 0) continue;

            var dest = Path.Combine(outRoot, bankPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, result.Bytes);
            banksWritten++;
            outputs.Add(Path.GetRelativePath(outRoot, dest));
            log.AppendLine($"BANK  {Path.GetFileName(bankPath)}: {result.Replaced} replaced, " +
                           $"{orig.Length:N0} B -> {result.Bytes.Length:N0} B");
        }

        // A loose Media/ file is written ONLY when the id has no bank entry to replace
        // (the spreadsheet's "MEDIA SFX" case), or when prefetch mode is on. Otherwise
        // the bank carries the whole clip and shipping Media/ is dead weight.
        var loose = 0;
        foreach (var (id, bytes) in payload)
        {
            var src = session.LooseMedia(id);
            if (placed.Contains(id) && !prefetchMode) continue;   // fully in the bank now
            if (src is null && placed.Contains(id)) continue;

            var dest = src is not null
                ? Path.Combine(outRoot, src.Path.Replace('/', Path.DirectorySeparatorChar))
                : Path.Combine(outRoot, "Marvel", "Content", "WwiseAudio", "Media",
                               id.ToString()[..2], $"{id}.wem");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            loose++;
            outputs.Add(Path.GetRelativePath(outRoot, dest));
            var note = pending.FirstOrDefault(p => p.MediaId == id)?.Note;
            log.AppendLine($"AUDIO {id} -> {Path.GetRelativePath(outRoot, dest)}" +
                           (string.IsNullOrWhiteSpace(note) ? "" : $"   [{note}]"));
        }

        var header = new StringBuilder();
        header.AppendLine($"{skin.Label}   ({pending.Count} custom file(s))");
        header.AppendLine();
        if (trimmed > 0)
        {
            header.AppendLine("PREFETCH MODE: the bank keeps each sound's vanilla opening fragment and");
            header.AppendLine("the full clip ships as a loose Media/ file. Opening the .bnk will show");
            header.AppendLine("fragments, not your clips. Ship the WHOLE folder.");
            header.AppendLine();
        }
        else if (loose > 0)
        {
            header.AppendLine($"{loose} file(s) had no bank entry to replace and ship loose under");
            header.AppendLine("WwiseAudio/Media/. Everything else is inside the .bnk.");
            header.AppendLine();
        }
        header.Append(log);

        File.WriteAllText(Path.Combine(outRoot, "build-log.txt"), header.ToString(), new UTF8Encoding(true));
        return new Result(banksWritten, loose, trimmed, header.ToString(), outputs);
    }
}
