using System.Collections.Concurrent;

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
            try
            {
                // A PCM wem is a RIFF/WAVE, so the wav reader takes it directly.
                var src = WwiseWem.ReadWav(wem);
                if (src.Bits != 16)
                {
                    failures.Add($"{e.Id}: {src.Bits}-bit, only 16-bit is supported");
                    return;
                }
                var enc = XzoundCore.Encode(src);
                // Refuse a "saving" that is not one. Very short or noisy clips can
                // come out no smaller, and swapping them would cost quality for free.
                if (enc.Length < e.Size) done[e.Id] = enc;
                else failures.Add($"{e.Id}: encoded larger ({enc.Length:N0} >= {e.Size:N0}), kept as PCM");
            }
            catch (Exception ex) { failures.Add($"{e.Id}: {ex.Message}"); }
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
}
