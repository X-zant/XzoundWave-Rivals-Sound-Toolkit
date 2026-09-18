using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MRAudioKit;

/// <summary>
/// Reads the Vorbis header fields off every shipped .wem we can reach and prints
/// them next to the quantities we already know how to compute. The point is to
/// stop copying the unknown fields from a reference file and derive them instead:
/// a field that always equals something we can calculate is a solved field.
/// </summary>
public static class ExtStats
{
    private sealed class Row
    {
        public string Name;
        public int Rate, Channels, DataLen;
        public uint Samples, SetupSpan, DataMinusSeek, SetupOff, AudioOff;
        public ushort F14, F1e, F20;
        public uint F22, F26;
        public byte Bs0, Bs1;
        public int SetupPayload;      // the uint16 sitting at SetupOff
        public int MaxPacket, PacketCount;
    }

    /// <summary>MRAudioKit.exe --extstats [outCsv] — survey shipped Vorbis headers.</summary>
    public static int Run(string outCsv)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        const string root = "Marvel/Content/WwiseAudio/Media/";
        var files = session.Provider.Files.Keys
            .Where(k => k.EndsWith(".wem", StringComparison.OrdinalIgnoreCase) &&
                        k.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        W($"loose media files: {files.Count:N0}");

        var rows = new List<Row>();
        var skipped = 0;
        foreach (var key in files)
        {
            if (rows.Count >= 4000) break;
            byte[] b;
            try { b = session.Provider.Files[key].Read(); } catch { skipped++; continue; }
            var r = Parse(b, Path.GetFileNameWithoutExtension(key));
            if (r is null) { skipped++; continue; }
            rows.Add(r);
        }
        W($"parsed {rows.Count:N0} Vorbis headers ({skipped:N0} not Vorbis / unreadable)");
        if (rows.Count == 0) return 1;

        // The question that matters: is any unknown field equal to something we
        // already compute? Test every candidate against every unknown.
        var cands = new (string Name, Func<Row, long> F)[]
        {
            ("setupSpan(0x0a)",   x => x.SetupSpan),
            ("setupPayload",      x => x.SetupPayload),
            ("setupOff(0x16)",    x => x.SetupOff),
            ("audioOff(0x1a)",    x => x.AudioOff),
            ("audioOff-setupOff", x => x.AudioOff - x.SetupOff),
            ("dataMinusSeek",     x => x.DataMinusSeek),
            ("samples",           x => x.Samples),
            ("dataLen",           x => x.DataLen),
            ("dataLen-audioOff",  x => x.DataLen - x.AudioOff),
            ("rate",              x => x.Rate),
            ("blocksize0",        x => 1L << x.Bs0),
            ("blocksize1",        x => 1L << x.Bs1),
            ("maxPacket",         x => x.MaxPacket),
            ("packetCount",       x => x.PacketCount),
        };
        var unknowns = new (string Name, Func<Row, long> F)[]
        {
            ("0x14", x => x.F14), ("0x1e", x => x.F1e), ("0x20", x => x.F20),
            ("0x22", x => x.F22), ("0x26", x => x.F26),
        };

        W("");
        W("exact matches (unknown == candidate, across every row):");
        foreach (var (un, uf) in unknowns)
        {
            var hits = cands.Where(c => rows.All(r => uf(r) == c.F(r))).Select(c => c.Name).ToList();
            hits.AddRange(unknowns.Where(o => o.Name != un && rows.All(r => uf(r) == o.F(r)))
                                  .Select(o => o.Name));
            W($"  {un}  ->  {(hits.Count == 0 ? "(nothing)" : string.Join(", ", hits))}");
        }

        W("");
        W("distinct values per (rate, channels):");
        foreach (var g in rows.GroupBy(r => (r.Rate, r.Channels))
                              .OrderBy(g => g.Key.Rate).ThenBy(g => g.Key.Channels))
        {
            string D(Func<Row, long> f)
            {
                var v = g.Select(f).Distinct().ToList();
                return v.Count == 1 ? v[0].ToString() : $"{v.Count} values";
            }
            W($"  {g.Key.Rate,6} Hz {g.Key.Channels}ch  n={g.Count(),-5} " +
              $"0x14={D(x => x.F14),-11} 0x1e={D(x => x.F1e),-11} 0x20={D(x => x.F20),-11} " +
              $"0x22={D(x => x.F22),-11} 0x26={D(x => x.F26),-11}");
        }

        if (!string.IsNullOrEmpty(outCsv))
        {
            var sb = new StringBuilder();
            sb.AppendLine("name,rate,ch,dataLen,samples,setupSpan,setupPayload,dataMinusSeek," +
                          "setupOff,audioOff,bs0,bs1,f14,f1e,f20,f22,f26,maxPacket,packets");
            foreach (var r in rows)
                sb.AppendLine($"{r.Name},{r.Rate},{r.Channels},{r.DataLen},{r.Samples},{r.SetupSpan}," +
                              $"{r.SetupPayload},{r.DataMinusSeek},{r.SetupOff},{r.AudioOff},{r.Bs0},{r.Bs1}," +
                              $"{r.F14},{r.F1e},{r.F20},{r.F22},{r.F26},{r.MaxPacket},{r.PacketCount}");
            File.WriteAllText(outCsv, sb.ToString());
            W("");
            W($"wrote {outCsv}");
        }
        return 0;
    }

    private static Row Parse(byte[] b, string name)
    {
        if (b.Length < 100 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF") return null;
        int rate = 0, ch = 0, dataOff = 0, dataLen = 0;
        byte[] ext = null;
        var p = 12;
        while (p + 8 <= b.Length)
        {
            var tag = Encoding.ASCII.GetString(b, p, 4);
            var sz = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4, 4));
            if (sz < 0 || p + 8 + sz > b.Length) break;
            if (tag == "fmt ")
            {
                // 0xFFFF is Wwise's Vorbis format tag; anything else is PCM or ADPCM.
                if (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 8, 2)) != 0xFFFF) return null;
                if (sz < 18 + 48) return null;
                ch = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 10, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 12, 4));
                ext = b.AsSpan(p + 8 + 18, 48).ToArray();
            }
            if (tag == "data") { dataOff = p + 8; dataLen = sz; }
            p += 8 + sz + (sz & 1);
        }
        if (ext is null || dataLen == 0) return null;

        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(ext.AsSpan(o, 4));
        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(ext.AsSpan(o, 2));
        var setupOff = U32(0x16);
        var payload = setupOff + 2 <= (uint)dataLen
            ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(dataOff + (int)setupOff, 2)) : 0;

        // Walk the audio packets -- each is a uint16 length followed by that many
        // bytes -- so the largest one can be compared against the field at 0x1e.
        int maxPacket = 0, packets = 0;
        for (var q = (int)U32(0x1a); q + 2 <= dataLen;)
        {
            var len = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(dataOff + q, 2));
            if (q + 2 + len > dataLen) break;
            if (len > maxPacket) maxPacket = len;
            packets++;
            q += 2 + len;
        }

        return new Row
        {
            Name = name, Rate = rate, Channels = ch, DataLen = dataLen,
            Samples = U32(0x06), SetupSpan = U32(0x0a), DataMinusSeek = U32(0x0e),
            SetupOff = setupOff, AudioOff = U32(0x1a), SetupPayload = payload,
            F14 = U16(0x14), F1e = U16(0x1e), F20 = U16(0x20), F22 = U32(0x22), F26 = U32(0x26),
            MaxPacket = maxPacket, PacketCount = packets,
            Bs0 = ext[0x2e], Bs1 = ext[0x2f],
        };
    }
}
