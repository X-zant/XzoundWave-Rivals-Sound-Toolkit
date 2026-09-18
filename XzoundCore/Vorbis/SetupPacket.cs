using System.IO;
namespace Xzound.Vorbis;

/// <summary>
/// A Vorbis setup packet, parsed from the standard form and re-emitted in Wwise's
/// stripped form.
///
/// The two differ in exactly five places, all of them established from ww2ogg's
/// reader rather than assumed:
///
///   codebooks        inline book  ->  10-bit index into the shared table
///   time domain      6+16 bits    ->  omitted entirely
///   floor type       16 bits      ->  omitted (always 1)
///   residue type     16 bits      ->  2 bits
///   mapping type     16 bits      ->  omitted (always 0)
///   mode window/xf   16+16 bits   ->  omitted (both always 0)
///
/// Everything else keeps identical field widths, so it is parsed and written back
/// unchanged. That accounts for the whole 3 KB -> 200 byte difference.
/// </summary>
public sealed class SetupPacket
{
    private sealed class Floor
    {
        public int[] PartitionClasses = [];
        public int[] ClassDimensions = [], ClassSubclasses = [], ClassMasterbooks = [];
        public int[][] SubclassBooks = [];
        public int Multiplier, RangeBits;
        public int[] Xs = [];
    }

    private sealed class Residue
    {
        public int Type, Begin, End, PartitionSize, Classifications, Classbook;
        public int[] Cascade = [];
        public int[][] Books = [];
    }

    private sealed class Mapping
    {
        public int Submaps;
        public bool SquarePolar;
        public int[] Magnitude = [], Angle = [];
        public int[] Mux = [];
        public int[] SubmapFloor = [], SubmapResidue = [];
    }

    private sealed class Mode
    {
        public bool BlockFlag;
        public int MappingIndex;
    }

    private readonly List<Codebook> _codebooks = [];
    private readonly List<Floor> _floors = [];
    private readonly List<Residue> _residues = [];
    private readonly List<Mapping> _mappings = [];
    private readonly List<Mode> _modes = [];

    public int CodebookCount => _codebooks.Count;

    /// <summary>Per-mode long-block flag, and how many bits a packet spends naming its mode.</summary>
    public bool[] ModeBlockFlags => _modes.Select(m => m.BlockFlag).ToArray();
    public int ModeNumberBits => BitMath.ILog(_modes.Count - 1);

    /// <summary>Parse a standard Vorbis setup packet (the third header packet).</summary>
    public static SetupPacket ParseStandard(byte[] packet, int channels)
    {
        var s = new SetupPacket();
        var r = new BitReader(packet);

        if (r.Read(8) != 5) throw new InvalidDataException("not a setup packet");
        foreach (var c in "vorbis")
            if (r.Read(8) != c) throw new InvalidDataException("setup packet signature missing");

        var books = r.ReadInt(8) + 1;
        for (var i = 0; i < books; i++) s._codebooks.Add(Codebook.ReadStandard(r));

        // Time domain transforms: a vestigial field, always a single zero.
        var times = r.ReadInt(6) + 1;
        for (var i = 0; i < times; i++) r.Read(16);

        var floors = r.ReadInt(6) + 1;
        for (var i = 0; i < floors; i++)
        {
            if (r.Read(16) != 1) throw new NotSupportedException("only floor type 1 is supported");
            s._floors.Add(ReadFloor(r));
        }

        var residues = r.ReadInt(6) + 1;
        for (var i = 0; i < residues; i++)
        {
            var type = r.ReadInt(16);
            s._residues.Add(ReadResidue(r, type));
        }

        var mappings = r.ReadInt(6) + 1;
        for (var i = 0; i < mappings; i++)
        {
            if (r.Read(16) != 0) throw new NotSupportedException("only mapping type 0 is supported");
            s._mappings.Add(ReadMapping(r, channels));
        }

        var modes = r.ReadInt(6) + 1;
        for (var i = 0; i < modes; i++)
        {
            var m = new Mode { BlockFlag = r.Flag() };
            if (r.Read(16) != 0 || r.Read(16) != 0)
                throw new NotSupportedException("non-zero window/transform type");
            m.MappingIndex = r.ReadInt(8);
            s._modes.Add(m);
        }
        return s;
    }

    /// <summary>
    /// Parse a setup packet already in Wwise's stripped form. Exists to verify the
    /// writer: read a shipped packet, write it back, and the bytes must match. A
    /// mismatch names the offending field instead of leaving "it doesn't play".
    /// </summary>
    public static SetupPacket ParseWwise(byte[] packet, int channels)
    {
        var s = new SetupPacket();
        var r = new BitReader(packet);

        var books = r.ReadInt(8) + 1;
        s._codebookIndices = new int[books];
        for (var i = 0; i < books; i++) s._codebookIndices[i] = r.ReadInt(10);

        // no time domain section

        var floors = r.ReadInt(6) + 1;
        for (var i = 0; i < floors; i++) s._floors.Add(ReadFloor(r));      // no floor type

        var residues = r.ReadInt(6) + 1;
        for (var i = 0; i < residues; i++)
        {
            var type = r.ReadInt(2);                                      // 2 bits, not 16
            s._residues.Add(ReadResidue(r, type));
        }

        var mappings = r.ReadInt(6) + 1;
        for (var i = 0; i < mappings; i++) s._mappings.Add(ReadMapping(r, channels));

        var modes = r.ReadInt(6) + 1;
        for (var i = 0; i < modes; i++)
            s._modes.Add(new Mode { BlockFlag = r.Flag(), MappingIndex = r.ReadInt(8) });

        return s;
    }

    private int[] _codebookIndices;

    private static Floor ReadFloor(BitReader r)
    {
        var f = new Floor();
        var partitions = r.ReadInt(5);
        f.PartitionClasses = new int[partitions];
        var maxClass = -1;
        for (var i = 0; i < partitions; i++)
        {
            f.PartitionClasses[i] = r.ReadInt(4);
            maxClass = Math.Max(maxClass, f.PartitionClasses[i]);
        }

        f.ClassDimensions = new int[maxClass + 1];
        f.ClassSubclasses = new int[maxClass + 1];
        f.ClassMasterbooks = new int[maxClass + 1];
        f.SubclassBooks = new int[maxClass + 1][];
        for (var c = 0; c <= maxClass; c++)
        {
            f.ClassDimensions[c] = r.ReadInt(3) + 1;
            f.ClassSubclasses[c] = r.ReadInt(2);
            if (f.ClassSubclasses[c] != 0) f.ClassMasterbooks[c] = r.ReadInt(8);
            var n = 1 << f.ClassSubclasses[c];
            f.SubclassBooks[c] = new int[n];
            for (var j = 0; j < n; j++) f.SubclassBooks[c][j] = r.ReadInt(8) - 1;
        }

        f.Multiplier = r.ReadInt(2) + 1;
        f.RangeBits = r.ReadInt(4);
        var xs = new List<int>();
        for (var i = 0; i < partitions; i++)
            for (var j = 0; j < f.ClassDimensions[f.PartitionClasses[i]]; j++)
                xs.Add(r.ReadInt(f.RangeBits));
        f.Xs = xs.ToArray();
        return f;
    }

    private static Residue ReadResidue(BitReader r, int type)
    {
        var res = new Residue
        {
            Type = type,
            Begin = r.ReadInt(24),
            End = r.ReadInt(24),
            PartitionSize = r.ReadInt(24) + 1,
            Classifications = r.ReadInt(6) + 1,
            Classbook = r.ReadInt(8),
        };
        res.Cascade = new int[res.Classifications];
        for (var i = 0; i < res.Classifications; i++)
        {
            var low = r.ReadInt(3);
            var high = r.Flag() ? r.ReadInt(5) : 0;
            res.Cascade[i] = (high << 3) | low;
        }
        res.Books = new int[res.Classifications][];
        for (var i = 0; i < res.Classifications; i++)
        {
            res.Books[i] = new int[8];
            for (var j = 0; j < 8; j++)
                res.Books[i][j] = (res.Cascade[i] & (1 << j)) != 0 ? r.ReadInt(8) : -1;
        }
        return res;
    }

    private static Mapping ReadMapping(BitReader r, int channels)
    {
        var m = new Mapping { Submaps = r.Flag() ? r.ReadInt(4) + 1 : 1 };
        m.SquarePolar = r.Flag();
        if (m.SquarePolar)
        {
            var steps = r.ReadInt(8) + 1;
            m.Magnitude = new int[steps];
            m.Angle = new int[steps];
            var bits = BitMath.ILog(channels - 1);
            for (var i = 0; i < steps; i++)
            {
                m.Magnitude[i] = r.ReadInt(bits);
                m.Angle[i] = r.ReadInt(bits);
            }
        }
        if (r.Read(2) != 0) throw new InvalidDataException("mapping reserved bits set");

        m.Mux = new int[channels];
        if (m.Submaps > 1)
            for (var i = 0; i < channels; i++) m.Mux[i] = r.ReadInt(4);

        m.SubmapFloor = new int[m.Submaps];
        m.SubmapResidue = new int[m.Submaps];
        for (var i = 0; i < m.Submaps; i++)
        {
            r.Read(8);                      // unused
            m.SubmapFloor[i] = r.ReadInt(8);
            m.SubmapResidue[i] = r.ReadInt(8);
        }
        return m;
    }

    /// <summary>
    /// Emit the Wwise stripped form. Every codebook must be present in
    /// <paramref name="library"/>; if one is not, there is no index to write and the
    /// caller needs to know rather than get a silently broken file.
    /// </summary>
    public byte[] WriteWwise(CodebookLibrary library, int channels)
    {
        var w = new BitWriter();

        // Indices are already known when this came from a Wwise packet; otherwise each
        // book has to be found in the shared table.
        if (_codebookIndices is not null)
        {
            w.Write(_codebookIndices.Length - 1, 8);
            foreach (var i in _codebookIndices) w.Write(i, 10);
        }
        else
        {
            w.Write(_codebooks.Count - 1, 8);
            foreach (var cb in _codebooks)
            {
                var index = library.IndexOf(cb);
                if (index < 0)
                    throw new InvalidOperationException(
                        $"codebook (dim {cb.Dimensions}, {cb.Entries} entries) is not in the shared " +
                        "table, so it cannot be referenced by index");
                w.Write(index, 10);
            }
        }

        // No time domain section in the stripped form.

        w.Write(_floors.Count - 1, 6);
        foreach (var f in _floors)                       // floor type omitted (always 1)
        {
            w.Write(f.PartitionClasses.Length, 5);
            foreach (var c in f.PartitionClasses) w.Write(c, 4);
            for (var c = 0; c < f.ClassDimensions.Length; c++)
            {
                w.Write(f.ClassDimensions[c] - 1, 3);
                w.Write(f.ClassSubclasses[c], 2);
                if (f.ClassSubclasses[c] != 0) w.Write(f.ClassMasterbooks[c], 8);
                foreach (var b in f.SubclassBooks[c]) w.Write(b + 1, 8);
            }
            w.Write(f.Multiplier - 1, 2);
            w.Write(f.RangeBits, 4);
            foreach (var x in f.Xs) w.Write(x, f.RangeBits);
        }

        w.Write(_residues.Count - 1, 6);
        foreach (var res in _residues)
        {
            w.Write(res.Type, 2);                        // 2 bits here, 16 in standard
            w.Write(res.Begin, 24);
            w.Write(res.End, 24);
            w.Write(res.PartitionSize - 1, 24);
            w.Write(res.Classifications - 1, 6);
            w.Write(res.Classbook, 8);
            foreach (var cascade in res.Cascade)
            {
                w.Write(cascade & 7, 3);
                var high = cascade >> 3;
                w.Flag(high != 0);
                if (high != 0) w.Write(high, 5);
            }
            for (var i = 0; i < res.Classifications; i++)
                for (var j = 0; j < 8; j++)
                    if ((res.Cascade[i] & (1 << j)) != 0) w.Write(res.Books[i][j], 8);
        }

        w.Write(_mappings.Count - 1, 6);
        foreach (var m in _mappings)                     // mapping type omitted (always 0)
        {
            w.Flag(m.Submaps > 1);
            if (m.Submaps > 1) w.Write(m.Submaps - 1, 4);
            w.Flag(m.SquarePolar);
            if (m.SquarePolar)
            {
                w.Write(m.Magnitude.Length - 1, 8);
                var bits = BitMath.ILog(channels - 1);
                for (var i = 0; i < m.Magnitude.Length; i++)
                {
                    w.Write(m.Magnitude[i], bits);
                    w.Write(m.Angle[i], bits);
                }
            }
            w.Write(0, 2);                               // reserved
            if (m.Submaps > 1)
                foreach (var mux in m.Mux) w.Write(mux, 4);
            for (var i = 0; i < m.Submaps; i++)
            {
                w.Write(0, 8);                           // unused
                w.Write(m.SubmapFloor[i], 8);
                w.Write(m.SubmapResidue[i], 8);
            }
        }

        w.Write(_modes.Count - 1, 6);
        foreach (var mode in _modes)                     // window/transform type omitted
        {
            w.Flag(mode.BlockFlag);
            w.Write(mode.MappingIndex, 8);
        }

        // No framing bit: the stripped form carries an explicit size, so the
        // packet-level framing flag standard Vorbis ends with is dropped. Writing it
        // was the last byte of difference against every shipped file.
        return w.ToArray();
    }
}
