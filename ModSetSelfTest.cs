using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MRAudioKit;

/// <summary>
/// Several mods loaded at once, and what a build does on top of them.
///
/// Two claims are worth a test rather than a look. One: a .bnk two mods both claim
/// belongs to the newer one, and the older is reported rather than dropped in silence.
/// Two: a build on a loaded mod keeps that mod's work — the thing that made the old
/// behaviour (refuse to build at all) safe, and the thing most likely to break quietly
/// if a builder ever reads the game's file instead of the overlay.
///
/// Everything here is synthetic, so it runs with no game mounted and no pak on disk.
/// </summary>
public static class ModSetSelfTest
{
    /// <summary>XzoundWave.exe --modset</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        const string StemA = "bnk_test_9999001";
        const string StemB = "bnk_test_9999002";

        var vanilla = Bank([(100, Wem(0x11)), (200, Wem(0x22)), (300, Wem(0x33))]);
        var modA = BnkBuilder.Build(vanilla, new Dictionary<uint, byte[]> { [100] = Wem(0xAA) }).Bytes;
        var modC = BnkBuilder.Build(vanilla, new Dictionary<uint, byte[]> { [100] = Wem(0xCC) }).Bytes;
        var other = Bank([(400, Wem(0x44))]);

        W("mod set");
        var mods = new ModSet();
        mods.Add(null, new ModBank.Bank($"{StemA}.bnk", @"C:\mods\first.pak", modA), @"C:\mods\first.pak", out var displaced0);
        Check("first .bnk loads", mods.Count == 1 && mods.Has(StemA));
        Check("nothing was displaced", displaced0 is null);

        mods.Add(null, new ModBank.Bank($"{StemB}.bnk", @"C:\mods\second.pak", other), @"C:\mods\second.pak", out _);
        Check("a second, different .bnk coexists", mods.Count == 2 && mods.Has(StemB));

        mods.Add(null, new ModBank.Bank($"{StemA}.bnk", @"C:\mods\third.pak", modC), @"C:\mods\third.pak", out var displaced1);
        Check("the same .bnk from another mod replaces, not adds", mods.Count == 2,
              $"{mods.Count} loaded");
        Check("the displaced entry is reported", displaced1?.SourceName == "first.pak",
              displaced1?.SourceName ?? "(none)");
        Check("the newest mod owns the .bnk", mods.Get(StemA)?.SourceName == "third.pak",
              mods.Get(StemA)?.SourceName ?? "(none)");
        Check("sources are listed once each", mods.Sources.Count() == 2,
              string.Join(", ", mods.Sources));

        W("");
        W("where a builder reads its bytes from");
        Check("no overlay, no session: nothing to read",
              BankBase.None.Read(null, "Marvel/Content/WwiseAudio/" + StemA + ".bnk") is null);

        var overlay = new BankBase(mods.All.Select(e =>
            new KeyValuePair<string, byte[]>(e.Stem, e.Bank.Bytes)));
        // The mod names a .bnk, not a path. The game's path for it is longer and in a
        // language folder, and the overlay has to match on the stem alone.
        var gamePath = "Marvel/Content/WwiseAudio/English(US)/" + StemA + ".bnk";
        var read = overlay.Read(null, gamePath);
        Check("the overlay answers for a game path", read is not null);
        Check("and it is the newest mod's bytes", read is not null && read.AsSpan().SequenceEqual(modC));
        Check("a .bnk no mod claims is left alone",
              overlay.Read(null, "Marvel/Content/WwiseAudio/bnk_test_9999003.bnk") is null);

        W("");
        W("a build on top of a loaded mod");
        var built = BnkBuilder.Build(read!, new Dictionary<uint, byte[]> { [200] = Wem(0xBB) }).Bytes;
        var after = Media(built);
        Check("the staged sound is in", after.GetValueOrDefault(200u)?[8] == 0xBB);
        Check("the MOD's sound survived the build", after.GetValueOrDefault(100u)?[8] == 0xCC,
              $"0x{after.GetValueOrDefault(100u)?[8]:X2} — 0xAA would be the older mod, 0x11 the game");
        Check("everything neither touched is the game's", after.GetValueOrDefault(300u)?[8] == 0x33);

        // The control. Without it "the mod's sound survived" could be true because the
        // build never replaced anything at all.
        var fromGame = Media(BnkBuilder.Build(vanilla, new Dictionary<uint, byte[]> { [200] = Wem(0xBB) }).Bytes);
        Check("building from the game gives the game's sound instead",
              fromGame.GetValueOrDefault(100u)?[8] == 0x11,
              $"0x{fromGame.GetValueOrDefault(100u)?[8]:X2}");

        W("");
        W("closing");
        mods.Remove(StemA);
        Check("one can be unloaded", mods.Count == 1 && !mods.Has(StemA));
        mods.Clear();
        Check("and all of them", !mods.Any);

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>A wem just real enough to be inspected: RIFF, a fmt chunk, one marker byte.</summary>
    private static byte[] Wem(byte mark)
    {
        var data = new byte[40];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        Encoding.ASCII.GetBytes("WAVE").CopyTo(data, 8);
        // The marker sits on the "WAVE" tag's first byte, which no check here reads,
        // so one byte tells every assertion above which copy it is holding.
        data[8] = mark;
        Encoding.ASCII.GetBytes("fmt ").CopyTo(data, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 0xFFFF);   // VORBIS
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 1);        // mono
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 48000);
        return data;
    }

    /// <summary>A .bnk with a header, a DIDX and a DATA — the shape the builders expect.</summary>
    private static byte[] Bank(IReadOnlyList<(uint Id, byte[] Wem)> media)
    {
        const int Align = 16;
        var didx = new byte[media.Count * 12];
        var data = new MemoryStream();
        for (var i = 0; i < media.Count; i++)
        {
            while (data.Length % Align != 0) data.WriteByte(0);
            var offset = (uint)data.Length;
            data.Write(media[i].Wem, 0, media[i].Wem.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12), media[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12 + 4), offset);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12 + 8), (uint)media[i].Wem.Length);
        }

        var bnk = new MemoryStream();
        void Section(string tag, byte[] body)
        {
            bnk.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
            bnk.Write(size, 0, 4);
            bnk.Write(body, 0, body.Length);
        }
        Section("BKHD", new byte[16]);
        Section("DIDX", didx);
        Section("DATA", data.ToArray());
        Section("HIRC", new byte[4]);   // copied through untouched, which is the point
        return bnk.ToArray();
    }

    /// <summary>Media id to its bytes, read back out of a built bank.</summary>
    private static Dictionary<uint, byte[]> Media(byte[] bnk)
    {
        var map = new Dictionary<uint, byte[]>();
        var sections = BnkBuilder.ReadSections(bnk);
        var didx = sections.FirstOrDefault(s => s.Tag == "DIDX");
        var data = sections.FirstOrDefault(s => s.Tag == "DATA");
        if (didx is null || data is null) return map;
        foreach (var e in BnkBuilder.ReadDidx(didx.Body))
            map[e.Id] = data.Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();
        return map;
    }
}
