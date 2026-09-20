using System.IO;

namespace MRAudioKit;

/// <summary>
/// Checks that remembering a conversion actually saves the work and returns the same
/// bytes, and that the destructive replace-the-source option is safe when it fires.
/// </summary>
public static class CacheSelfTest
{
    /// <summary>XzoundWave.exe --cachetest &lt;anyAudioFile&gt;</summary>
    public static int Run(string source)
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        if (!File.Exists(source)) { W("no such file: " + source); return 1; }

        var work = Path.Combine(Path.GetTempPath(), "xzw-cachetest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var cacheDir = Path.Combine(work, "cache");

        // A settings object of our own, so the real cache is never touched.
        var options = new Settings { CacheConvertedAudio = true, ConvertedCacheDir = cacheDir };
        var previous = ConvertedCache.Options;
        ConvertedCache.Options = options;
        try
        {
            var input = Path.Combine(work, "1000585642-source" + Path.GetExtension(source));
            File.Copy(source, input);
            W($"source: {Path.GetFileName(source)}  ({new FileInfo(input).Length:N0} B)");
            W("");

            // ---- first time: encode ----------------------------------------
            var t0 = DateTime.UtcNow;
            var first = PendingWem.ToWemBytes(input, out var why1);
            var firstMs = (DateTime.UtcNow - t0).TotalMilliseconds;
            Check("it converts", first is not null, why1);
            if (first is null) return 1;
            W($"        first pass {firstMs:N0} ms -> {first.Length:N0} B");

            var stored = ConvertedCache.Stats(options);
            Check("the conversion was kept", stored.Files == 1, $"{stored.Files} entry(s)");

            // ---- second time: remembered -----------------------------------
            var t1 = DateTime.UtcNow;
            var second = PendingWem.ToWemBytes(input, out _);
            var secondMs = (DateTime.UtcNow - t1).TotalMilliseconds;
            W($"        second pass {secondMs:N0} ms");
            Check("the same bytes come back", second is not null && second.AsSpan().SequenceEqual(first));
            Check("and it did not encode again", secondMs < Math.Max(firstMs * 0.5, 5),
                  $"{firstMs:N0} ms -> {secondMs:N0} ms");

            // ---- a copy under another name is the same audio ----------------
            var renamed = Path.Combine(work, "999-renamed" + Path.GetExtension(source));
            File.Copy(input, renamed);
            var third = PendingWem.ToWemBytes(renamed, out _);
            Check("a renamed copy hits the same entry",
                  third is not null && third.AsSpan().SequenceEqual(first)
                  && ConvertedCache.Stats(options).Files == 1);

            // ---- an edited file must NOT hit --------------------------------
            var edited = Path.Combine(work, "998-edited" + Path.GetExtension(source));
            var bytes = File.ReadAllBytes(input);
            bytes[^1] ^= 0xFF;                       // change the content, keep it parseable
            File.WriteAllBytes(edited, bytes);
            PendingWem.ToWemBytes(edited, out _);
            Check("a changed file is not served the old answer",
                  ConvertedCache.Stats(options).Files == 2,
                  $"{ConvertedCache.Stats(options).Files} entry(s)");

            // ---- turning it off stops it storing ----------------------------
            options.CacheConvertedAudio = false;
            var before = ConvertedCache.Stats(options).Files;
            var another = Path.Combine(work, "997-off" + Path.GetExtension(source));
            var b2 = File.ReadAllBytes(input); b2[0] ^= 0x01;
            File.WriteAllBytes(another, b2);
            PendingWem.ToWemBytes(another, out _);
            options.CacheConvertedAudio = true;
            Check("nothing is stored while it is off",
                  ConvertedCache.Stats(options).Files == before);

            // ---- clearing ---------------------------------------------------
            var cleared = ConvertedCache.Clear(options);
            Check("clearing empties it", cleared > 0 && ConvertedCache.Stats(options).Files == 0,
                  $"removed {cleared}");

            // ---- the destructive option -------------------------------------
            W("");
            W("replacing the source file");
            var victim = Path.Combine(work, "996-victim" + Path.GetExtension(source));
            File.Copy(input, victim);
            var replaced = ConvertedCache.ReplaceSource(victim, first, out var repErr);
            Check("a .wem is written", replaced is not null && File.Exists(replaced), repErr);
            Check("it holds exactly the converted bytes",
                  replaced is not null && File.ReadAllBytes(replaced).AsSpan().SequenceEqual(first));
            if (!AudioDecode.IsWem(victim))
                Check("the original is gone", !File.Exists(victim));
            Check("no .part file is left behind",
                  Directory.GetFiles(work, "*.part").Length == 0);

            // A destructive step must refuse rather than clobber something already there.
            var occupied = Path.Combine(work, "995-occupied.mp3");
            File.WriteAllBytes(occupied, File.ReadAllBytes(input));
            File.WriteAllBytes(Path.ChangeExtension(occupied, ".wem"), [1, 2, 3]);
            var blocked = ConvertedCache.ReplaceSource(occupied, first, out var blockErr);
            Check("it refuses when a .wem of that name already exists",
                  blocked is null && File.Exists(occupied), blockErr);
        }
        finally
        {
            ConvertedCache.Options = previous;
            try { Directory.Delete(work, true); } catch { }
        }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
