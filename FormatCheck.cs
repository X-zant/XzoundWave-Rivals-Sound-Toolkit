using System.IO;

namespace MRAudioKit;

/// <summary>
/// Compares replacement audio against the sound it is going to replace.
///
/// A bank can be structurally perfect and still not behave in game if what is inside
/// it does not match what the engine was told to expect. Channel count and sample rate
/// are the two that a person cannot see and a decoder will not complain about: foobar
/// happily plays a mono 48 kHz clip sitting where the game ships stereo 44.1 kHz.
/// </summary>
public static class FormatCheck
{
    private sealed record Fmt(int Channels, int SampleRate, string Codec)
    {
        public override string ToString() => $"{Channels}ch {SampleRate}Hz {Codec}";
    }

    /// <summary>XzoundWave.exe --fmtcheck &lt;wemDir&gt;</summary>
    public static int Run(string dir)
    {
        void W(string s) => Console.WriteLine(s);
        if (!Directory.Exists(dir)) { W("no such folder: " + dir); return 1; }

        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        var files = Directory.GetFiles(dir, "*.wem").OrderBy(f => f).ToList();
        W($"{files.Count} file(s) in {Path.GetFileName(dir)}");
        W("");

        int checkedCount = 0, mismatch = 0, missing = 0;
        var byShape = new Dictionary<string, int>();

        foreach (var f in files)
        {
            var leaf = Path.GetFileNameWithoutExtension(f);
            var digits = new string(leaf.TakeWhile(char.IsDigit).ToArray());
            if (!uint.TryParse(digits, out var id)) continue;

            var mine = Read(File.ReadAllBytes(f));
            if (mine is null) continue;

            // The shipped loose file is a complete sound; a bank entry may be only a
            // prefetch stub, so the loose copy is the honest thing to compare against.
            var shippedFile = session.LooseMedia(id);
            if (shippedFile is null) { missing++; continue; }

            Fmt theirs;
            try { theirs = Read(shippedFile.Read()); } catch { continue; }
            if (theirs is null) continue;

            checkedCount++;
            var same = mine.Channels == theirs.Channels && mine.SampleRate == theirs.SampleRate;
            var key = $"{(same ? "ok  " : "DIFF")}  yours {mine.Channels}ch {mine.SampleRate}Hz  " +
                      $"vs game {theirs.Channels}ch {theirs.SampleRate}Hz";
            byShape[key] = byShape.GetValueOrDefault(key) + 1;
            if (!same && mismatch < 5)
                W($"  {id}: yours {mine}   game {theirs}");
            if (!same) mismatch++;
        }

        W("");
        foreach (var (k, n) in byShape.OrderByDescending(k => k.Value)) W($"  {n,5}  {k}");
        W("");
        W($"{checkedCount} compared, {mismatch} differ, {missing} not shipped loose");
        return mismatch > 0 ? 1 : 0;
    }

    /// <summary>Channels, rate and codec out of a wem's fmt chunk.</summary>
    private static Fmt Read(byte[] wem)
    {
        try
        {
            var info = BnkBuilder.Inspect(wem);
            var i = 12;
            while (i + 8 <= wem.Length)
            {
                var tag = System.Text.Encoding.ASCII.GetString(wem, i, 4);
                var size = BitConverter.ToInt32(wem, i + 4);
                if (tag == "fmt ")
                    return new Fmt(BitConverter.ToUInt16(wem, i + 10),
                                   BitConverter.ToInt32(wem, i + 12), info.Codec);
                i += 8 + size + (size & 1);
            }
        }
        catch { }
        return null;
    }
}
