using System.IO;
using System.Text;

namespace MRAudioKit;

/// <summary>
/// Runs the exact code paths the window uses, with no window, and writes a report.
///     MRAudioKit.exe --selftest &lt;skinId&gt; [reportPath]
/// Present so the pipeline can be verified without a human clicking through it.
/// </summary>
public static class SelfTest
{
    /// <summary>
    ///     MRAudioKit.exe --bankmax
    /// The largest number of media entries in any single bank — i.e. how far a
    /// numbered clip set actually has to go.
    /// </summary>
    public static int BankMax()
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        var banks = session.Provider.Files
            .Where(kv => kv.Key.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();

        var counts = new List<(string Path, int N)>();
        foreach (var b in banks)
        {
            try
            {
                var didx = BnkBuilder.ReadSections(b.Read()).FirstOrDefault(s => s.Tag == "DIDX");
                counts.Add((b.Path, didx is null ? 0 : BnkBuilder.ReadDidx(didx.Body).Count));
            }
            catch { }
        }
        counts.Sort((a, b) => b.N.CompareTo(a.N));
        W($"{counts.Count} banks scanned");
        W("largest by embedded media:");
        foreach (var c in counts.Take(12)) W($"  {c.N,6}  {Path.GetFileName(c.Path)}");
        W($"over 1000 entries: {counts.Count(c => c.N > 1000)} bank(s)");
        W($"over 500 entries : {counts.Count(c => c.N > 500)} bank(s)");
        return 0;
    }

    /// <summary>
    ///     MRAudioKit.exe --tts &lt;outFolder&gt; &lt;first&gt; &lt;last&gt;
    /// Generates spoken-number wems and checks each one decodes.
    /// </summary>
    public static int TtsTest(string outDir, int first, int last)
    {
        void W(string s) => Console.WriteLine(s);
        W($"SAPI available: {Tts.IsAvailable}");
        if (!Tts.IsAvailable) return 1;

        var t0 = DateTime.UtcNow;
        var made = Tts.GenerateNumbers(outDir, first, last, W);
        W($"{made} clip(s) in {(DateTime.UtcNow - t0).TotalSeconds:0.0}s " +
          $"({(DateTime.UtcNow - t0).TotalMilliseconds / Math.Max(1, made):0} ms each)");

        var settings = Settings.Load();
        settings.AutoDetect();
        var vgm = settings.VgmstreamPath;
        if (string.IsNullOrWhiteSpace(vgm) || !File.Exists(vgm))
        {
            W("vgmstream not configured — cannot verify the output decodes.");
            return 0;
        }

        var checkedCount = 0; var failed = 0;
        foreach (var f in Directory.EnumerateFiles(outDir, "*.wem").OrderBy(x => x).Take(200))
        {
            var psi = new System.Diagnostics.ProcessStartInfo(vgm, $"-m \"{f}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            var o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            checkedCount++;
            if (!o.Contains("play duration")) { failed++; W($"  DECODE FAILED: {Path.GetFileName(f)}"); continue; }
            if (checkedCount <= 4)
            {
                var lines = o.Split('\n').Where(l => l.Contains("encoding") || l.Contains("sample rate")
                                                  || l.Contains("channels:") || l.Contains("play duration"));
                W($"  {Path.GetFileName(f)} ({new FileInfo(f).Length:N0} B)");
                foreach (var l in lines) W("      " + l.Trim());
            }
        }
        W($"decoded {checkedCount - failed}/{checkedCount} generated clips");

        // A generated clip must also survive going into a real bank.
        W("");
        var info = BnkBuilder.Inspect(File.ReadAllBytes(Path.Combine(outDir, $"{first}.wem")));
        W($"BnkBuilder sees it as: isRiff={info.IsRiff} codec={info.Codec} " +
          $"{info.SampleRate} Hz {info.Channels}ch");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    ///     MRAudioKit.exe --testbank &lt;skinId&gt; &lt;numberedWemFolder&gt; &lt;outFolder&gt; [bankFilter]
    /// </summary>
    public static int TestBank(string skinId, string wemDir, string outDir, string bankFilter)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        var charId = skinId[..4];
        var skin = session.Characters.First(c => c.CharId == charId).Skins.First(s => s.SkinId == skinId);
        var sounds = SoundIndex.Build(session, skin, charId, _ => { });

        var scope = skin.Banks.Select(b => b.Path)
            .Where(p => bankFilter is null ||
                        Path.GetFileName(p).Contains(bankFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var wems = TestBankBuilder.ScanFolder(wemDir);
        var lowest = wems.ByNumber.Keys.Min();
        W($"numbered clips: {wems.ByNumber.Count} ({lowest}..{wems.Max}), " +
          $"silent clip: {(wems.SilentPath is null ? "none" : Path.GetFileName(wems.SilentPath))}");
        foreach (var p in scope)
            W($"  {Path.GetFileName(p),-34} {TestBankBuilder.CountEntries(session, [p]),5} entries");
        W($"total entries in scope: {TestBankBuilder.CountEntries(session, scope)}");

        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        var r = TestBankBuilder.Build(session, scope, wems, outDir, lowest, true, sounds, true);
        W("");
        W(r.Log);
        W($"legend rows: {r.Legend.Count}");
        foreach (var row in r.Legend.Take(6))
            W($"  #{row.Number,-4} {row.MediaId,-12} {row.Bank,-28} {row.Events}");
        W("output:");
        foreach (var f in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).OrderBy(x => x))
            W($"  {new FileInfo(f).Length,12:N0} B  {Path.GetRelativePath(outDir, f)}");
        return 0;
    }

    /// <summary>
    ///     MRAudioKit.exe --build &lt;skinId&gt; &lt;wemFolder&gt; &lt;outFolder&gt;
    /// The same ModBuilder the window calls, with no window.
    /// </summary>
    public static int BuildMod(string skinId, string wemDir, string outDir)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });
        session.IndexBanks(_ => { });

        var charId = skinId[..4];
        var skin = session.Characters.First(c => c.CharId == charId).Skins.First(s => s.SkinId == skinId);

        var pending = Directory.EnumerateFiles(wemDir, "*.wem")
            .Select(f => PendingWem.FromFile(f, out _)).Where(x => x is not null).ToList();
        W($"staged {pending.Count} file(s)");
        foreach (var p in pending)
        {
            var banks = session.BanksForMedia(p.MediaId);
            var loose = session.LooseMedia(p.MediaId);
            W($"  {p.MediaId,-12} {p.Bytes,8:N0} B  {p.Codec,-8} banks=[{string.Join(", ", banks.Select(Path.GetFileName))}]" +
              $"  loose={(loose is null ? "none" : loose.Size.ToString("N0") + " B")}   note=\"{p.Note}\"");
        }

        var bankPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pending) foreach (var b in session.BanksForMedia(p.MediaId)) bankPaths.Add(b);
        W($"banks to rebuild: {bankPaths.Count}");

        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        var r = ModBuilder.Build(session, skin, pending, outDir, bankPaths);

        W("");
        W(r.Log);
        W("output tree:");
        foreach (var f in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
                                   .OrderBy(x => x))
            W($"  {new FileInfo(f).Length,10:N0} B  {Path.GetRelativePath(outDir, f)}");
        return 0;
    }

    /// <summary>
    ///     MRAudioKit.exe --media &lt;bankPath&gt; &lt;id&gt; [&lt;id&gt;...]
    /// What the game actually ships for a given media id: is it embedded, how big is
    /// the bank's copy, is there a loose streamed file, and how do they relate.
    /// </summary>
    public static int Media(string[] ids, string bankHint, string reportPath)
    {
        var log = new StringBuilder();
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }

        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });
        session.IndexBanks(_ => { });

        foreach (var raw in ids)
        {
            if (!uint.TryParse(raw, out var id)) { W($"{raw}: not a number"); continue; }
            W("");
            W($"media {id}");
            var banks = session.BanksForMedia(id);
            W($"  embedded in {banks.Count} bank(s): " +
              (banks.Count == 0 ? "(none)" : string.Join(", ", banks.Select(Path.GetFileName))));

            foreach (var bp in banks.Where(b => bankHint is null ||
                     Path.GetFileName(b).Contains(bankHint, StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = session.Provider.Files[bp].Read();
                var didx = BnkBuilder.ReadDidx(
                    BnkBuilder.ReadSections(bytes).First(s => s.Tag == "DIDX").Body);
                var e = didx.First(x => x.Id == id);
                W($"    {Path.GetFileName(bp)}: DIDX size {e.Size:N0} B at offset {e.Offset:N0}");
            }

            // The authoritative signal for HOW the engine loads this: Data means the
            // bank holds the whole clip, PrefetchStreaming means bank-then-file.
            foreach (var bp in banks.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var f = session.Provider.Files[bp];
                using var ar = f.CreateReader();
                var w = new CUE4Parse.UE4.Wwise.WwiseReader(
                    new CUE4Parse.UE4.Wwise.FWwiseArchive(ar),
                    new CUE4Parse.UE4.Wwise.WwiseGameFileSource(f));
                foreach (var h in w.Hierarchies ?? [])
                {
                    if (h.Data is not CUE4Parse.UE4.Wwise.Objects.HIRC.Containers.HierarchySoundSfxVoice s)
                        continue;
                    if (s.Source.SourceId != id && s.Source.FileId != id) continue;
                    W($"    {bp}");
                    W($"      SourceType={s.Source.SourceType}  plugin={s.Source.Plugin.PluginId}  " +
                      $"inMemorySize={s.Source.InMemoryMediaSize:N0}");
                    break;
                }
            }

            var loose = session.LooseMedia(id);
            W($"  loose streamed file: {(loose is null ? "(none)" : $"{loose.Path}  {loose.Size:N0} B")}");

            if (banks.Count > 0 && loose is not null)
            {
                var bankBytes = session.Provider.Files[banks[0]].Read();
                var sec = BnkBuilder.ReadSections(bankBytes);
                var didx = BnkBuilder.ReadDidx(sec.First(s => s.Tag == "DIDX").Body);
                var body = sec.First(s => s.Tag == "DATA").Body;
                var e = didx.First(x => x.Id == id);
                var stub = new byte[e.Size];
                Buffer.BlockCopy(body, (int)e.Offset, stub, 0, (int)e.Size);
                var full = loose.Read();
                W($"  bank copy is a byte-prefix of the streamed file: " +
                  $"{stub.Length <= full.Length && full.AsSpan(0, stub.Length).SequenceEqual(stub)}");
                W($"  ratio: bank {stub.Length:N0} B vs streamed {full.Length:N0} B");
            }
        }

        if (!string.IsNullOrWhiteSpace(reportPath))
            File.WriteAllText(reportPath, log.ToString(), new UTF8Encoding(true));
        return 0;
    }

    public static int Run(string skinId, string reportPath)
    {
        var log = new StringBuilder();
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }

        var settings = Settings.Load();
        settings.AutoDetect();
        if (string.IsNullOrWhiteSpace(settings.AesKey))
            settings.AesKey = Environment.GetEnvironmentVariable("MR_AES") ?? "";

        var session = new GameSession();
        var t0 = DateTime.UtcNow;
        session.Mount(settings, W);
        W($"mount+index: {(DateTime.UtcNow - t0).TotalSeconds:0.0}s");

        var charId = skinId[..4];
        var ch = session.Characters.FirstOrDefault(c => c.CharId == charId);
        if (ch is null) { W($"character {charId} not found"); return 2; }
        var skin = ch.Skins.FirstOrDefault(s => s.SkinId == skinId);
        if (skin is null) { W($"skin {skinId} not found under {ch.Name}"); return 2; }

        W($"character : {ch.Name}");
        W($"skin      : {skin.Label}");
        W($"banks     : {string.Join(", ", skin.Banks.Select(b => Path.GetFileName(b.Path)))}");

        var t1 = DateTime.UtcNow;
        var sounds = SoundIndex.Build(session, skin, charId, _ => { });
        W($"index     : {sounds.Rows.Count} rows in {(DateTime.UtcNow - t1).TotalSeconds:0.0}s");

        var named = sounds.Rows.Count(r => r.EventName is not null);
        var subbed = sounds.Rows.Count(r => r.Subtitle.Length > 0);
        W($"named     : {named}/{sounds.Rows.Count}");
        W($"subtitled : {subbed}/{sounds.Rows.Count}");

        W("");
        W("first 10 rows:");
        foreach (var r in sounds.Rows.Where(r => r.EventName is not null).Take(10))
            W($"  {r.Kind,-7} {r.SheetCell,-58} {r.Category,-10} {r.Subtitle}");

        // Prove the byte-source rule: loose media first, bank stub second.
        var preview = new AudioPreview { VgmstreamPath = settings.VgmstreamPath };
        var probe = sounds.Rows.FirstOrDefault(r => r.Kind == "VO" && r.EventName is not null)
                 ?? sounds.Rows.FirstOrDefault(r => r.EventName is not null);
        if (probe is not null)
        {
            W("");
            W($"decode check: {probe.Display}");
            var data = preview.Bytes(session, sounds, probe);
            if (data is null) W("  no bytes"); else
            {
                W($"  {data.Length:N0} B from {probe.Origin}");
                if (sounds.Embedded.TryGetValue(probe.MediaId, out var stub))
                    W($"  bank-embedded copy would have been {stub.GetData().Length:N0} B");
                var secs = preview.Probe(data, probe.MediaId);
                W($"  duration: {(secs is null ? "(vgmstream not configured)" : secs.Value.ToString("0.000") + "s")}");
                var wav = preview.Decode(data, probe.MediaId, out var err);
                W(wav is null ? $"  decode FAILED: {err}"
                              : $"  decoded -> {wav} ({new FileInfo(wav).Length:N0} B)");
            }
        }

        // ---- bank rewriting -------------------------------------------------
        // Identity round-trip first: rebuilding with zero replacements must return
        // the original file byte for byte, or the section/DIDX/DATA handling is wrong
        // and every later claim about a modded bank is worthless.
        W("");
        W("bank rebuild — identity round-trip:");
        byte[] targetBank = null; string targetBankName = null;
        foreach (var bank in skin.Banks)
        {
            var orig = bank.Read();
            var rt = BnkBuilder.Build(orig, new Dictionary<uint, byte[]>());
            var same = orig.Length == rt.Bytes.Length && orig.AsSpan().SequenceEqual(rt.Bytes);
            W($"  {Path.GetFileName(bank.Path),-34} {orig.Length,10:N0} B  ->  " +
              $"{(same ? "IDENTICAL" : "DIFFERS")}   [{string.Join("; ", rt.Notes)}]");
            if (!same)
            {
                var n = Math.Min(orig.Length, rt.Bytes.Length);
                var at = -1;
                for (var i = 0; i < n; i++) if (orig[i] != rt.Bytes[i]) { at = i; break; }
                W($"      lengths {orig.Length:N0} vs {rt.Bytes.Length:N0}; first byte diff at {(at < 0 ? "(none - length only)" : at.ToString("N0"))}");
                var so = BnkBuilder.ReadSections(orig);
                W($"      sections: {string.Join(", ", so.Select(s => $"{s.Tag}:{s.Body.Length}"))}");
                var consumed = so.Sum(s => (long)s.Body.Length + 8);
                W($"      section bytes {consumed:N0} of {orig.Length:N0} (tail {orig.Length - consumed:N0})");
                var de = BnkBuilder.ReadDidx(so.First(s => s.Tag == "DIDX").Body);
                W($"      first entries: {string.Join(" | ", de.Take(3).Select(x => $"id={x.Id} off={x.Offset} size={x.Size}"))}");
                var last = de[^1];
                W($"      last entry off={last.Offset} size={last.Size} end={last.Offset + last.Size}; DATA body={so.First(s => s.Tag == "DATA").Body.Length}");
            }
            if (same && targetBank is null && rt.Notes.Any(n => n.Contains("embedded media")))
            {
                targetBank = orig;
                targetBankName = Path.GetFileName(bank.Path);
            }
        }

        // Then a real swap, read back through the parser rather than trusted.
        if (targetBank is not null)
        {
            var didx = BnkBuilder.ReadDidx(
                BnkBuilder.ReadSections(targetBank).First(s => s.Tag == "DIDX").Body);
            if (didx.Count >= 2)
            {
                var victim = didx[0];
                var witness = didx[1];
                var custom = new byte[victim.Size + 5000];
                new Random(1234).NextBytes(custom);

                var built = BnkBuilder.Build(targetBank,
                    new Dictionary<uint, byte[]> { [victim.Id] = custom });

                W("");
                W($"bank rebuild — swap check on {targetBankName}:");
                W($"  replaced {built.Replaced} media; {targetBank.Length:N0} B -> {built.Bytes.Length:N0} B");

                var reread = new CUE4Parse.UE4.Wwise.WwiseReader(
                    new CUE4Parse.UE4.Wwise.FWwiseArchive("rebuilt.bnk", built.Bytes),
                    new CUE4Parse.UE4.Wwise.WwiseArchiveSource());

                var got = reread.WwiseEncodedMedias.TryGetValue(victim.Id.ToString(), out var gv)
                        ? gv.GetData() : null;
                W($"  media {victim.Id}: re-parsed {got?.Length ?? -1:N0} B, " +
                  $"matches custom bytes = {(got is not null && got.AsSpan().SequenceEqual(custom))}");

                var origWitness = new byte[witness.Size];
                var dataBody = BnkBuilder.ReadSections(targetBank).First(s => s.Tag == "DATA").Body;
                Buffer.BlockCopy(dataBody, (int)witness.Offset, origWitness, 0, (int)witness.Size);
                var newWitness = reread.WwiseEncodedMedias.TryGetValue(witness.Id.ToString(), out var wv)
                        ? wv.GetData() : null;
                W($"  media {witness.Id} (untouched, sits AFTER the swap): " +
                  $"intact = {(newWitness is not null && newWitness.AsSpan().SequenceEqual(origWitness))}");
                W($"  hierarchies preserved: {reread.Hierarchies?.Length ?? -1} " +
                  $"(was {sounds.Rows.Count} rows across all banks)");
            }
        }

        // ---- a real mod build, decoded back to audio -------------------------
        // Random bytes prove the container is right; this proves the RESULT PLAYS.
        // Swap one clip's audio for another's inside a real bank, rebuild, re-read
        // through the parser, decode, and check the duration is now the donor's.
        var displayBank = skin.Banks.FirstOrDefault(b =>
            Path.GetFileName(b.Path).Contains("_display_", StringComparison.OrdinalIgnoreCase));
        if (displayBank is not null && !string.IsNullOrWhiteSpace(settings.VgmstreamPath))
        {
            var orig = displayBank.Read();
            var didx = BnkBuilder.ReadDidx(
                BnkBuilder.ReadSections(orig).First(s => s.Tag == "DIDX").Body);
            var body = BnkBuilder.ReadSections(orig).First(s => s.Tag == "DATA").Body;

            byte[] Slice(BnkBuilder.MediaEntry m)
            {
                var b = new byte[m.Size];
                Buffer.BlockCopy(body, (int)m.Offset, b, 0, (int)m.Size);
                return b;
            }

            var target = didx[0];
            var donor = didx.Skip(1).OrderByDescending(x => x.Size).First();
            var donorBytes = Slice(donor);

            W("");
            W($"mod build — real audio swap in {Path.GetFileName(displayBank.Path)}:");
            W($"  target {target.Id}  {preview.Probe(Slice(target), target.Id):0.000}s  ({target.Size:N0} B)");
            W($"  donor  {donor.Id}  {preview.Probe(donorBytes, donor.Id):0.000}s  ({donor.Size:N0} B)");

            var info = BnkBuilder.Inspect(donorBytes);
            W($"  donor codec check: {info.Codec}, {info.SampleRate} Hz, {info.Channels}ch");

            var built = BnkBuilder.Build(orig, new Dictionary<uint, byte[]> { [target.Id] = donorBytes });
            var outDir = Path.Combine(Path.GetTempPath(), "MRAudioKit", "modbuild");
            var outPath = Path.Combine(outDir, Path.GetFileName(displayBank.Path));
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(outPath, built.Bytes);
            W($"  wrote {outPath} ({built.Bytes.Length:N0} B, {built.Replaced} replaced)");

            var reread2 = new CUE4Parse.UE4.Wwise.WwiseReader(
                new CUE4Parse.UE4.Wwise.FWwiseArchive("modded.bnk", File.ReadAllBytes(outPath)),
                new CUE4Parse.UE4.Wwise.WwiseArchiveSource());
            var after = reread2.WwiseEncodedMedias[target.Id.ToString()].GetData();
            var secs = preview.Probe(after, 999000001);
            W($"  re-read {target.Id}: {after.Length:N0} B, decodes to {secs:0.000}s " +
              $"(donor was {preview.Probe(donorBytes, donor.Id):0.000}s)");
            W($"  every other media still readable: " +
              $"{didx.Skip(1).All(m => reread2.WwiseEncodedMedias.ContainsKey(m.Id.ToString()))}");
            W($"  HIRC intact: {reread2.Hierarchies?.Length} nodes");
        }

        // ---- the drop-pane naming convention --------------------------------
        // {MediaID}-{note}.wem : digits up front decide the assignment, everything
        // from the first hyphen is the author's note and must not affect it.
        if (displayBank is not null)
        {
            var orig = displayBank.Read();
            var sec = BnkBuilder.ReadSections(orig);
            var didx = BnkBuilder.ReadDidx(sec.First(s => s.Tag == "DIDX").Body);
            var dataBody = sec.First(s => s.Tag == "DATA").Body;
            var sample = new byte[didx[1].Size];
            Buffer.BlockCopy(dataBody, (int)didx[1].Offset, sample, 0, (int)didx[1].Size);

            var stage = Path.Combine(Path.GetTempPath(), "MRAudioKit", "stagetest");
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);

            var cases = new (string File, string ExpectId, string ExpectNote)[]
            {
                ($"{didx[0].Id}-my emote replacement.wem",        didx[0].Id.ToString(), "my emote replacement"),
                ($"{didx[2].Id}.wem",                             didx[2].Id.ToString(), ""),
                ($"{didx[3].Id}-hyphens-in-the-note-are-fine.wem",didx[3].Id.ToString(), "hyphens-in-the-note-are-fine"),
                ($"{didx[4].Id} - spaced note .wem",              didx[4].Id.ToString(), "spaced note"),
                ("not-a-media-id.wem",                            null,                  null),
            };
            foreach (var c in cases) File.WriteAllBytes(Path.Combine(stage, c.File), sample);
            File.WriteAllText(Path.Combine(stage, "readme.txt"), "ignored");

            W("");
            W("drop-pane naming ({MediaID}-{note}.wem):");
            var ok = 0;
            foreach (var c in cases)
            {
                var item = PendingWem.FromFile(Path.Combine(stage, c.File), out var why);
                var pass = c.ExpectId is null
                    ? item is null
                    : item is not null && item.MediaId.ToString() == c.ExpectId && item.Note == c.ExpectNote;
                if (pass) ok++;
                W($"  {(pass ? "ok  " : "FAIL")} {c.File,-46} -> " +
                  (item is null ? $"rejected: {why}" : $"id={item.MediaId}  note=\"{item.Note}\"  {item.Codec}"));
            }
            W($"  {ok}/{cases.Length} naming cases correct");

            // batch: a whole folder in, then a build from it
            var batch = Directory.EnumerateFiles(stage, "*.wem")
                .Select(f => PendingWem.FromFile(f, out _)).Where(x => x is not null).ToList();
            var payload = batch.ToDictionary(b => b.MediaId, b => File.ReadAllBytes(b.FullPath));
            var built2 = BnkBuilder.Build(orig, payload);
            W($"  batch staged {batch.Count} file(s) from a folder drop " +
              $"(readme.txt and the unnamed file ignored)");
            W($"  build with the batch: {built2.Replaced} media replaced, " +
              $"{orig.Length:N0} B -> {built2.Bytes.Length:N0} B");

            var check = new CUE4Parse.UE4.Wwise.WwiseReader(
                new CUE4Parse.UE4.Wwise.FWwiseArchive("batch.bnk", built2.Bytes),
                new CUE4Parse.UE4.Wwise.WwiseArchiveSource());
            var allSwapped = payload.Keys.All(id =>
                check.WwiseEncodedMedias.TryGetValue(id.ToString(), out var v) &&
                v.GetData().AsSpan().SequenceEqual(sample));
            W($"  every staged id carries the new bytes after re-parse: {allSwapped}");
            W($"  untouched media still present: " +
              $"{didx.Count(m => !payload.ContainsKey(m.Id) && check.WwiseEncodedMedias.ContainsKey(m.Id.ToString()))}" +
              $"/{didx.Count(m => !payload.ContainsKey(m.Id))}");
        }

        // ---- per-bank breakdown (what the tree's third level shows) ----------
        W("");
        W("banks in this skin:");
        foreach (var b in skin.Banks.OrderBy(b => Path.GetFileName(b.Path)))
        {
            var fn = Path.GetFileName(b.Path);
            var stem = Path.GetFileNameWithoutExtension(fn);
            var n = sounds.Rows.Count(r => r.Bank.Equals(stem, StringComparison.OrdinalIgnoreCase));
            var media = sounds.BankOfMedia.Count(kv => kv.Value.Equals(fn, StringComparison.OrdinalIgnoreCase));
            W($"  {fn,-34} {n,5} sounds · {media,4} embedded media · {b.Size / 1024.0 / 1024.0,6:0.0} MB");
        }

        // ---- what a staged file reports back to the author -------------------
        W("");
        W("staged-file reference (bank + what it replaces):");
        foreach (var pick in new[]
                 {
                     sounds.Rows.FirstOrDefault(r => r.Subtitle.Length > 0),
                     sounds.Rows.FirstOrDefault(r => r.Kind == "SFX" && r.EventName is not null),
                     sounds.Rows.FirstOrDefault(r => r.Kind == "DISPLAY" && r.EventName is not null),
                 }.Where(r => r is not null))
        {
            var inBank = sounds.BankOfMedia.GetValueOrDefault(pick.MediaId);
            var isLoose = session.LooseMedia(pick.MediaId) is not null;
            var lands = (inBank, isLoose) switch
            {
                (not null, true) => $"{inBank} + loose",
                (not null, false) => inBank,
                (null, true) => "loose wem",
                _ => "not in this skin",
            };
            var original = pick.Subtitle.Length > 0 ? pick.Subtitle : pick.Display;
            W($"  {pick.MediaId,-12} lands in {lands,-32} replaces: {original}");
        }

        // ---- is the bank's prefetch stub a byte prefix of the streamed file? --
        // Decides whether a streamed-voice mod must patch BOTH copies or only the
        // loose one. Reported rather than assumed.
        var both = sounds.Rows.FirstOrDefault(r =>
            sounds.Embedded.ContainsKey(r.MediaId) && session.LooseMedia(r.MediaId) is not null);
        if (both is not null)
        {
            var stub = sounds.Embedded[both.MediaId].GetData();
            var full = session.LooseMedia(both.MediaId).Read();
            var isPrefix = stub.Length <= full.Length &&
                           full.AsSpan(0, stub.Length).SequenceEqual(stub);
            W("");
            W($"prefetch stub check on media {both.MediaId} ({both.Display}):");
            W($"  bank stub {stub.Length:N0} B, streamed file {full.Length:N0} B");
            W($"  stub is a byte-prefix of the streamed file: {isPrefix}");

            // So a streamed swap must patch both, and the stub must be the first n
            // bytes of the NEW clip. Verify the build keeps that invariant.
            var bankOf = sounds.BankOfMedia[both.MediaId];
            var bankFile = skin.Banks.First(b => Path.GetFileName(b.Path) == bankOf);
            var donorFull = new byte[full.Length];
            new Random(99).NextBytes(donorFull);

            var trimmed = donorFull[..Math.Min(stub.Length, donorFull.Length)];
            var rebuilt = BnkBuilder.Build(bankFile.Read(),
                new Dictionary<uint, byte[]> { [both.MediaId] = trimmed });
            var readBack = new CUE4Parse.UE4.Wwise.WwiseReader(
                new CUE4Parse.UE4.Wwise.FWwiseArchive("pf.bnk", rebuilt.Bytes),
                new CUE4Parse.UE4.Wwise.WwiseArchiveSource())
                .WwiseEncodedMedias[both.MediaId.ToString()].GetData();
            W($"  after a streamed swap: new stub {readBack.Length:N0} B, " +
              $"still a prefix of the new loose file: " +
              $"{donorFull.AsSpan(0, readBack.Length).SequenceEqual(readBack)}");
        }

        // ---- diagnosing "+ loose" and the missing bank name ------------------
        // (a) Is the loose copy really in the ACTIVE language, or is the lookup
        //     falling back to another language's file?
        {
            var voRows = sounds.Rows.Where(r => r.Kind == "VO").Take(400).ToList();
            int active = 0, otherLang = 0, neutral = 0, none = 0;
            foreach (var r in voRows)
            {
                var f = session.LooseMedia(r.MediaId);
                if (f is null) { none++; continue; }
                if (f.Path.Contains($"/{session.Language}/", StringComparison.OrdinalIgnoreCase)) active++;
                else if (GameSession.Languages.Any(l => f.Path.Contains($"/{l}/", StringComparison.OrdinalIgnoreCase))) otherLang++;
                else neutral++;
            }
            W("");
            W($"loose-copy provenance for {voRows.Count} VO media (language {session.Language}):");
            W($"  in the active language : {active}");
            W($"  in ANOTHER language    : {otherLang}   <- would write to the wrong folder");
            W($"  language-neutral       : {neutral}");
            W($"  no loose copy at all   : {none}");
        }

        // (b) The global media -> bank index: cost, and whether it agrees with the
        //     per-skin parse for media we already resolved the slow way.
        {
            var t = DateTime.UtcNow;
            session.IndexBanks(_ => { });
            W("");
            W("global media -> bank index (DIDX only, no HIRC):");
            W($"  {session.MediaBank.Count:N0} media ids mapped in {(DateTime.UtcNow - t).TotalSeconds:0.0}s");

            var agree = 0; var missing = 0; var shared = 0;
            var examples = new List<string>();
            foreach (var (id, bankName) in sounds.BankOfMedia)
            {
                var banks = session.BanksForMedia(id);
                if (banks.Count == 0) { missing++; continue; }
                if (!banks.Any(p => Path.GetFileName(p).Equals(bankName, StringComparison.OrdinalIgnoreCase)))
                { missing++; continue; }
                agree++;
                if (banks.Count > 1)
                {
                    shared++;
                    if (examples.Count < 4)
                        examples.Add($"    {id} -> {string.Join(" , ", banks.Select(Path.GetFileName))}");
                }
            }
            W($"  contains the per-skin answer: {agree}, unresolved: {missing}");
            W($"  media embedded in MORE THAN ONE bank: {shared}");
            foreach (var ex in examples) W(ex);
            var multi = session.MediaBank.Count(kv => kv.Value.Count > 1);
            W($"  game-wide: {multi:N0} of {session.MediaBank.Count:N0} media ids live in several banks");
        }

        // ---- notes: persistence, and surviving a media-id change -------------
        // Runs against the real notes.json, so back it up and put it back.
        {
            var notesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MRAudioKit", "notes.json");
            var backup = File.Exists(notesPath) ? File.ReadAllBytes(notesPath) : null;
            try
            {
                if (File.Exists(notesPath)) File.Delete(notesPath);

                var a = sounds.Rows.First(r => r.EventName is not null);
                var b = sounds.Rows.Last(r => r.EventName is not null);

                var store = NoteStore.Load();
                a.Notes = "left click impact, 2 of 4";
                a.ExtraNotes = "loud - amplify -3 dB";
                b.Notes = "ult voice";
                store.Set(a); store.Set(b); store.Save();

                // fresh instance, as if the app had restarted
                foreach (var r in sounds.Rows) { r.Notes = ""; r.ExtraNotes = ""; }
                var reloaded = NoteStore.Load();
                reloaded.Apply(sounds.Rows);
                W("");
                W("notes:");
                W($"  saved 2, reloaded {reloaded.Count}");
                W($"  {a.MediaId}: notes=\"{a.Notes}\" extra=\"{a.ExtraNotes}\" -> " +
                  $"{(a.Notes == "left click impact, 2 of 4" && a.ExtraNotes == "loud - amplify -3 dB" ? "ok" : "FAIL")}");
                W($"  {b.MediaId}: notes=\"{b.Notes}\" -> {(b.Notes == "ult voice" ? "ok" : "FAIL")}");

                // Simulate a patch: the media id moved, the event name did not.
                var raw = File.ReadAllText(notesPath)
                              .Replace($"\"MediaId\": {a.MediaId}", "\"MediaId\": 4242424242");
                File.WriteAllText(notesPath, raw);
                foreach (var r in sounds.Rows) { r.Notes = ""; r.ExtraNotes = ""; }
                var afterPatch = NoteStore.Load();
                var moved = afterPatch.Apply(sounds.Rows);
                W($"  after the id changed: {moved} note(s) relinked by event name; " +
                  $"{a.MediaId} now reads \"{a.Notes}\" -> " +
                  $"{(a.Notes == "left click impact, 2 of 4" ? "ok" : "FAIL")}");
            }
            finally
            {
                if (backup is not null) File.WriteAllBytes(notesPath, backup);
                else if (File.Exists(notesPath)) File.Delete(notesPath);
            }

            // The case that motivated it: a media id from a character you have NOT opened.
            var other = session.Characters.FirstOrDefault(c => c.CharId != charId && c.Skins.Count > 0);
            if (other is not null)
            {
                var otherBank = other.Skins[0].Banks.FirstOrDefault();
                if (otherBank is not null)
                {
                    var raw = otherBank.Read();
                    var d = BnkBuilder.ReadSections(raw).FirstOrDefault(s => s.Tag == "DIDX");
                    if (d is not null)
                    {
                        var someId = BnkBuilder.ReadDidx(d.Body)[0].Id;
                        var banks2 = session.BanksForMedia(someId);
                        W($"  id {someId} from {other.Name} (never opened) resolves to: " +
                          (banks2.Count == 0 ? "(unresolved)"
                                             : string.Join(" , ", banks2.Select(Path.GetFileName))));
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(reportPath))
            File.WriteAllText(reportPath, log.ToString(), new UTF8Encoding(true));
        return 0;
    }
}
