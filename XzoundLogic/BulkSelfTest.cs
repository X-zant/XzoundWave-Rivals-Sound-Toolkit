using System.IO;

namespace MRAudioKit;

/// <summary>
/// Checks the two bulk operations without a window: one file standing in for many
/// sounds, and writing out only what an opened bank changed.
/// </summary>
public static class BulkSelfTest
{
    /// <summary>XzoundWave.exe --bulktest &lt;skinId&gt; &lt;sourceAudio&gt; &lt;moddedBnk&gt; &lt;outFolder&gt;</summary>
    public static int Run(string skinId, string source, string moddedBank, string outRoot)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        var failures = 0;

        // ---- one file, many sounds -----------------------------------------
        var charId = skinId[..4];
        var skin = session.Characters.First(c => c.CharId == charId).Skins.First(s => s.SkinId == skinId);
        var sounds = SoundIndex.Build(session, skin, charId, _ => { });
        var targets = sounds.Rows.Select(r => r.MediaId).Distinct().Take(12).ToList();

        var t0 = DateTime.UtcNow;
        var bytes = PendingWem.ToWemBytes(source, out var why);
        if (bytes is null) { W("ENCODE FAILED: " + why); return 1; }
        var encodeMs = (DateTime.UtcNow - t0).TotalMilliseconds;

        var staged = targets.Select(id => PendingWem.ForMedia(id, source, bytes, "bulk")).ToList();
        W($"one file -> many: {staged.Count} staged from one {encodeMs:N0} ms encode");

        // The point of encoding once is that every target gets the SAME bytes.
        var shared = staged.All(p => ReferenceEquals(p.Payload, bytes));
        var sized = staged.All(p => p.Bytes == bytes.Length);
        var ids = staged.Select(p => p.MediaId).Distinct().Count();
        W($"  same bytes reused: {shared}, sizes agree: {sized}, distinct ids: {ids}/{staged.Count}");
        if (!shared || !sized || ids != staged.Count) { W("  -> FAIL"); failures++; }

        // And they must survive into a bank, which is the whole point.
        var bankPath = session.BanksForMedia(targets[0]).FirstOrDefault();
        if (bankPath is not null)
        {
            var orig = session.Provider.Files[bankPath].Read();
            var payload = staged.ToDictionary(p => p.MediaId, p => p.Payload);
            var built = BnkBuilder.Build(orig, payload);
            W($"  into {Path.GetFileName(bankPath)}: {built.Replaced} of {payload.Count} replaced");
            if (built.Replaced == 0) { W("  -> FAIL"); failures++; }
        }

        // ---- extract only what a bank changed ------------------------------
        W("");
        ModBank.Opened opened;
        try { opened = ModBank.Open(moddedBank, settings); }
        catch (Exception ex) { W("OPEN FAILED: " + ex.Message); return 1; }

        foreach (var bank in opened.Banks)
        {
            var rows = ModBank.ToSounds(session, bank);
            var changed = rows.Rows.Where(r => r.ModTag is "MODDED" or "NEW")
                               .GroupBy(r => r.MediaId).Select(g => g.First()).ToList();
            var dir = Path.Combine(outRoot, Path.GetFileNameWithoutExtension(bank.Name));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            var written = 0;
            foreach (var row in changed)
            {
                if (!rows.Raw.TryGetValue(row.MediaId, out var b)) continue;
                var note = Clean(row.Display);
                var file = string.IsNullOrEmpty(note) ? $"{row.MediaId}.wem" : $"{row.MediaId}-{note}.wem";
                File.WriteAllBytes(Path.Combine(dir, file), b);
                written++;
            }

            var onDisk = Directory.GetFiles(dir, "*.wem").Length;
            W($"extract: {bank.Name} -> {written} written, {onDisk} on disk, " +
              $"{rows.Rows.Count - changed.Count} untouched sounds skipped");
            if (written != changed.Count || onDisk != written) { W("  -> FAIL"); failures++; }

            // Every extracted file must re-import, or the naming is wrong.
            var reimported = Directory.GetFiles(dir, "*.wem")
                .Select(f => PendingWem.FromFile(f, out _))
                .Count(x => x is not null);
            W($"  re-import as staged files: {reimported}/{onDisk}");
            if (reimported != onDisk) { W("  -> FAIL"); failures++; }
        }

        W("");
        W(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static string Clean(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 60 ? s[..60] : s;
    }
}
