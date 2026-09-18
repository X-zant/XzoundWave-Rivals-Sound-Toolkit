using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OggVorbisEncoder;
using Encoding = System.Text.Encoding;
using System.Reflection;
using Xzound.Vorbis;

namespace Xzound;

/// <summary>
/// XzoundCore — encodes 16-bit PCM into a Wwise Vorbis .wem without an Audiokinetic
/// install, an account, or a reference file to copy fields from.
///
/// The compression is not ours — OggVorbisEncoder (a managed libvorbis port) does it.
/// What this class adds is the container: Wwise strips the Ogg framing and stores the
/// raw Vorbis packets each prefixed by a 2-byte length, with the stream's parameters
/// hoisted into the fmt chunk instead of the id/comment headers, and the codebooks
/// referenced by index into a table the runtime already carries.
///
/// The layout was recovered by measurement, not guesswork: offsets were brute-forced
/// against shipped specimens, the packet chain verified to land exactly on the end of
/// the data chunk, and the remaining fields identified by surveying 4000 shipped .wem
/// files straight out of the paks (see ExtStats). Offsets are into the 48-byte block
/// following the 18-byte WAVEFORMATEX, and correspond to Wwise's AkVorbisInfo:
///
///   ext+0x02  uint32  channel config          mono 0x4101, stereo 0x3102
///   ext+0x06  uint32  dwTotalPCMFrames
///   ext+0x0a  uint32  setup packet span       2 + payload
///   ext+0x0e  uint32  data size after the seek table
///   ext+0x16  uint32  dwSeekTableSize         = the setup packet's offset
///   ext+0x1a  uint32  dwVorbisDataOffset      first audio packet
///   ext+0x1e  uint16  uMaxPacketSize          largest audio packet -- REQUIRED
///   ext+0x2e  uint8   blocksize_0 exponent
///   ext+0x2f  uint8   blocksize_1 exponent
///
/// uMaxPacketSize sizes the runtime's packet buffer. Leaving it zero makes every
/// sound silent in game while still decoding perfectly in vgmstream, which is why it
/// went unnoticed for so long: no offline tool reads it. It matched the largest audio
/// packet on all 4000 surveyed files, and a test bank with only this field set (and
/// the loop, granule, alloc and codebook-hash fields all left zero) played correctly
/// in game — so the rest are written as zero rather than guessed.
/// </summary>
public static class XzoundCore
{
    /// <summary>
    /// Validated encoder quality. libvorbis picks its floor/residue templates from
    /// this value, and not every template it can produce is one this decode path
    /// accepts — 0.4 emits residues with trailing empty classifications that fail to
    /// open at >=32 kHz, and 0.6 decodes to noise on mono. 0.5 was measured good on
    /// 22050/32000/44100/48000 mono and on real stereo and music sources (14-25 dB).
    /// Change it only with the same matrix re-run.
    /// </summary>
    public const float DefaultQuality = 0.5f;

    public const uint ChannelConfigMono = 0x4101;
    public const uint ChannelConfigStereo = 0x3102;

    private static Vorbis.CodebookLibrary _library;
    private static readonly object Gate = new();

    /// <summary>The shared aoTuV table, embedded so no side file is needed.</summary>
    public static Vorbis.CodebookLibrary Library
    {
        get
        {
            lock (Gate)
            {
                if (_library is not null) return _library;
                var asm = Assembly.GetExecutingAssembly();
                var name = asm.GetManifestResourceNames()
                              .First(n => n.EndsWith("packed_codebooks_aoTuV_603.bin"));
                using var st = asm.GetManifestResourceStream(name)!;
                using var ms = new MemoryStream();
                st.CopyTo(ms);
                return _library = new Vorbis.CodebookLibrary(ms.ToArray());
            }
        }
    }

    /// <summary>PCM in, Wwise Vorbis wem out.</summary>
    public static byte[] Encode(WwiseWem.WavData wav, float quality = DefaultQuality)
    {
        // Diagnostic override: libvorbis picks its residue/floor templates from the
        // quality setting, so sweeping it changes the configuration we emit.
        var qEnv = Environment.GetEnvironmentVariable("MRAK_QUALITY");
        if (float.TryParse(qEnv, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out var q)) quality = q;

        if (wav.Bits != 16) throw new NotSupportedException("16-bit PCM input only");
        if (wav.Channels is not (1 or 2)) throw new NotSupportedException("mono or stereo only");

        var ogg = EncodeOgg(wav, quality);
        var packets = Demux(ogg);
        if (packets.Count < 3)
            throw new InvalidDataException("encoder produced no Vorbis headers");

        // Vorbis always emits three headers: identification, comment, setup. Wwise
        // keeps only the setup packet -- everything the other two carried (rate,
        // channels, blocksizes) is what the fmt chunk exists to hold.
        // Re-emit the setup packet in Wwise's stripped form: codebooks become 10-bit
        // indices into the shared table, and the always-constant fields disappear.
        // Diagnostic hook: the raw standard setup packet, before any of our rewriting.
        var dumpTo = Environment.GetEnvironmentVariable("MRAK_DUMP_STD_SETUP");
        if (!string.IsNullOrEmpty(dumpTo)) File.WriteAllBytes(dumpTo, packets[2]);

        var parsedSetup = SetupPacket.ParseStandard(packets[2], wav.Channels);
        var setup = parsedSetup.WriteWwise(Library, wav.Channels);
        var audio = packets.Skip(3).ToList();
        var (bs0, bs1) = BlockSizes(packets[0]);
        var totalSamples = wav.Pcm.Length / 2 / wav.Channels;

        using var data = new MemoryStream();
        var maxPacket = 0;
        void Packet(byte[] p)
        {
            var len = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)p.Length);
            data.Write(len);
            data.Write(p);
            if (p.Length > maxPacket) maxPacket = p.Length;
        }

        // Measured layout of a shipped file, verified on five specimens:
        //   data[0 .. ext+0x16)              seek table (grows with duration)
        //   data[ext+0x16]                   uint16 setup payload size
        //   then the setup payload
        //   data[ext+0x1a .. end)            audio packets, 2-byte length prefixed
        //   ext+0x0a  = 2 + payload size     (the setup packet's whole span)
        // We emit no seek table, so the setup packet sits at offset 0.
        // Seek table first. Every shipped file has one: (samples, bytes) per block of
        // ~16384 samples, trailing partial block omitted. vgmstream decodes linearly
        // and never reads it, which is why its absence went unnoticed offline.
        var seek = BuildSeekTable(audio, parsedSetup, bs0, bs1);
        data.Write(seek);
        var setupOffset = (uint)data.Length;

        var sizePrefix = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(sizePrefix, (ushort)setup.Length);
        data.Write(sizePrefix);
        data.Write(setup);
        var firstAudioOffset = (uint)data.Length;
        // Wwise stores audio packets WITHOUT the leading packet-type bit that standard
        // Vorbis puts in front of every audio packet (it is always 0, and the reader
        // knows a packet is audio because the headers are elsewhere). ww2ogg calls the
        // inverse of this "mod packets" and re-inserts the bit when converting out.
        foreach (var p in audio) Packet(ModPacket(p, parsedSetup));
        var dataBytes = data.ToArray();

        var ext = new byte[48];
        void U32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(ext.AsSpan(o, 4), v);
        U32(0x02, wav.Channels == 1 ? ChannelConfigMono : ChannelConfigStereo);
        U32(0x06, (uint)totalSamples);
        U32(0x0a, (uint)(2 + setup.Length));      // setup packet span
        U32(0x0e, (uint)(dataBytes.Length - seek.Length));   // data size minus the seek table
        U32(0x16, setupOffset);                   // setup packet offset
        U32(0x1a, firstAudioOffset);

        // uMaxPacketSize. The runtime sizes its packet buffer from this, so a zero
        // here means every packet overflows a zero-length buffer and the sound is
        // silent -- which is exactly what the game did until this was written.
        // Verified equal to the largest audio packet on all 4000 shipped files the
        // survey could reach (--extstats); it is the ONLY one of the previously
        // unknown fields the game needs, measured in game with the others zeroed.
        BinaryPrimitives.WriteUInt16LittleEndian(ext.AsSpan(0x1e, 2), (ushort)maxPacket);

        ext[0x2e] = bs0;
        ext[0x2f] = bs1;

        // Deliberately left zero. Shipped files also carry uLoopEndExtra (0x14),
        // uLastGranuleExtra (0x20), uDecodeAllocSize / uDecodeX64AllocSize (0x22,
        // 0x26) and uHashCodebook (0x2a). A test bank that zeroed all of them and
        // set only uMaxPacketSize played correctly in game, so the runtime either
        // recomputes them or does not consult them. Writing a guessed value would
        // be worse than writing none: a wrong granule count truncates audio.

        return Riff(wav, ext, dataBytes);
    }

    // ---- container ---------------------------------------------------------

    private static byte[] Riff(WwiseWem.WavData wav, byte[] ext, byte[] data)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
        void Tag(string s) => bw.Write(Encoding.ASCII.GetBytes(s));

        // 12 + (8+66) + (8+16) + (8+data) ; the RIFF size counts all but its own 8.
        var total = 12 + 74 + 24 + 8 + data.Length;
        Tag("RIFF");
        bw.Write((uint)(total - 8));
        Tag("WAVE");

        Tag("fmt ");
        bw.Write(66u);
        bw.Write((ushort)0xFFFF);              // Wwise Vorbis
        bw.Write((ushort)wav.Channels);
        bw.Write((uint)wav.SampleRate);
        // Shipped files carry a real value here (num42: 4099), so mirror the
        // convention rather than leaving it zero.
        bw.Write((uint)(data.Length * wav.SampleRate /
                        Math.Max(1, wav.Pcm.Length / 2 / wav.Channels)));
        bw.Write((ushort)0);                   // block align: unused
        bw.Write((ushort)0);                   // bits: unused, 0 in every specimen
        bw.Write((ushort)48);                  // cbSize
        bw.Write(ext);

        Tag("hash");
        bw.Write(16u);
        bw.Write(MD5.HashData(data));

        Tag("data");
        bw.Write((uint)data.Length);
        bw.Write(data);

        bw.Flush();
        return ms.ToArray();
    }

    // ---- vorbis ------------------------------------------------------------

    private const int WriteBufferSize = 512;

    private static byte[] EncodeOgg(WwiseWem.WavData wav, float quality)
    {
        var info = VorbisInfo.InitVariableBitRate(wav.Channels, wav.SampleRate, quality);
        // Fixed serial: the number never reaches the wem (Ogg framing is discarded),
        // and a constant keeps identical input producing identical output.
        var oggStream = new OggStream(1);

        using var outMs = new MemoryStream();
        void Flush(bool force)
        {
            while (oggStream.PageOut(out var page, force))
            {
                outMs.Write(page.Header, 0, page.Header.Length);
                outMs.Write(page.Body, 0, page.Body.Length);
            }
        }

        // No comment tags: they inflate the packet Wwise throws away anyway.
        oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(new Comments()));
        oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        Flush(true);

        var samples = wav.Pcm.Length / 2 / wav.Channels;
        var buffer = new float[wav.Channels][];
        for (var c = 0; c < wav.Channels; c++) buffer[c] = new float[samples];
        for (var i = 0; i < samples; i++)
            for (var c = 0; c < wav.Channels; c++)
                buffer[c][i] = BinaryPrimitives.ReadInt16LittleEndian(
                    wav.Pcm.AsSpan((i * wav.Channels + c) * 2, 2)) / 32768f;

        var processing = ProcessingState.Create(info);
        for (var read = 0; read < samples; read += WriteBufferSize)
        {
            processing.WriteData(buffer, Math.Min(WriteBufferSize, samples - read), read);
            while (!oggStream.Finished && processing.PacketOut(out var packet))
            {
                oggStream.PacketIn(packet);
                Flush(false);
            }
        }

        processing.WriteEndOfStream();
        while (!oggStream.Finished && processing.PacketOut(out var packet))
        {
            oggStream.PacketIn(packet);
            Flush(false);
        }
        Flush(true);
        return outMs.ToArray();
    }
    private const int SeekBlockSamples = 16384;

    /// <summary>
    /// (samples, bytes) per ~16 k-sample block, as uint16 pairs. A Vorbis packet
    /// outputs (prevBlocksize + thisBlocksize) / 4 samples, so a block ends on
    /// whichever packet first crosses the threshold -- which is why the shipped
    /// tables read 16384 plus a small overshoot rather than exactly 16384. The
    /// trailing partial block is omitted, as it is in every shipped file.
    /// </summary>
    private static byte[] BuildSeekTable(List<byte[]> audio, SetupPacket setup, byte bs0, byte bs1)
    {
        var flags = setup.ModeBlockFlags;
        var modeBits = setup.ModeNumberBits;
        var entries = new List<(int Samples, int Bytes)>();
        int blockSamples = 0, blockBytes = 0, prev = 0;

        foreach (var pkt in audio)
        {
            if (pkt.Length == 0) continue;
            var r = new Vorbis.BitReader(pkt);
            r.Read(1);                                        // packet type
            var mode = r.ReadInt(modeBits);
            var size = 1 << (flags[mode] ? bs1 : bs0);
            if (prev != 0) blockSamples += (prev + size) / 4;
            prev = size;

            blockBytes += 2 + pkt.Length;                     // stored with its length prefix
            if (blockSamples >= SeekBlockSamples)
            {
                entries.Add((blockSamples, blockBytes));
                blockSamples = 0; blockBytes = 0;
            }
        }

        var outBytes = new byte[entries.Count * 4];
        for (var i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(outBytes.AsSpan(i * 4, 2),
                (ushort)Math.Min(entries[i].Samples, ushort.MaxValue));
            BinaryPrimitives.WriteUInt16LittleEndian(outBytes.AsSpan(i * 4 + 2, 2),
                (ushort)Math.Min(entries[i].Bytes, ushort.MaxValue));
        }
        return outBytes;
    }


    /// <summary>
    /// Standard audio packet -> Wwise's stored form. Wwise drops the leading
    /// packet-type bit (always 0 for audio) and, on long-block modes, the two window
    /// flags, reconstructing all three on read from the neighbouring packets' modes.
    /// </summary>
    private static byte[] ModPacket(byte[] packet, SetupPacket setup)
    {
        if (packet.Length == 0) return packet;
        var r = new Vorbis.BitReader(packet);
        if (r.Read(1) != 0)
            throw new InvalidDataException("audio packet did not start with a zero type bit");

        var modeBits = setup.ModeNumberBits;
        var mode = r.ReadInt(modeBits);
        var blockFlag = setup.ModeBlockFlags[mode];
        // A/B toggle: does this decode path actually want the window flags dropped?
        var keepFlags = Environment.GetEnvironmentVariable("MRAK_KEEP_WINFLAGS") == "1";
        if (blockFlag && !keepFlags) { r.Read(1); r.Read(1); }   // prev / next window flags

        var w = new Vorbis.BitWriter();
        w.Write(mode, modeBits);
        var total = packet.Length * 8;
        for (var i = r.Position; i < total; i++)
            w.Write((uint)((packet[i >> 3] >> (i & 7)) & 1), 1);
        return w.ToArray();
    }

    /// <summary>Ogg pages back into whole Vorbis packets (a packet may span pages).</summary>
    private static List<byte[]> Demux(byte[] ogg)
    {
        var packets = new List<byte[]>();
        var current = new MemoryStream();
        var p = 0;
        while (p + 27 <= ogg.Length)
        {
            if (Encoding.ASCII.GetString(ogg, p, 4) != "OggS") break;
            int segCount = ogg[p + 26];
            var segTable = p + 27;
            var body = segTable + segCount;
            var at = body;
            for (var i = 0; i < segCount; i++)
            {
                int len = ogg[segTable + i];
                current.Write(ogg, at, len);
                at += len;
                if (len < 255)                 // a segment under 255 ends the packet
                {
                    packets.Add(current.ToArray());
                    current.SetLength(0);
                }
            }
            p = at;
        }
        if (current.Length > 0) packets.Add(current.ToArray());
        return packets;
    }

    /// <summary>blocksize_0 / blocksize_1 exponents, read out of the identification header.</summary>
    private static (byte, byte) BlockSizes(byte[] idHeader)
    {
        // 1 type + "vorbis" + version(4) + channels(1) + rate(4) + 3 bitrates(12) = 28
        var b = idHeader[28];
        return ((byte)(b & 0x0F), (byte)(b >> 4));
    }
}
