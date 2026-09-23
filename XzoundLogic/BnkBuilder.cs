using System.IO;
using System.Buffers.Binary;
using System.Text;

namespace MRAudioKit;

/// <summary>
/// Rewrites a soundbank's embedded media in place of shelling out to BNKTool /
/// BNK-Pack / BNK-Unpack. Doing it in-process also drops soundKit's robocopy-to-a-
/// short-path workaround and its "no spaces anywhere in your path" rule.
///
/// Container layout: a flat sequence of [char[4] tag][uint32 length][body].
/// Only DIDX (the media index) and DATA (the blob) are touched; every other
/// section -- BKHD, HIRC, STID, STMG... -- is copied through byte for byte.
/// </summary>
public static class BnkBuilder
{
    public sealed record Section(string Tag, byte[] Body);
    public sealed record MediaEntry(uint Id, uint Offset, uint Size);

    public static List<Section> ReadSections(byte[] bnk)
    {
        var sections = new List<Section>();
        var p = 0;
        while (p + 8 <= bnk.Length)
        {
            var tag = Encoding.ASCII.GetString(bnk, p, 4);
            var len = BinaryPrimitives.ReadUInt32LittleEndian(bnk.AsSpan(p + 4, 4));
            p += 8;
            if (len > (uint)(bnk.Length - p)) break;   // truncated / not a bank
            var body = new byte[len];
            Buffer.BlockCopy(bnk, p, body, 0, (int)len);
            sections.Add(new Section(tag, body));
            p += (int)len;
        }
        return sections;
    }

    public static List<MediaEntry> ReadDidx(byte[] didx)
    {
        var list = new List<MediaEntry>(didx.Length / 12);
        for (var i = 0; i + 12 <= didx.Length; i += 12)
            list.Add(new MediaEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(didx.AsSpan(i, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(didx.AsSpan(i + 4, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(didx.AsSpan(i + 8, 4))));
        return list;
    }

    /// <summary>
    /// The padding Wwise used between blobs, recovered from the bank rather than
    /// assumed. Guessing 16 when the bank used something else moves every offset
    /// after the first replacement.
    /// </summary>
    public static int InferAlignment(List<MediaEntry> entries)
    {
        if (entries.Count < 2) return 16;
        var best = 1;
        foreach (var a in new[] { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 })
        {
            long cur = 0; var ok = true;
            foreach (var e in entries)
            {
                if (Align(cur, a) != e.Offset) { ok = false; break; }
                cur = e.Offset + (long)e.Size;
            }
            if (ok) best = a;      // keep the strictest alignment that still reproduces the bank
        }
        return best;
    }

    private static long Align(long v, int a) => a <= 1 ? v : (v + a - 1) / a * a;

    public sealed record BuildResult(byte[] Bytes, int Replaced, List<string> Notes);

    /// <summary>
    /// Rebuild <paramref name="original"/> with the given media ids swapped for new
    /// bytes. Ids the bank does not embed are reported, not silently dropped --
    /// they are usually streamed media that belongs beside the bank, not inside it.
    /// </summary>
    public static BuildResult Build(byte[] original, IReadOnlyDictionary<uint, byte[]> replacements)
    {
        var notes = new List<string>();
        var sections = ReadSections(original);

        var didxIdx = sections.FindIndex(s => s.Tag == "DIDX");
        var dataIdx = sections.FindIndex(s => s.Tag == "DATA");
        if (didxIdx < 0 || dataIdx < 0)
        {
            notes.Add("This bank embeds no media (no DIDX/DATA) — nothing to replace inside it.");
            return new BuildResult(original, 0, notes);
        }

        var entries = ReadDidx(sections[didxIdx].Body);
        var data = sections[dataIdx].Body;
        var align = InferAlignment(entries);
        notes.Add($"{entries.Count} embedded media, alignment {align} B.");

        var present = entries.Select(e => e.Id).ToHashSet();
        foreach (var id in replacements.Keys)
            if (!present.Contains(id))
                notes.Add($"media {id} is not embedded in this bank — it is streamed; " +
                          $"ship it as a loose Media/ wem instead.");

        var newDidx = new byte[entries.Count * 12];
        using var blob = new MemoryStream();
        var replaced = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            byte[] payload;
            if (replacements.TryGetValue(e.Id, out var custom) && custom is { Length: > 0 })
            {
                payload = custom;
                replaced++;
            }
            else
            {
                if (e.Offset + (long)e.Size > data.Length)
                {
                    notes.Add($"media {e.Id}: DIDX points past DATA — bank left unchanged.");
                    return new BuildResult(original, 0, notes);
                }
                payload = new byte[e.Size];
                Buffer.BlockCopy(data, (int)e.Offset, payload, 0, (int)e.Size);
            }

            var offset = Align(blob.Length, align);
            while (blob.Length < offset) blob.WriteByte(0);
            BinaryPrimitives.WriteUInt32LittleEndian(newDidx.AsSpan(i * 12, 4), e.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(newDidx.AsSpan(i * 12 + 4, 4), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(newDidx.AsSpan(i * 12 + 8, 4), (uint)payload.Length);
            blob.Write(payload);
        }

        // Padding goes BETWEEN blobs only. The shipped banks end the DATA body exactly
        // at the last blob -- all four of The Hood's have last.Offset + last.Size ==
        // body length -- so a trailing pad here makes the rebuild differ from the
        // original by the pad width and nothing else.
        var newData = blob.ToArray();

        sections[didxIdx] = new Section("DIDX", newDidx);
        sections[dataIdx] = new Section("DATA", newData);

        using var outMs = new MemoryStream();
        foreach (var s in sections)
        {
            outMs.Write(Encoding.ASCII.GetBytes(s.Tag));
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)s.Body.Length);
            outMs.Write(len);
            outMs.Write(s.Body);
        }
        return new BuildResult(outMs.ToArray(), replaced, notes);
    }

    // ---- wem sanity, so a wrong-codec file is refused with a reason ---------

    public sealed record WemInfo(bool IsRiff, ushort FormatTag, string Codec, uint SampleRate, ushort Channels);

    /// <summary>
    /// The banks declare VORBIS for every audio source in this game, and a PCM wem
    /// dropped in its place plays as noise or silence. Reading the replacement's own
    /// header turns that from a pinned Discord guide into a check.
    /// </summary>
    public static WemInfo Inspect(byte[] wem)
    {
        if (wem.Length < 12 || Encoding.ASCII.GetString(wem, 0, 4) != "RIFF")
            return new WemInfo(false, 0, "not a RIFF/wem file", 0, 0);

        var p = 12;
        while (p + 8 <= wem.Length)
        {
            var id = Encoding.ASCII.GetString(wem, p, 4);
            var sz = BinaryPrimitives.ReadUInt32LittleEndian(wem.AsSpan(p + 4, 4));
            var body = p + 8;
            if (id == "fmt " && sz >= 16 && body + 16 <= wem.Length)
            {
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(wem.AsSpan(body, 2));
                var ch = BinaryPrimitives.ReadUInt16LittleEndian(wem.AsSpan(body + 2, 2));
                var rate = BinaryPrimitives.ReadUInt32LittleEndian(wem.AsSpan(body + 4, 4));
                return new WemInfo(true, tag, CodecName(tag), rate, ch);
            }
            p = body + (int)sz + ((sz & 1) == 1 ? 1 : 0);
        }
        return new WemInfo(true, 0, "no fmt chunk", 0, 0);
    }

    private static string CodecName(ushort tag) => tag switch
    {
        0xFFFF => "VORBIS",
        0xFFFE => "PCM (extensible)",
        0x0001 => "PCM",
        0x0002 => "ADPCM",
        0x0166 => "XMA2",
        0xFFF0 => "OPUS/WEM",
        _ => $"0x{tag:X4}",
    };
}
