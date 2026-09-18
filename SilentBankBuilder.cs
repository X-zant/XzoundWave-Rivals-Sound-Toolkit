using System.Buffers.Binary;
using System.IO;
using System.Text;

using Xzound;

namespace MRAudioKit;

/// <summary>
/// Replaces chosen sounds with silence, leaving everything else as shipped.
///
/// Silence is generated to match each sound's own sample rate, channel count and
/// length rather than reusing one canned clip. Length matters: a sound the game
/// waits on -- a voice line a subtitle is timed against, a loop it fades out of --
/// behaves the way it always did, and only the audio goes away. Silence costs almost
/// nothing in Vorbis, so matching the length is close to free.
/// </summary>
public static class SilentBankBuilder
{
    public sealed record Row(uint MediaId, string Bank, string Events, string Category,
                             string Subtitle, double Seconds);

    public sealed record Result(int Banks, int Silenced, int NotFound, List<Row> Legend, string Log);

    /// <summary>Longest silence we will write for one entry.</summary>
    private const double MaxSeconds = 60;

    public static Result Build(GameSession session, IEnumerable<string> bankPaths,
                               IReadOnlySet<uint> only, string outRoot, SkinSounds context)
    {
        var log = new StringBuilder();
        var legend = new List<Row>();
        int banks = 0, silenced = 0;
        var found = new HashSet<uint>();

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
            var payload = new Dictionary<uint, byte[]>();

            foreach (var entry in BnkBuilder.ReadDidx(didx.Body))
            {
                if (!only.Contains(entry.Id)) continue;
                found.Add(entry.Id);

                var wem = data.Body.AsSpan((int)entry.Offset, (int)entry.Size).ToArray();
                var (rate, channels, seconds) = Describe(wem);
                var quiet = Silence(rate, channels, seconds);
                if (quiet is null)
                {
                    log.AppendLine($"{bankName}: {entry.Id} could not be silenced (unreadable header).");
                    continue;
                }
                payload[entry.Id] = quiet;

                var rows = context?.Rows
                    .Where(r => r.MediaId == entry.Id &&
                                r.Bank.Equals(stem, StringComparison.OrdinalIgnoreCase))
                    .ToList() ?? [];
                legend.Add(new Row(entry.Id, bankName,
                    string.Join(" | ", rows.Select(r => r.Display).Distinct()),
                    rows.FirstOrDefault()?.Category ?? "",
                    rows.FirstOrDefault()?.Subtitle ?? "",
                    seconds));
                silenced++;
            }

            if (payload.Count == 0) continue;
            var built = BnkBuilder.Build(orig, payload);
            var dest = Path.Combine(outRoot, bankPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, built.Bytes);
            banks++;
            log.AppendLine($"{bankName}: {built.Replaced} silenced, " +
                           $"{orig.Length:N0} B -> {built.Bytes.Length:N0} B");
        }

        var csv = new StringBuilder();
        csv.AppendLine("MediaId,Bank,Event(s),Category,Subtitle,Length");
        foreach (var r in legend.OrderBy(r => r.Bank).ThenBy(r => r.MediaId))
            csv.AppendLine(string.Join(',', new[]
            {
                r.MediaId.ToString(), r.Bank, r.Events, r.Category, r.Subtitle,
                r.Seconds.ToString("0.00"),
            }.Select(x => $"\"{(x ?? "").Replace("\"", "\"\"")}\"")));
        Directory.CreateDirectory(outRoot);
        File.WriteAllText(Path.Combine(outRoot, "silent-bank-legend.csv"), csv.ToString(),
                          new UTF8Encoding(true));

        var missing = only.Count - found.Count;
        var header = new StringBuilder();
        header.AppendLine($"Silent build — {silenced} sound(s) replaced with silence.");
        header.AppendLine("Everything not listed keeps its shipped audio.");
        if (missing > 0)
            header.AppendLine($"{missing} selected id(s) are not embedded in these banks and were skipped.");
        header.AppendLine();
        header.Append(log);
        File.WriteAllText(Path.Combine(outRoot, "build-log.txt"), header.ToString(), new UTF8Encoding(true));

        return new Result(banks, silenced, missing, legend, header.ToString());
    }

    // ---- format of the sound being replaced --------------------------------

    /// <summary>Rate, channels and playing time of an existing wem, PCM or Vorbis.</summary>
    private static (int Rate, int Channels, double Seconds) Describe(byte[] wem)
    {
        int rate = 48000, ch = 1, dataLen = 0;
        ushort tag = 0;
        byte[] ext = null;
        var p = 12;
        while (p + 8 <= wem.Length)
        {
            var id = Encoding.ASCII.GetString(wem, p, 4);
            var sz = (int)BinaryPrimitives.ReadUInt32LittleEndian(wem.AsSpan(p + 4, 4));
            if (sz < 0 || p + 8 + sz > wem.Length) break;
            if (id == "fmt " && sz >= 16)
            {
                tag = BinaryPrimitives.ReadUInt16LittleEndian(wem.AsSpan(p + 8, 2));
                ch = BinaryPrimitives.ReadUInt16LittleEndian(wem.AsSpan(p + 10, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wem.AsSpan(p + 12, 4));
                if (sz >= 18 + 48) ext = wem.AsSpan(p + 8 + 18, 48).ToArray();
            }
            if (id == "data") dataLen = sz;
            p += 8 + sz + (sz & 1);
        }
        if (ch < 1) ch = 1;
        if (rate < 8000) rate = 48000;

        // Vorbis states its own frame count; PCM has to be measured from the bytes.
        double seconds = tag == 0xFFFF && ext is not null
            ? BinaryPrimitives.ReadUInt32LittleEndian(ext.AsSpan(0x06, 4)) / (double)rate
            : dataLen / (double)(rate * ch * 2);

        return (rate, Math.Min(ch, 2), Math.Clamp(seconds, 0.05, MaxSeconds));
    }

    /// <summary>Silence shaped like an existing sound: same rate, channels, length.</summary>
    public static byte[] Like(byte[] existingWem)
    {
        var (rate, channels, seconds) = Describe(existingWem);
        return Silence(rate, channels, seconds);
    }

    private static byte[] Silence(int rate, int channels, double seconds)
    {
        var frames = (int)Math.Round(rate * seconds);
        var pcm = new byte[frames * channels * 2];
        var wav = new WwiseWem.WavData(rate, (short)channels, 16, pcm);
        try { return XzoundCore.Encode(wav); }
        catch { try { return WwiseWem.Write(wav); } catch { return null; } }
    }
}
