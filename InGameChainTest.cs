using System.IO;
using NAudio.Wave;

namespace MRAudioKit;

/// <summary>
/// Previewing a sound the way the .bnk plays it.
///
/// The failure worth guarding against is a chain that is built, reported in the status
/// line, and does nothing to the samples -- the preview would sound identical while
/// claiming to be "as in game", which is worse than not having the switch. So these
/// read the samples that come out and check them against what was asked for.
///
/// A synthetic tone is used rather than a game sound: the expected answer for a -6 dB
/// trim on a known signal is arithmetic, not a judgement.
/// </summary>
public static class InGameChainTest
{
    /// <summary>XzoundWave.exe --ingame</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        const int Rate = 48000;
        const int Samples = Rate;              // one second
        var wav = Path.Combine(Path.GetTempPath(), $"xzw-ingame-{Guid.NewGuid():N}.wav");
        WriteTone(wav, Rate, Samples, 0.5f);

        try
        {
            W("nothing applied");
            var plain = Render(wav, null, out var said);
            Check("a null chain plays the file untouched",
                  plain.Count == Samples && Math.Abs(Peak(plain) - 0.5f) < 0.01f,
                  $"{plain.Count} samples, peak {Peak(plain):0.###}");
            Check("and says nothing was applied", said.Length == 0, said);

            W("");
            W("volume");
            // -6.02 dB is half the amplitude. Anything that merely reports a trim
            // without applying one leaves the peak at 0.5 and fails here.
            var quiet = Render(wav, Applied(("Volume", 0x00, -6.0206f)), out said);
            Check("a -6 dB trim halves the amplitude",
                  Math.Abs(Peak(quiet) - 0.25f) < 0.005f, $"peak {Peak(quiet):0.####}");
            Check("and the status line says so", said.Contains("dB"), said);

            // Volume and make-up gain are separate properties that Wwise adds together.
            var both = Render(wav, Applied(("Volume", 0x00, -3f), ("Make-up gain", 0x06, -3f)), out _);
            var one = Render(wav, Applied(("Volume", 0x00, -6f)), out _);
            Check("volume and make-up gain add up rather than one winning",
                  Math.Abs(Peak(both) - Peak(one)) < 0.005f,
                  $"-3 and -3 gives {Peak(both):0.####}, -6 gives {Peak(one):0.####}");

            W("");
            W("pitch");
            // +1200 cents is an octave: Wwise resamples, so the clip also plays twice
            // as fast and the reported rate doubles.
            var up = RenderFormat(wav, Applied(("Pitch", 0x02, 1200f)), out said);
            Check("an octave up doubles the playback rate", up.SampleRate == Rate * 2,
                  $"{up.SampleRate} Hz from {Rate} Hz");
            Check("and is described in cents", said.Contains("cents"), said);

            var down = RenderFormat(wav, Applied(("Pitch", 0x02, -1200f)), out _);
            Check("an octave down halves it", down.SampleRate == Rate / 2, $"{down.SampleRate} Hz");

            W("");
            W("delay and loop");
            var late = Render(wav, Applied(("Initial delay", 0x3B, 0.25f)), out said);
            Check("a delay prepends silence of the right length",
                  Math.Abs(late.Count - (Samples + Rate / 4)) < 64 && Silent(late, Rate / 4),
                  $"{late.Count} samples, expected {Samples + Rate / 4}");
            Check("and is described in seconds", said.Contains('s'), said);

            var twice = Render(wav, Loop(2), out said);
            Check("a loop count of 2 plays it twice",
                  Math.Abs(twice.Count - Samples * 2) < 64, $"{twice.Count} samples");

            var forever = Render(wav, Loop(0), out said);
            Check($"an endless loop is capped at {InGameChain.MaxLoops} passes",
                  Math.Abs(forever.Count - Samples * InGameChain.MaxLoops) < 64,
                  $"{forever.Count} samples");
            Check("and the cap is stated rather than hidden", said.Contains("loops"), said);

            W("");
            W("what it does not claim to do");
            var filtered = Applied(("Low-pass", 0x03, 40f));
            Check("a filter is named as not reproduced",
                  InGameChain.Unapplied(filtered).Contains("filter"),
                  InGameChain.Unapplied(filtered));
            Check("a sound with nothing exotic has nothing to disclaim",
                  InGameChain.Unapplied(Applied(("Volume", 0x00, -5f))).Length == 0);

            // Everything together, which is the case the ult music actually is.
            W("");
            W("all at once");
            var all = Render(wav, Applied(("Volume", 0x00, -6.0206f), ("Initial delay", 0x3B, 0.1f)),
                             out said);
            Check("a trim and a delay both land",
                  Math.Abs(Peak(all) - 0.25f) < 0.005f &&
                  Math.Abs(all.Count - (Samples + Rate / 10)) < 64,
                  $"peak {Peak(all):0.####}, {all.Count} samples");
            W($"    reported as: {said}");
        }
        finally { try { File.Delete(wav); } catch { } }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    private static WwiseProps.Applied Applied(params (string Name, byte Id, float Value)[] props)
    {
        var a = new WwiseProps.Applied();
        foreach (var (name, id, value) in props)
            a.Props.Add(new WwiseProps.Prop(id, name, UnitOf(id), value, 0, "sound"));
        return a;
    }

    private static WwiseProps.Applied Loop(int count) => new() { LoopCount = count };

    private static WwiseProps.Unit UnitOf(byte id) => id switch
    {
        0x00 or 0x06 => WwiseProps.Unit.Db,
        0x02 => WwiseProps.Unit.Cents,
        0x03 or 0x04 => WwiseProps.Unit.Percent,
        0x0F or 0x3B => WwiseProps.Unit.Seconds,
        _ => WwiseProps.Unit.Number,
    };

    /// <summary>Every sample the chain produces, mono-summed.</summary>
    private static List<float> Render(string wav, WwiseProps.Applied applied, out string said)
    {
        using var reader = new WaveFileReader(wav);
        var chain = InGameChain.Build(reader, applied, out said);
        var channels = chain.WaveFormat.Channels;

        var all = new List<float>();
        var buffer = new float[4096];
        int n;
        while ((n = chain.Read(buffer)) > 0)
            for (var i = 0; i < n; i += channels)
                all.Add(buffer[i]);
        return all;
    }

    private static WaveFormat RenderFormat(string wav, WwiseProps.Applied applied, out string said)
    {
        using var reader = new WaveFileReader(wav);
        return InGameChain.Build(reader, applied, out said).WaveFormat;
    }

    private static float Peak(List<float> s) => s.Count == 0 ? 0 : s.Max(Math.Abs);

    private static bool Silent(List<float> s, int first) =>
        s.Take(first).All(v => Math.Abs(v) < 1e-6f);

    /// <summary>A one-second tone at a known amplitude, so the arithmetic is checkable.</summary>
    private static void WriteTone(string path, int rate, int samples, float amplitude)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, 1));
        var buffer = new float[samples];
        for (var i = 0; i < samples; i++)
            buffer[i] = amplitude * (float)Math.Sin(2 * Math.PI * 440 * i / rate);
        writer.WriteSamples(buffer, 0, buffer.Length);
    }
}
