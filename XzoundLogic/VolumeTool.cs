using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using Xzound;

namespace MRAudioKit;

/// <summary>
/// Changes how loud a sound is, by decoding it, scaling the samples and encoding it
/// again.
///
/// The level is always applied to the ORIGINAL audio, never to the last result.
/// Scaling a lossy file repeatedly compounds the loss, so 1.0 -> 0.8 -> 0.6 sounds
/// worse than going straight to 0.6; storing the level against the sound and
/// re-deriving from the shipped bytes each time avoids that entirely.
/// </summary>
public static class VolumeTool
{
    /// <summary>Sensible bounds. Past about 4x everything clips into distortion.</summary>
    public const double MinGain = 0.05;
    public const double MaxGain = 4.0;

    public sealed record Result(byte[] Bytes, int ClippedSamples, int TotalSamples)
    {
        public double ClippedPercent => TotalSamples == 0 ? 0 : 100.0 * ClippedSamples / TotalSamples;
    }

    /// <summary>
    /// Scale a wem. Returns null when the source cannot be decoded, which is a reason
    /// to leave the sound alone rather than to fail the whole run.
    /// </summary>
    public static Result Scale(byte[] wem, double gain, string vgmstreamPath)
    {
        if (wem is null || wem.Length == 0) return null;
        gain = Math.Clamp(gain, MinGain, MaxGain);

        var pcm = Decode(wem, vgmstreamPath);
        if (pcm is null) return null;

        var samples = pcm.Pcm.Length / 2;
        var clipped = 0;
        var data = pcm.Pcm;
        for (var i = 0; i < samples; i++)
        {
            var v = (int)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 2, 2)) * gain);
            if (v > short.MaxValue) { v = short.MaxValue; clipped++; }
            else if (v < short.MinValue) { v = short.MinValue; clipped++; }
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2, 2), (short)v);
        }

        try { return new Result(XzoundCore.Encode(pcm), clipped, samples); }
        catch { return null; }
    }

    /// <summary>
    /// wem -> 16-bit PCM. A PCM wem is read directly; Vorbis needs vgmstream, the same
    /// decoder the preview already uses.
    ///
    /// With <see cref="AudioDecode.NoLoop"/>, for the reason given there and because this
    /// one SHIPS what it decodes. Levelling media 972121045 without it replaced 44.333 s
    /// of music with 77.937 s carrying a repeat of the loop and vgmstream's ten second
    /// fade baked into the audio, at nearly twice the size of what it was given.
    /// </summary>
    private static WwiseWem.WavData Decode(byte[] wem, string vgmstreamPath)
    {
        if (FormatTag(wem) == 0xFFFE)
        {
            try { return WwiseWem.ReadWav(wem); } catch { return null; }
        }

        if (string.IsNullOrWhiteSpace(vgmstreamPath) || !File.Exists(vgmstreamPath)) return null;

        var tmp = Path.Combine(Path.GetTempPath(), "XzoundWave", "vol_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var src = Path.Combine(tmp, "in.wem");
            var dst = Path.Combine(tmp, "out.wav");
            File.WriteAllBytes(src, wem);

            var psi = new System.Diagnostics.ProcessStartInfo(vgmstreamPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(AudioDecode.NoLoop);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(dst);
            psi.ArgumentList.Add(src);
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                proc!.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit();
            }
            if (!File.Exists(dst)) return null;
            return WwiseWem.ReadWav(File.ReadAllBytes(dst));
        }
        catch { return null; }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    private static ushort FormatTag(byte[] wem)
    {
        var p = 12;
        while (p + 8 <= wem.Length)
        {
            var id = Encoding.ASCII.GetString(wem, p, 4);
            var sz = (int)BinaryPrimitives.ReadUInt32LittleEndian(wem.AsSpan(p + 4, 4));
            if (sz < 0 || p + 8 + sz > wem.Length) break;
            if (id == "fmt ") return BinaryPrimitives.ReadUInt16LittleEndian(wem.AsSpan(p + 8, 2));
            p += 8 + sz + (sz & 1);
        }
        return 0;
    }

    /// <summary>
    /// A suffix for the output folder or pak, so exports at different levels do not
    /// overwrite each other: 1 -> "Vol-1.0", 0.8 -> "Vol-0.8".
    ///
    /// Formatted with the invariant culture because this becomes a directory name. The
    /// machine's own culture decides what a decimal separator looks like, so a German
    /// locale wrote "Vol-0,8" and the same export made on two machines landed in two
    /// differently spelled folders. Swapping the comma back afterwards -- which is what
    /// this did -- only ever knew about that one separator, and said nothing about
    /// group separators or any of the other punctuation a locale is free to pick.
    /// </summary>
    public static string Label(double gain) =>
        "Vol-" + gain.ToString("0.0#", CultureInfo.InvariantCulture);
}
