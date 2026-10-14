using System.IO;

namespace MRAudioKit;

/// <summary>
/// Checks that remembering a conversion actually saves the work and returns the same
/// bytes, that a conversion is never reused across a change of decoder, and that the
/// destructive replace-the-source option is safe when it fires.
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

            // ---- a change of decoder --------------------------------------------
            //
            // The scenario, in the order it happened: a file is imported, the decoder
            // that imported it is replaced, the same file is imported again. Keyed on the
            // file's contents alone the second import returned the first decoder's bytes,
            // for ever, and only on a machine that had the first entry -- so one reader's
            // mod and a fresh install of the same build shipped different audio, with
            // nothing on either side to say which was which.
            //
            // Proved with bytes no encoder would ever produce, in two steps. First that
            // the plant IS served, because a check that cannot tell a hit from a miss
            // would pass just as well against a cache that had stopped working. Then that
            // it is NOT served once the decoder is a different one.
            W("");
            W("a change of decoder");
            var hash = ConvertedCache.HashOf(input);
            var wasDecoder = AudioDecode.DecoderIdentity;
            byte[] planted = [.. "PLANTED"u8, .. first];
            ConvertedCache.Put(options, hash, wasDecoder, planted);
            var served = PendingWem.ToWemBytes(input, out _);
            Check("an entry this decoder wrote is served",
                  served is not null && served.AsSpan().SequenceEqual(planted));

            // Taking the decoder away is the one identity change that can be made from
            // here: a second real revision would have to be downloaded. Cleared rather
            // than pointed somewhere else, because a path that does not exist is
            // discarded and re-resolved to the bundled payload, and because clearing it
            // is what a host does when the reader empties the box in Settings > Tools.
            var vgmWas = AudioDecode.VgmstreamPath;
            if (string.IsNullOrWhiteSpace(vgmWas) || !File.Exists(vgmWas))
                W("  --    the old bytes are not served to a different decoder   " +
                  "skipped: there is no decoder on this host to take away, so a second " +
                  "identity cannot be produced");
            else
                try
                {
                    AudioDecode.VgmstreamPath = null;
                    Check("taking the decoder away is a different identity",
                          AudioDecode.DecoderIdentity != wasDecoder,
                          AudioDecode.DecoderIdentity);
                    var after = PendingWem.ToWemBytes(input, out var whyAfter);
                    Check("and the old bytes are not served to a different decoder",
                          after is null || !after.AsSpan().SequenceEqual(planted),
                          after is null ? whyAfter
                          : after.AsSpan().SequenceEqual(planted)
                              ? $"the planted {planted.Length:N0} B came straight back"
                              : $"re-encoded, {after.Length:N0} B");
                }
                finally { AudioDecode.VgmstreamPath = vgmWas; }

            // Keyed alongside rather than invalidated: the entry was never asked for, not
            // destroyed, so the decoder that wrote it still has it. That is what makes a
            // reader who points Settings > Tools at their own copy and back again pay for
            // one encode each way instead of one per switch, and it is why a delete that
            // silently failed could not leave a stale entry to be served here.
            Check("while the decoder that wrote it still has its own entry",
                  PendingWem.ToWemBytes(input, out _) is { } back
                  && back.AsSpan().SequenceEqual(planted));
            ConvertedCache.Clear(options);

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
