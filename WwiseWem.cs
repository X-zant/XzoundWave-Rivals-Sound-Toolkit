using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MRAudioKit;

/// <summary>
/// Writes a Wwise PCM .wem around raw 16-bit PCM.
///
/// A PCM wem is an ordinary RIFF/WAVE with WAVE_FORMAT_EXTENSIBLE (0xFFFE) and a
/// six-byte extension, plus a 16-byte "hash" chunk, then "data". No encoder is
/// involved -- which is why PCM is the fallback whenever Wwise's own Vorbis encoder
/// is not on the machine.
///
/// Vorbis would be smaller and is what the game ships, but producing it needs
/// WwiseConsole.exe. For test tones and spoken numbers, size is irrelevant.
/// </summary>
public static class WwiseWem
{
    public sealed record WavData(int SampleRate, short Channels, short Bits, byte[] Pcm);

    /// <summary>Pull format and samples out of a normal .wav.</summary>
    public static WavData ReadWav(byte[] wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF"
                            || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            throw new InvalidDataException("not a RIFF/WAVE file");

        int rate = 0; short ch = 0, bits = 0;
        byte[] pcm = null;
        var p = 12;
        while (p + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, p, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(p + 4, 4));
            var body = p + 8;
            if (id == "fmt " && size >= 16)
            {
                ch = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 2, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(body + 4, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 14, 2));
            }
            else if (id == "data")
            {
                var n = Math.Min(size, wav.Length - body);
                pcm = new byte[n];
                Buffer.BlockCopy(wav, body, pcm, 0, n);
            }
            p = body + size + (size & 1);
        }
        if (pcm is null || rate == 0 || ch == 0)
            throw new InvalidDataException("wav has no fmt/data chunk");
        return new WavData(rate, ch, bits, pcm);
    }

    public static byte[] FromWav(byte[] wav) => Write(ReadWav(wav));

    public static byte[] Write(WavData w)
    {
        if (w.Bits != 16)
            throw new NotSupportedException($"16-bit PCM only, got {w.Bits}-bit");

        var blockAlign = (short)(w.Channels * (w.Bits / 8));
        var byteRate = w.SampleRate * blockAlign;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);

        void Tag(string s) => bw.Write(Encoding.ASCII.GetBytes(s));

        // 12 (RIFF/WAVE) + 32 (fmt) + 24 (hash) + 8 (data header) = 76 bytes of
        // container; the RIFF size field counts everything after its own 8 bytes.
        Tag("RIFF");
        bw.Write((uint)(68 + w.Pcm.Length));
        Tag("WAVE");

        Tag("fmt ");
        bw.Write(24u);
        bw.Write((ushort)0xFFFE);        // WAVE_FORMAT_EXTENSIBLE
        bw.Write((ushort)w.Channels);
        bw.Write((uint)w.SampleRate);
        bw.Write((uint)byteRate);
        bw.Write((ushort)blockAlign);
        bw.Write((ushort)w.Bits);
        bw.Write((ushort)6);             // cbSize: Wwise's short extension, not the usual 22
        bw.Write(0u);                    // channel mask
        bw.Write((ushort)0x4101);        // Wwise extension word

        // A 16-byte "hash" chunk sits between fmt and data in Wwise's PCM layout.
        // Nothing observed reads it back, so the content is a digest of the samples --
        // the right size and stable for the same input.
        Tag("hash");
        bw.Write(16u);
        bw.Write(MD5.HashData(w.Pcm));

        Tag("data");
        bw.Write((uint)w.Pcm.Length);
        bw.Write(w.Pcm);

        bw.Flush();
        return ms.ToArray();
    }
}
