using System.IO;

namespace MRAudioKit;

/// <summary>
/// Take a finished mod apart and put it back together.
///
/// Extract every wem a mod changed, then rebuild each bank from those extracted files
/// exactly as a person would, and compare the result against the mod we started from.
/// Anything that differs is something the tool loses or alters on the way through, and
/// the comparison is byte-for-byte because "sounds about right" is how a broken bank
/// gets shipped.
/// </summary>
public static class RoundTripTest
{
    /// <summary>XzoundWave.exe --roundtrip &lt;pak|bnk&gt; &lt;outDir&gt;</summary>
    public static int Run(string modPath, string outDir)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        ModBank.Opened opened;
        try { opened = ModBank.Open(modPath, settings); }
        catch (Exception ex) { W("OPEN FAILED: " + ex.Message); return 1; }

        var wemRoot = Path.Combine(outDir, "extracted-wems");
        var bnkRoot = Path.Combine(outDir, "rebuilt-bnks");
        Directory.CreateDirectory(wemRoot);
        Directory.CreateDirectory(bnkRoot);

        int totalWems = 0, failures = 0;
        W($"{opened.Banks.Count} bank(s) in {Path.GetFileName(modPath)}");
        W("");

        foreach (var bank in opened.Banks)
        {
            var stem = Path.GetFileNameWithoutExtension(bank.Name);
            var cmp = ModBank.Compare(session, bank);
            var changed = cmp.Rows.Rows
                .Where(r => r.ModTag is "MODDED" or "NEW")
                .GroupBy(r => r.MediaId).Select(g => g.First()).ToList();

            // ---- extract ---------------------------------------------------
            var dir = Path.Combine(wemRoot, stem);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            foreach (var row in changed)
                if (cmp.Rows.Raw.TryGetValue(row.MediaId, out var bytes))
                    File.WriteAllBytes(Path.Combine(dir, $"{row.MediaId}.wem"), bytes);
            var written = Directory.GetFiles(dir, "*.wem").Length;
            totalWems += written;

            if (cmp.ShippedPath is null)
            {
                W($"{stem,-22} {changed.Count,4} changed, {written,4} extracted   " +
                  "NO SHIPPED BANK — cannot rebuild");
                failures++;
                continue;
            }

            // ---- put them back ---------------------------------------------
            var payload = new Dictionary<uint, byte[]>();
            foreach (var f in Directory.GetFiles(dir, "*.wem"))
                if (uint.TryParse(Path.GetFileNameWithoutExtension(f), out var id))
                    payload[id] = File.ReadAllBytes(f);

            var original = session.Provider.Files[cmp.ShippedPath].Read();
            var rebuilt = BnkBuilder.Build(original, payload);
            var outBnk = Path.Combine(bnkRoot, bank.Name);
            File.WriteAllBytes(outBnk, rebuilt.Bytes);
            // Keep the mod's own copy beside it, so the two can be diffed by hand.
            var asShipped = Path.Combine(outDir, "mod-bnks");
            Directory.CreateDirectory(asShipped);
            File.WriteAllBytes(Path.Combine(asShipped, bank.Name), bank.Bytes);

            // ---- and check they came back the same -------------------------
            // Compare the media, not the whole file: a rebuild may lay entries out
            // differently while still holding byte-identical audio, and it is the
            // audio the game plays.
            var mine = MediaOf(rebuilt.Bytes);
            var theirs = MediaOf(bank.Bytes);
            var missing = theirs.Keys.Where(k => !mine.ContainsKey(k)).Count();
            var extra = mine.Keys.Where(k => !theirs.ContainsKey(k)).Count();
            var differing = theirs.Count(kv => mine.TryGetValue(kv.Key, out var b)
                                               && !b.AsSpan().SequenceEqual(kv.Value));
            var identical = rebuilt.Bytes.AsSpan().SequenceEqual(bank.Bytes);

            var ok = missing == 0 && extra == 0 && differing == 0;
            if (!ok) failures++;
            W($"{stem,-22} {changed.Count,4} changed, {written,4} extracted, " +
              $"{rebuilt.Replaced,4} put back   " +
              (ok ? (identical ? "IDENTICAL bank" : "same media, bank bytes differ")
                  : $"MISMATCH: {missing} missing, {extra} extra, {differing} differing"));
        }

        W("");
        W($"{totalWems:N0} wem(s) extracted to {wemRoot}");
        W($"banks rebuilt into {bnkRoot}");
        W("");
        W(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} BANK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static Dictionary<uint, byte[]> MediaOf(byte[] bnk)
    {
        var result = new Dictionary<uint, byte[]>();
        var sections = BnkBuilder.ReadSections(bnk);
        var didx = sections.FirstOrDefault(s => s.Tag == "DIDX");
        var data = sections.FirstOrDefault(s => s.Tag == "DATA");
        if (didx is null || data is null) return result;
        foreach (var e in BnkBuilder.ReadDidx(didx.Body))
            result[e.Id] = data.Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();
        return result;
    }
}
