using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

using Xzound;

namespace MRAudioKit;

/// <summary>
/// What the importer can decode, checked against files this test writes itself.
///
/// Nothing here reads anything off disk that it did not create. An import test that
/// needed an mp3 lying somewhere could only ever run on the machine that had one, and
/// the formats most worth checking are exactly the ones this project cannot encode: it
/// carries a Wwise Vorbis writer and nothing else. So the containers below are written
/// by hand, which is possible because the interesting question is never the codec. It is
/// which route a file takes, whether the route is lossless when the source is, and
/// whether a subprocess was asked for the whole stream once rather than for its loop
/// twice with a fade.
///
/// The one thing that cannot be faked is a real mp3, m4a, aac, wma or ogg, and those say
/// they were skipped and why rather than passing on a file that is not one.
///
/// A RIFF wav appears twice, once named .wav and once named .dat. That is not a trick:
/// vgmstream declares dat among the extensions it reads, so the same bytes under the two
/// names is the cleanest way there is to compare the two routes with the codec held
/// constant, and it is how the loop flag is checked at all.
/// </summary>
public static class AudioDecodeSelfTest
{
    private const int Rate = 44100;
    private const int Frames = 4410;

    /// <summary>XzoundWave.exe --decode</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }
        void Skip(string what, string why) => W($"  --    {what}   skipped: {why}");

        var exe = AudioDecode.VgmstreamPath;
        var haveVgm = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe);
        W($"host: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        W($"decoder: {(haveVgm ? exe : "NOT AVAILABLE")}");
        W($"revision: {AudioDecode.DecoderRevision ?? "unknown"}");

        // ---- which route each format takes ---------------------------------
        //
        // Printed before anything is decoded, because every measurement below is a
        // measurement of one of these two routes and the interesting failure is a file
        // quietly taking the other one.
        W("");
        W("routing");
        W("  " + string.Join("  ", AudioDecode.Extensions.Select(
            e => e + (AudioDecode.IsWem(e) ? ":pass"
                    : AudioDecode.ReadInProcess(e) ? ":here" : ":vgm"))));

        Check("wav is read in this process", AudioDecode.ReadInProcess("x.wav"));
        Check("ogg is read in this process", AudioDecode.ReadInProcess("x.ogg"));
        // The migration this test exists for. Media Foundation read these four on
        // Windows and the portable build refused them, so a route that is not vgmstream
        // means one of the two platforms cannot import them at all.
        foreach (var ext in new[] { ".mp3", ".m4a", ".aac", ".wma", ".flac" })
            Check($"{ext.TrimStart('.')} is decoded by vgmstream on every platform",
                  !AudioDecode.ReadInProcess("x" + ext) && AudioDecode.CanDecode("x" + ext));
        Check("a wem is accepted and never decoded",
              AudioDecode.IsWem("CLIP.WEM") && AudioDecode.CanDecode("CLIP.WEM"));

        if (haveVgm)
            Check("the accepted list includes what the decoder declares",
                  AudioDecode.CanDecode("x.opus") && AudioDecode.CanDecode("x.aiff"),
                  $"{AudioDecode.Extensions.Length} extension(s)");
        else
            Skip("the accepted list includes what the decoder declares",
                 "there is no vgmstream on this host to ask");

        var dir = Path.Combine(Path.GetTempPath(), "xzw-decode-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var samples = Chirp(Frames, 2);
            var pcm = Pcm16(samples);

            // ---- the in-process wav reader ---------------------------------
            W("");
            W("a wav, read here");
            var wavPath = Path.Combine(dir, "tone.wav");
            File.WriteAllBytes(wavPath, Loop(Wav(1, 16, 2, samples), Frames));

            WwiseWem.WavData here = null;
            try { here = AudioDecode.ToPcm(wavPath); }
            catch (Exception ex) { Check("it decodes", false, ex.Message); }
            if (here is not null)
            {
                Check("it decodes", true, $"{here.SampleRate} Hz {here.Channels}ch {here.Bits}-bit");
                Check("rate and channels are the source's", here.SampleRate == Rate && here.Channels == 2);
                Check("every frame is there and no more", here.Pcm.Length == Frames * 2 * 2,
                      $"{here.Pcm.Length / 4} frames");
                Check("the samples are the ones written", here.Pcm.AsSpan().SequenceEqual(pcm));
            }

            // ---- the same bytes, through vgmstream -------------------------
            W("");
            W("the same wav, through vgmstream");
            var datPath = Path.Combine(dir, "tone.dat");
            File.Copy(wavPath, datPath);
            if (!haveVgm)
                Skip("it decodes through the subprocess", "there is no vgmstream on this host");
            else if (AudioDecode.ReadInProcess(datPath))
                Skip("it decodes through the subprocess",
                     "this decoder does not declare dat, so the two routes cannot be " +
                     "compared on one file");
            else
            {
                WwiseWem.WavData via = null;
                try { via = AudioDecode.ToPcm(datPath); }
                catch (Exception ex) { Check("it decodes through the subprocess", false, ex.Message); }
                if (via is not null)
                {
                    Check("it decodes through the subprocess", true,
                          $"{via.SampleRate} Hz {via.Channels}ch {via.Bits}-bit");
                    // The loop flag, measured. This file carries a smpl loop over its
                    // middle half with an infinite play count, which vgmstream obeys
                    // unless told not to: the whole stream once is 4410 frames, the
                    // default is two passes of the loop and a ten-second fade.
                    Check("a looping source decodes to exactly one pass",
                          via.Pcm.Length == Frames * 2 * 2, $"{via.Pcm.Length / 4} frames");
                    Check("and to the same samples the in-process reader gave",
                          here is not null && via.Pcm.AsSpan().SequenceEqual(here.Pcm));
                }
            }

            // ---- the same question asked of the other three callers ---------
            //
            // The loop flag is not the import's business alone. Four places in the tool
            // run this decoder and all four want the file's own contents, but each one
            // builds its own command line, so the check above proves nothing about the
            // other three -- and for a long time the other three were wrong: a preview
            // played a loop pass and a fade nobody authored, the level tool encoded
            // those into what it shipped, and the duration column stated them.
            //
            // The looping bytes are handed over as a .wem, which is what these three
            // take. vgmstream reads a plain RIFF through its Wwise reader, so the same
            // fixture serves here with the codec and the loop held constant.
            W("");
            W("the same looping source, through the preview, the level tool and the column");
            if (!haveVgm)
                Skip("the preview decodes one pass", "there is no vgmstream on this host");
            else
            {
                var looping = Loop(Wav(1, 16, 2, samples), Frames);
                var preview = new AudioPreview { VgmstreamPath = exe };
                var previewWav = preview.Decode(looping, 990000001, out var previewWhy);
                if (previewWav is null)
                    Check("the preview decodes one pass", false, previewWhy);
                else
                {
                    var got = WwiseWem.ReadWav(File.ReadAllBytes(previewWav));
                    Check("the preview decodes one pass", got.Pcm.Length == Frames * 2 * 2,
                          $"{got.Pcm.Length / 4} frames");
                }

                var secs = preview.Probe(looping, 990000002);
                Check("the duration is the audio's own length",
                      secs is not null && Math.Abs(secs.Value - (double)Frames / Rate) < 0.01,
                      $"{secs:0.000}s of {(double)Frames / Rate:0.000}s");

                var scaled = VolumeTool.Scale(looping, 1.0, exe);
                Check("a level change ships one pass",
                      scaled is not null && scaled.TotalSamples == Frames * 2,
                      scaled is null ? "it would not decode" : $"{scaled.TotalSamples / 2} frames");
            }

            // ---- a format with no in-process reader ------------------------
            W("");
            W("an aiff, which has no reader here at all");
            var aiffPath = Path.Combine(dir, "tone.aiff");
            File.WriteAllBytes(aiffPath, Aiff(samples));
            if (!haveVgm)
                Skip("it decodes", "there is no vgmstream on this host");
            else if (!AudioDecode.CanDecode(aiffPath))
                Skip("it decodes", "this decoder does not declare aiff");
            else
            {
                Check("the route is the subprocess, not this process",
                      !AudioDecode.ReadInProcess(aiffPath));
                try
                {
                    var aiff = AudioDecode.ToPcm(aiffPath);
                    Check("it decodes", true,
                          $"{aiff.SampleRate} Hz {aiff.Channels}ch, {aiff.Pcm.Length / 4} frames");
                    // Big-endian source, little-endian result, and nothing lossy in
                    // between, so anything but an exact match is a byte-order or an
                    // alignment bug rather than a codec difference.
                    Check("sample for sample, with the bytes swapped back",
                          aiff.Pcm.AsSpan().SequenceEqual(pcm));
                }
                catch (Exception ex) { Check("it decodes", false, ex.Message); }
            }

            // ---- the one thing vgmstream will not open ---------------------
            W("");
            W("companded telephone audio, which is why the G.711 tables are still here");
            foreach (var law in new[] { "a mu-law", "an A-law" })
            {
                var mu = law.EndsWith("mu-law", StringComparison.Ordinal);
                var path = Path.Combine(dir, (mu ? "ulaw" : "alaw") + ".wav");
                File.WriteAllBytes(path, G711Wav(samples, mu));
                var want = Pcm16(samples.Select(s => mu
                    ? NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(
                          NAudio.Codecs.MuLawEncoder.LinearToMuLawSample(s))
                    : NAudio.Codecs.ALawDecoder.ALawToLinearSample(
                          NAudio.Codecs.ALawEncoder.LinearToALawSample(s))).ToArray());
                try
                {
                    var got = AudioDecode.ToPcm(path);
                    Check($"{law} wav decodes", got.Pcm.Length == Frames * 2 * 2,
                          $"{got.Pcm.Length / 4} frames");
                    Check("...to what its own table says", got.Pcm.AsSpan().SequenceEqual(want));
                }
                catch (Exception ex) { Check($"{law} wav decodes", false, ex.Message); }
            }

            // Informational, and deliberately not a failure either way: if a later
            // revision learns to open these, the reason to decode them here goes away,
            // and that is worth seeing rather than discovering through a broken build.
            if (haveVgm && !AudioDecode.ReadInProcess("x.dat"))
            {
                var probe = Path.Combine(dir, "ulaw.dat");
                File.Copy(Path.Combine(dir, "ulaw.wav"), probe);
                string said = null;
                try { _ = AudioDecode.ToPcm(probe); }
                catch (Exception ex) { said = ex.Message; }
                W(said is null
                    ? "  --    vgmstream now opens mu-law itself, so the tables here are no longer the only way"
                    : "  --    vgmstream cannot open it, which is why the tables above are not optional");
            }

            // ---- float, and more channels than the encoder writes ----------
            W("");
            W("the other layouts the reader here handles");
            var floatPath = Path.Combine(dir, "float.wav");
            File.WriteAllBytes(floatPath, Wav(3, 32, 2, samples));
            try
            {
                var f = AudioDecode.ToPcm(floatPath);
                Check("a 32-bit float wav decodes", f.Pcm.Length == Frames * 2 * 2,
                      $"{f.Pcm.Length / 4} frames");
                // Float to 16-bit is a rounding, so one least significant bit is the
                // right tolerance and anything larger is a scaling mistake.
                Check("...to within one bit of the source", MaxDelta(f.Pcm, pcm) <= 1,
                      $"max delta {MaxDelta(f.Pcm, pcm)}");
            }
            catch (Exception ex) { Check("a 32-bit float wav decodes", false, ex.Message); }

            var sixPath = Path.Combine(dir, "six.wav");
            File.WriteAllBytes(sixPath, Wav(1, 16, 6, Chirp(Frames, 6)));
            try
            {
                var six = AudioDecode.ToPcm(sixPath);
                Check("a 5.1 source is folded to stereo rather than refused",
                      six.Channels == 2 && six.Pcm.Length == Frames * 2 * 2,
                      $"{six.Channels}ch, {six.Pcm.Length / 4} frames");
            }
            catch (Exception ex) { Check("a 5.1 source is folded to stereo", false, ex.Message); }

            // ---- what the user is told when it will not decode -------------
            //
            // The message is the whole feature for a file nobody can decode, and the
            // wording it replaces told the owner of an unreadable wav to convert it to
            // a wav.
            W("");
            W("what a file that cannot be decoded says");

            // A structurally valid WAVE whose codec nobody implements: the managed reader
            // refuses it and so does vgmstream, which is the case the wav wording is for.
            var oddWav = Path.Combine(dir, "odd.wav");
            File.WriteAllBytes(oddWav, Riff(Fmt(0x9999, 2, Rate, 16), pcm));
            // And 4 KB of nothing in particular, which is not a wav at all. This is the
            // one that must NOT reach vgmstream: it opens it as Nintendo DTK ADPCM and
            // returns a tenth of a second of noise.
            var junkWav = Path.Combine(dir, "junk.wav");
            File.WriteAllBytes(junkWav, Encoding.ASCII.GetBytes(new string('x', 4096)));
            var junkMp3 = Path.Combine(dir, "junk.mp3");
            File.WriteAllBytes(junkMp3, Encoding.ASCII.GetBytes(new string('x', 4096)));

            var oddSaid = Refusal(oddWav);
            var junkSaid = Refusal(junkWav);
            var mp3Said = Refusal(junkMp3);
            W("  unknown codec: " + (oddSaid ?? "(it decoded, which it must not have)"));
            W("  not a wav:     " + (junkSaid ?? "(it decoded, which it must not have)"));
            W("  bad mp3:       " + (mp3Said ?? "(it decoded, which it must not have)"));

            Check("a file that is not a wav is refused rather than guessed at",
                  junkSaid is not null &&
                  junkSaid.Contains("no RIFF WAVE header", StringComparison.Ordinal));
            if (!haveVgm)
                Skip("the wav message names a remedy that is true for a wav",
                     "with no decoder every refusal names the decoder instead");
            else
            {
                Check("the wav message names a remedy that is true for a wav",
                      oddSaid is not null &&
                      oddSaid.Contains("16-bit PCM wav", StringComparison.Ordinal) &&
                      !oddSaid.Contains("Converting it to wav", StringComparison.Ordinal));
                Check("the mp3 message names the format and a remedy that works",
                      mp3Said is not null &&
                      mp3Said.Contains("mp3", StringComparison.Ordinal) &&
                      mp3Said.Contains("wav or ogg", StringComparison.Ordinal));
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // ---- what cannot be checked on any host ----------------------------
        W("");
        W("what no host can check from here");
        Skip("mp3, m4a, aac and wma decode to the right samples",
             "this build has no encoder for them, so a file to decode cannot be made " +
             "here; the routing above is what is checkable");
        Skip("an ogg decodes through NVorbis",
             "the only Vorbis writer here is the Wwise one, which is not an ogg; " +
             "--selftest covers that encoder against vgmstream");
        Skip("MS-ADPCM, IMA-ADPCM and mp3-in-wav fall through to vgmstream",
             "encoding one correctly by hand would make this test lie when the encoder " +
             "is what is wrong; the dat case above exercises the same fallback route");

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>The message a file is refused with, or null when it decoded.</summary>
    private static string Refusal(string path)
    {
        try { _ = AudioDecode.ToPcm(path); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    private static int MaxDelta(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length) / 2;
        var max = 0;
        for (var i = 0; i < n; i++)
            max = Math.Max(max, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(a.AsSpan(i * 2, 2)) -
                                         BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i * 2, 2))));
        return max;
    }

    // ---- the files this test writes -----------------------------------------

    /// <summary>
    /// A rising chirp rather than a steady tone. A tone repeats, so a decode offset by a
    /// whole period compares equal to the source and a misalignment passes.
    /// </summary>
    private static short[] Chirp(int frames, int channels)
    {
        var s = new short[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / Rate;
            for (var c = 0; c < channels; c++)
            {
                var v = Math.Sin(2 * Math.PI * (200 + 900 * t) * t) * (0.7 - 0.1 * c);
                s[i * channels + c] = (short)Math.Clamp(Math.Round(v * 32767), short.MinValue, short.MaxValue);
            }
        }
        return s;
    }

    private static byte[] Pcm16(short[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2, 2), samples[i]);
        return b;
    }

    /// <param name="tag">1 for PCM, 3 for IEEE float, which is all this needs.</param>
    private static byte[] Wav(int tag, int bits, int channels, short[] samples)
    {
        byte[] data;
        if (tag == 3)
        {
            data = new byte[samples.Length * 4];
            for (var i = 0; i < samples.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4, 4), samples[i] / 32767f);
        }
        else data = Pcm16(samples);

        return Riff(Fmt(tag, channels, Rate, bits), data);
    }

    private static byte[] G711Wav(short[] samples, bool muLaw)
    {
        var data = new byte[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            data[i] = muLaw ? NAudio.Codecs.MuLawEncoder.LinearToMuLawSample(samples[i])
                            : NAudio.Codecs.ALawEncoder.LinearToALawSample(samples[i]);
        return Riff(Fmt(muLaw ? 7 : 6, 2, Rate, 8), data);
    }

    private static byte[] Fmt(int tag, int channels, int rate, int bits)
    {
        var block = channels * bits / 8;
        var f = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0), (ushort)tag);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(8), (uint)(rate * block));
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(12), (ushort)block);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(14), (ushort)bits);
        return f;
    }

    private static byte[] Riff(byte[] fmt, byte[] data)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(4 + 8 + fmt.Length + 8 + data.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(fmt.Length);
        w.Write(fmt);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(data.Length);
        w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Append the smpl chunk an editor writes when a wav has a loop region: one loop over
    /// the middle half, played for ever. This is what a decoder has to be told to ignore.
    /// </summary>
    private static byte[] Loop(byte[] wav, int frames)
    {
        using var ms = new MemoryStream();
        ms.Write(wav);
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("smpl"));
        w.Write(36 + 24);
        foreach (var v in new[] { 0, 0, (int)(1_000_000_000L / Rate), 60, 0, 0, 0, 1, 0 }) w.Write(v);
        foreach (var v in new[] { 0, 0, frames / 4, frames * 3 / 4, 0, 0 }) w.Write(v);
        w.Flush();

        var all = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(all.AsSpan(4), (uint)(all.Length - 8));
        return all;
    }

    /// <summary>
    /// COMM and SSND, 16-bit big-endian, which is the whole of an AIFF. Written here
    /// because this project has no aiff writer and the point is to hand vgmstream a
    /// container nothing in this process can read.
    /// </summary>
    private static byte[] Aiff(short[] samples)
    {
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16BigEndian(pcm.AsSpan(i * 2, 2), samples[i]);

        var comm = new byte[18];
        BinaryPrimitives.WriteInt16BigEndian(comm.AsSpan(0), 2);
        BinaryPrimitives.WriteUInt32BigEndian(comm.AsSpan(2), (uint)(samples.Length / 2));
        BinaryPrimitives.WriteInt16BigEndian(comm.AsSpan(6), 16);
        Extended80(Rate).CopyTo(comm.AsSpan(8));

        var ssnd = new byte[8 + pcm.Length];
        pcm.CopyTo(ssnd.AsSpan(8));

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("FORM"));
        w.Write(0);
        w.Write(Encoding.ASCII.GetBytes("AIFF"));
        WriteChunk(w, "COMM", comm);
        WriteChunk(w, "SSND", ssnd);
        w.Flush();

        var all = ms.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(all.AsSpan(4), (uint)(all.Length - 8));
        return all;

        static void WriteChunk(BinaryWriter w, string id, byte[] data)
        {
            w.Write(Encoding.ASCII.GetBytes(id));
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)data.Length);
            w.Write(size);
            w.Write(data);
            if (data.Length % 2 == 1) w.Write((byte)0);
        }
    }

    /// <summary>
    /// The sample rate as the 80-bit extended float AIFF stores it. Built with integer
    /// shifts rather than from a double: the mantissa is the rate itself moved up to the
    /// top of a 64-bit field, so it is exact, and a rate written approximately comes back
    /// as 44099 Hz.
    /// </summary>
    private static byte[] Extended80(int rate)
    {
        var bits = 64 - BitOperations.LeadingZeroCount((ulong)rate);
        var b = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0), (ushort)(16382 + bits));
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(2), (ulong)rate << (64 - bits));
        return b;
    }
}
