using System.IO;
using NAudio.Wave;

using Xzound;

namespace MRAudioKit;

/// <summary>
/// Turns whatever audio file someone drags in into the 16-bit PCM the encoder wants.
///
/// mp3, m4a, aac, wma and flac are decoded by Media Foundation, which is part of
/// Windows -- no ffmpeg to ship, license or keep updated. Ogg Vorbis is the one
/// common format Media Foundation does not carry, so NVorbis covers it. Plain wav is
/// read directly, since most drops are wav and the fast path should not involve COM.
///
/// Everything comes out as interleaved 16-bit little-endian PCM at the source's own
/// sample rate. Rate is never changed: resampling would be a quality loss the author
/// did not ask for, and XzoundCore encodes any rate.
/// </summary>
public static class AudioDecode
{
    /// <summary>Extensions we can turn into a wem, lowercase, with the dot.</summary>
    public static readonly string[] Extensions =
        [".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".wem"];

    /// <summary>An OpenFileDialog filter listing everything we accept.</summary>
    public const string DialogFilter =
        "Audio|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aac;*.wma;*.wem|" +
        "Wwise media|*.wem|All files|*.*";

    public static bool CanDecode(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>True when the file is already a wem and needs no conversion.</summary>
    public static bool IsWem(string path) =>
        Path.GetExtension(path).Equals(".wem", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decode to 16-bit PCM. Throws with a message worth showing the user -- these
    /// are files they chose, so "what was wrong with it" matters more than a stack.
    /// </summary>
    public static WwiseWem.WavData ToPcm(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            var data = ext switch
            {
                ".ogg" => FromVorbis(path),
                ".wav" => FromWav(path),
                _      => FromMediaFoundation(path),
            };
            if (data.Pcm.Length == 0)
                throw new InvalidDataException("decoded to no audio at all");
            return Downmix(data);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidDataException(Describe(ext, ex), ex);
        }
    }

    private static string Describe(string ext, Exception ex) => ext switch
    {
        ".m4a" or ".aac" or ".wma" or ".mp3" or ".flac" =>
            $"Windows could not decode this {ext.TrimStart('.')} ({ex.Message}). " +
            "Converting it to wav first will work.",
        ".ogg" => $"not readable as Ogg Vorbis ({ex.Message})",
        _ => ex.Message,
    };

    // ---- readers -----------------------------------------------------------

    /// <summary>
    /// A plain wav, including float and 8/24/32-bit, without going through COM.
    /// Anything unusual enough that NAudio's own reader balks falls through to
    /// Media Foundation, which handles a surprising amount of malformed wav.
    /// </summary>
    private static WwiseWem.WavData FromWav(string path)
    {
        try
        {
            using var r = new WaveFileReader(path);
            return Read(r);
        }
        catch { return FromMediaFoundation(path); }
    }

    private static WwiseWem.WavData FromMediaFoundation(string path)
    {
        using var r = new MediaFoundationReader(path);
        return Read(r);
    }

    private static WwiseWem.WavData FromVorbis(string path)
    {
        using var v = new NVorbis.VorbisReader(path);
        var ch = v.Channels;
        var floats = new float[ch * 4096];
        using var ms = new MemoryStream();
        int n;
        while ((n = v.ReadSamples(floats, 0, floats.Length)) > 0)
            WriteSamples(ms, floats.AsSpan(0, n));
        return new WwiseWem.WavData(v.SampleRate, (short)ch, 16, ms.ToArray());
    }

    /// <summary>
    /// Pull 16-bit PCM out of any NAudio stream. Float and higher-bit sources are
    /// converted through the sample provider so the clamp and rounding are NAudio's
    /// rather than ours.
    /// </summary>
    private static WwiseWem.WavData Read(WaveStream stream)
    {
        var fmt = stream.WaveFormat;
        if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            using var raw = new MemoryStream();
            stream.CopyTo(raw);
            return new WwiseWem.WavData(fmt.SampleRate, (short)fmt.Channels, 16, raw.ToArray());
        }

        var sp = stream.ToSampleProvider();
        var buf = new float[fmt.Channels * 4096];
        using var ms = new MemoryStream();
        int n;
        while ((n = sp.Read(buf)) > 0)
            WriteSamples(ms, buf.AsSpan(0, n));
        return new WwiseWem.WavData(fmt.SampleRate, (short)fmt.Channels, 16, ms.ToArray());
    }

    private static void WriteSamples(Stream to, ReadOnlySpan<float> samples)
    {
        Span<byte> two = stackalloc byte[2];
        foreach (var f in samples)
        {
            var s = (int)MathF.Round(Math.Clamp(f, -1f, 1f) * 32767f);
            two[0] = (byte)s;
            two[1] = (byte)(s >> 8);
            to.Write(two);
        }
    }

    // ---- channels ----------------------------------------------------------

    /// <summary>
    /// The game's banks are mono or stereo, and the encoder writes a channel config
    /// for those two. A 5.1 source is folded to stereo rather than rejected -- the
    /// author dragged in music, and a refusal helps nobody.
    /// </summary>
    private static WwiseWem.WavData Downmix(WwiseWem.WavData d)
    {
        if (d.Channels <= 2) return d;

        var frames = d.Pcm.Length / 2 / d.Channels;
        var outPcm = new byte[frames * 2 * 2];
        for (var f = 0; f < frames; f++)
        {
            int l = 0, r = 0;
            for (var c = 0; c < d.Channels; c++)
            {
                var s = BitConverter.ToInt16(d.Pcm, (f * d.Channels + c) * 2);
                if (c % 2 == 0) l += s; else r += s;
            }
            var half = (d.Channels + 1) / 2;
            var other = d.Channels / 2;
            Write16(outPcm, f * 4, l / Math.Max(1, half));
            Write16(outPcm, f * 4 + 2, r / Math.Max(1, other));
        }
        return new WwiseWem.WavData(d.SampleRate, 2, 16, outPcm);

        static void Write16(byte[] b, int at, int v)
        {
            v = Math.Clamp(v, short.MinValue, short.MaxValue);
            b[at] = (byte)v;
            b[at + 1] = (byte)(v >> 8);
        }
    }
}
