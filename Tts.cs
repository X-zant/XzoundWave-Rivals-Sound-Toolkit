using System.IO;
using System.Reflection;

namespace MRAudioKit;

/// <summary>
/// Spoken numbers via SAPI, the speech engine already present on every Windows
/// install. Bound late through COM so there is no NuGet package and nothing to ship.
/// Quality is irrelevant here — the clip only has to be recognisable as a number.
/// </summary>
public static class Tts
{
    private const int SSFMCreateForWrite = 3;

    public static bool IsAvailable
    {
        get
        {
            try { return Type.GetTypeFromProgID("SAPI.SpVoice") is not null; }
            catch { return false; }
        }
    }

    private static object Invoke(object target, string member, BindingFlags flags, params object[] args)
        => target.GetType().InvokeMember(member, flags, null, target, args);

    /// <summary>Speak <paramref name="text"/> into a .wav file. Returns the path.</summary>
    public static string SpeakToWav(string text, string wavPath, int rateBoost = 0)
    {
        var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice")
                        ?? throw new InvalidOperationException("SAPI is not available on this machine.");
        var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream")
                         ?? throw new InvalidOperationException("SAPI file stream is not available.");

        var voice = Activator.CreateInstance(voiceType);
        var stream = Activator.CreateInstance(streamType);
        try
        {
            Invoke(stream, "Open", BindingFlags.InvokeMethod, wavPath, SSFMCreateForWrite, false);
            Invoke(voice, "Rate", BindingFlags.SetProperty, rateBoost);
            Invoke(voice, "AudioOutputStream", BindingFlags.SetProperty, stream);
            Invoke(voice, "Speak", BindingFlags.InvokeMethod, text, 0);
            Invoke(voice, "AudioOutputStream", BindingFlags.SetProperty, [null]);
        }
        finally
        {
            try { Invoke(stream, "Close", BindingFlags.InvokeMethod); } catch { }
            if (stream is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(stream);
            if (voice is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(voice);
        }
        return wavPath;
    }

    /// <summary>
    /// Generate {first}.wem … {last}.wem plus a silent clip, in the folder the
    /// numbered-test-bank builder expects.
    /// </summary>
    /// <param name="vorbis">
    /// When supplied, each spoken WAV is handed to Wwise's encoder instead of being
    /// wrapped as PCM. That is the only route to real Vorbis; without it the clips are
    /// PCM, which the game's banks do not declare.
    /// </param>
    public static int GenerateNumbers(string outDir, int first, int last,
                                      Action<string> progress, int rateBoost = 2,
                                      Func<string, (byte[] Bytes, string Error)> vorbis = null)
    {
        Directory.CreateDirectory(outDir);
        var tmp = Path.Combine(Path.GetTempPath(), "MRAudioKit", "tts");
        Directory.CreateDirectory(tmp);

        var made = 0;
        int rate = 0; short ch = 0;
        for (var n = first; n <= last; n++)
        {
            var wav = Path.Combine(tmp, $"{n}.wav");
            // Digits, not "four hundred and thirty-three" -- shorter, and far easier to
            // catch when a sound is half a second long.
            SpeakToWav(string.Join(" ", n.ToString().ToCharArray()), wav, rateBoost);
            var w = WwiseWem.ReadWav(File.ReadAllBytes(wav));
            rate = w.SampleRate; ch = w.Channels;

            byte[] wem = null;
            if (vorbis is not null)
            {
                var (bytes, err) = vorbis(wav);
                if (bytes is null)
                    throw new InvalidOperationException($"Vorbis encoding failed on {n}: {err}");
                wem = bytes;
            }
            wem ??= WwiseWem.Write(w);
            File.WriteAllBytes(Path.Combine(outDir, $"{n}.wem"), wem);
            File.Delete(wav);
            made++;
            if (made % 25 == 0) progress($"generating spoken numbers… {made}/{last - first + 1}");
        }

        // A silent clip of the same format, so entries past the end of the range are
        // audibly nothing rather than leftover vanilla audio.
        if (rate > 0)
        {
            var silentWav = Path.Combine(tmp, "silent.wav");
            var half = new byte[rate * ch * 2 / 2];
            byte[] silentWem = null;
            if (vorbis is not null)
            {
                File.WriteAllBytes(silentWav, BuildWav(rate, ch, half));
                silentWem = vorbis(silentWav).Bytes;
                File.Delete(silentWav);
            }
            silentWem ??= WwiseWem.Write(new WwiseWem.WavData(rate, ch, 16, half));
            File.WriteAllBytes(Path.Combine(outDir, "0-Silent.wem"), silentWem);
        }

        progress($"generated {made} spoken number(s) at {rate} Hz, {ch}ch " +
                 $"as {(vorbis is null ? "PCM" : "Vorbis")} into {outDir}");
        return made;
    }

    /// <summary>A plain 16-bit WAV, for handing to an external encoder.</summary>
    private static byte[] BuildWav(int rate, short ch, byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        var blockAlign = (short)(ch * 2);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write((uint)(36 + pcm.Length));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        bw.Write(16u);
        bw.Write((ushort)1);
        bw.Write((ushort)ch);
        bw.Write((uint)rate);
        bw.Write((uint)(rate * blockAlign));
        bw.Write((ushort)blockAlign);
        bw.Write((ushort)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write((uint)pcm.Length);
        bw.Write(pcm);
        bw.Flush();
        return ms.ToArray();
    }
}
