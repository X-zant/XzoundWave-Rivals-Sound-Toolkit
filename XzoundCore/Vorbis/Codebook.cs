using System.IO;
using System.Buffers.Binary;

namespace Xzound.Vorbis;

/// <summary>
/// A Vorbis codebook, read either from a standard setup packet or from Wwise's
/// packed table. Normalised so the two can be compared for equality — which is the
/// whole point: if a book we produced is already in Wwise's table, we can reference
/// it by index instead of writing it out.
/// </summary>
public sealed class Codebook
{
    public int Dimensions;
    public int Entries;
    public int[] Lengths = [];        // 0 means "entry not present" (sparse)
    public int LookupType;
    public uint LookupMin, LookupMax;
    public int LookupValueBits;
    public bool LookupSequence;
    public int[] Multiplicands = [];

    public bool SameAs(Codebook o) =>
        Dimensions == o.Dimensions && Entries == o.Entries &&
        LookupType == o.LookupType && LookupMin == o.LookupMin && LookupMax == o.LookupMax &&
        LookupValueBits == o.LookupValueBits && LookupSequence == o.LookupSequence &&
        Lengths.AsSpan().SequenceEqual(o.Lengths) &&
        Multiplicands.AsSpan().SequenceEqual(o.Multiplicands);

    /// <summary>How many lookup values a type-1 book carries.</summary>
    private static int QuantVals(int entries, int dimensions)
    {
        var v = 1;
        while (Pow(v + 1, dimensions) <= entries) v++;
        return v;
        static long Pow(int b, int e) { long r = 1; for (var i = 0; i < e; i++) { r *= b; if (r > int.MaxValue) return r; } return r; }
    }

    /// <summary>Read from a standard Vorbis setup packet.</summary>
    public static Codebook ReadStandard(BitReader r)
    {
        if (r.Read(24) != 0x564342) throw new InvalidDataException("codebook sync missing");
        var cb = new Codebook { Dimensions = r.ReadInt(16), Entries = r.ReadInt(24) };
        cb.Lengths = new int[cb.Entries];

        if (!r.Flag())                       // not ordered
        {
            var sparse = r.Flag();
            for (var i = 0; i < cb.Entries; i++)
                cb.Lengths[i] = sparse && !r.Flag() ? 0 : r.ReadInt(5) + 1;
        }
        else
        {
            var current = r.ReadInt(5) + 1;
            var done = 0;
            while (done < cb.Entries)
            {
                var n = r.ReadInt(BitMath.ILog(cb.Entries - done));
                for (var i = 0; i < n; i++) cb.Lengths[done + i] = current;
                done += n; current++;
            }
        }

        cb.LookupType = r.ReadInt(4);
        if (cb.LookupType is 1 or 2)
        {
            cb.LookupMin = r.Read(32);
            cb.LookupMax = r.Read(32);
            cb.LookupValueBits = r.ReadInt(4) + 1;
            cb.LookupSequence = r.Flag();
            var count = cb.LookupType == 1 ? QuantVals(cb.Entries, cb.Dimensions) : cb.Entries * cb.Dimensions;
            cb.Multiplicands = new int[count];
            for (var i = 0; i < count; i++) cb.Multiplicands[i] = r.ReadInt(cb.LookupValueBits);
        }
        return cb;
    }

    /// <summary>Read from Wwise's packed table (narrower fields, no sync word).</summary>
    public static Codebook ReadPacked(BitReader r)
    {
        var cb = new Codebook { Dimensions = r.ReadInt(4), Entries = r.ReadInt(14) };
        cb.Lengths = new int[cb.Entries];

        if (!r.Flag())
        {
            var lengthBits = r.ReadInt(3);
            var sparse = r.Flag();
            for (var i = 0; i < cb.Entries; i++)
                cb.Lengths[i] = sparse && !r.Flag() ? 0 : r.ReadInt(lengthBits) + 1;
        }
        else
        {
            var current = r.ReadInt(5) + 1;
            var done = 0;
            while (done < cb.Entries)
            {
                var n = r.ReadInt(BitMath.ILog(cb.Entries - done));
                for (var i = 0; i < n; i++) cb.Lengths[done + i] = current;
                done += n; current++;
            }
        }

        cb.LookupType = r.ReadInt(1);        // 1 bit here, 4 in standard
        if (cb.LookupType == 1)
        {
            cb.LookupMin = r.Read(32);
            cb.LookupMax = r.Read(32);
            cb.LookupValueBits = r.ReadInt(4) + 1;
            cb.LookupSequence = r.Flag();
            var count = QuantVals(cb.Entries, cb.Dimensions);
            cb.Multiplicands = new int[count];
            for (var i = 0; i < count; i++) cb.Multiplicands[i] = r.ReadInt(cb.LookupValueBits);
        }
        return cb;
    }
}

/// <summary>
/// Wwise's shared codebook table — the aoTuV 6.03 books its runtime carries, which
/// is why a shipped setup packet is 200 bytes instead of 3 KB: it names books by
/// index rather than spelling them out.
///
/// The table is aoTuV's data, an open-source libvorbis fork, not Audiokinetic's.
/// </summary>
public sealed class CodebookLibrary
{
    private readonly Codebook[] _books;
    public int Count => _books.Length;

    public CodebookLibrary(byte[] packed)
    {
        var offsetTable = BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(packed.Length - 4, 4));
        var count = (int)((packed.Length - 4 - offsetTable) / 4);
        var offsets = new uint[count];
        for (var i = 0; i < count; i++)
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan((int)offsetTable + i * 4, 4));

        _books = new Codebook[count];
        for (var i = 0; i < count; i++)
        {
            var end = i + 1 < count ? offsets[i + 1] : offsetTable;
            var slice = packed[(int)offsets[i]..(int)end];
            try { _books[i] = Codebook.ReadPacked(new BitReader(slice)); }
            catch { _books[i] = null; }      // a book we cannot parse simply never matches
        }
    }

    /// <summary>Index of a book identical to <paramref name="book"/>, or -1.</summary>
    public int IndexOf(Codebook book)
    {
        for (var i = 0; i < _books.Length; i++)
            if (_books[i] is not null && _books[i].SameAs(book)) return i;
        return -1;
    }
}
