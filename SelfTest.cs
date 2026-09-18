using System.IO;
using System.Text;

using Xzound;
using Xzound.Vorbis;

namespace MRAudioKit;

/// <summary>
/// Runs the exact code paths the window uses, with no window, and writes a report.
///     MRAudioKit.exe --selftest &lt;skinId&gt; [reportPath]
/// Present so the pipeline can be verified without a human clicking through it.
/// </summary>
public static class SelfTest
{
    /// <summary>
    ///     MRAudioKit.exe --wav2wem &lt;in.wav&gt; &lt;out.wem&gt;
    /// The WAV -> Wwise wem path on its own. Vorbis via XzoundCore; PCM only if the
    /// encoder cannot handle the input, since the game's banks all declare Vorbis.
    /// </summary>
    public static int WavToWem(string inWav, string outWem)
    {
        void W(string s) => Console.WriteLine(s);

        var src = WwiseWem.ReadWav(File.ReadAllBytes(inWav));
        W($"input : {Path.GetFileName(inWav)}  {src.SampleRate} Hz  {src.Channels}ch  " +
          $"{src.Bits}-bit  {src.Pcm.Length:N0} B of samples");

        byte[] wem;
        try { wem = XzoundCore.Encode(src); W("encoded by XzoundCore (Vorbis)"); }
        catch (Exception ex)
        {
            wem = WwiseWem.Write(src);
            W($"wrapped as PCM — XzoundCore could not encode it: {ex.Message}");
        }

        File.WriteAllBytes(outWem, wem);
        var info = BnkBuilder.Inspect(wem);
        W($"output: {Path.GetFileName(outWem)}  {wem.Length:N0} B  " +
          $"{info.Codec}  {info.SampleRate} Hz  {info.Channels}ch");
        return 0;
    }

    /// <summary>
    ///     MRAudioKit.exe --vorbistest &lt;in.wav&gt; &lt;out.wem&gt;
    /// Encode with our own Wwise Vorbis writer and check vgmstream reads it back.
    /// </summary>
    public static int VorbisTest(string inWav, string outWem)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();

        var src = WwiseWem.ReadWav(File.ReadAllBytes(inWav));
        W($"input : {src.SampleRate} Hz {src.Channels}ch {src.Bits}-bit, " +
          $"{src.Pcm.Length / 2 / src.Channels:N0} samples");

        var t0 = DateTime.UtcNow;
        byte[] wem;
        try { wem = XzoundCore.Encode(src); }
        catch (Exception ex) { W("ENCODE FAILED: " + ex); return 1; }
        File.WriteAllBytes(outWem, wem);
        W($"output: {wem.Length:N0} B in {(DateTime.UtcNow - t0).TotalMilliseconds:N0} ms " +
          $"(input PCM was {src.Pcm.Length:N0} B)");

        var info = BnkBuilder.Inspect(wem);
        W($"header: tag=0x{info.FormatTag:X4} {info.Codec} {info.SampleRate} Hz {info.Channels}ch");

        var vgm = settings.VgmstreamPath;
        if (string.IsNullOrWhiteSpace(vgm) || !File.Exists(vgm)) { W("no vgmstream"); return 0; }
        var psi = new System.Diagnostics.ProcessStartInfo(vgm, $"-m \"{outWem}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var pr = System.Diagnostics.Process.Start(psi);
        var so = pr.StandardOutput.ReadToEnd(); var se = pr.StandardError.ReadToEnd(); pr.WaitForExit();
        W("--- vgmstream ---");
        foreach (var l in (so + se).Split('\n')) if (l.Trim().Length > 0) W("  " + l.Trim());
        return so.Contains("play duration") ? 0 : 1;
    }

    /// <summary>
    ///     MRAudioKit.exe --setuproundtrip &lt;file.wem&gt;
    /// Read a shipped stripped setup packet, write it back, and diff. Byte-identical
    /// means the writer is correct; a mismatch localises the bad field.
    /// </summary>
    public static int SetupRoundTrip(string wemPath)
    {
        void W(string s) => Console.WriteLine(s);
        var b = File.ReadAllBytes(wemPath);
        int fo = -1, dof = -1; var p = 12;
        while (p + 8 <= b.Length)
        {
            var cid = System.Text.Encoding.ASCII.GetString(b, p, 4);
            var sz = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4, 4));
            if (cid == "fmt ") fo = p + 8;
            if (cid == "data") { dof = p + 8; break; }
            p += 8 + sz + (sz & 1);
        }
        var channels = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(fo + 2, 2));
        var ext = b.AsSpan(fo + 18, 48);
        var setupOffset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(ext.Slice(0x16, 4));
        var setupSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            b.AsSpan(dof + setupOffset, 2));
        var original = b[(dof + setupOffset + 2)..(dof + setupOffset + 2 + setupSize)];
        W($"{Path.GetFileName(wemPath)}: {channels}ch, stripped setup = {original.Length} B");

        SetupPacket parsed;
        try { parsed = SetupPacket.ParseWwise(original, channels); }
        catch (Exception ex) { W("PARSE FAILED: " + ex.Message); return 1; }

        byte[] rewritten;
        try { rewritten = parsed.WriteWwise(XzoundCore.Library, channels); }
        catch (Exception ex) { W("WRITE FAILED: " + ex.Message); return 1; }

        W($"re-emitted: {rewritten.Length} B");
        if (rewritten.Length == original.Length && rewritten.AsSpan().SequenceEqual(original))
        { W("ROUND TRIP IDENTICAL — the writer is correct"); return 0; }

        var n = Math.Min(original.Length, rewritten.Length);
        var at = -1;
        for (var i = 0; i < n; i++) if (original[i] != rewritten[i]) { at = i; break; }
        W($"DIFFERS: first byte at {(at < 0 ? "(length only)" : at.ToString())}" +
          $"  original {original.Length} vs {rewritten.Length}");
        if (at >= 0)
        {
            var lo = Math.Max(0, at - 4); var hi = Math.Min(n, at + 8);
            W($"  original  : {Convert.ToHexString(original[lo..hi])}");
            W($"  rewritten : {Convert.ToHexString(rewritten[lo..hi])}");
            W($"  (bit {at * 8} onwards)");
        }
        return 1;
    }

    /// <summary>
    ///     MRAudioKit.exe --recompress &lt;bankOrFolder&gt; &lt;outFolder&gt;
    /// Re-encode every PCM entry in a bank (or every .bnk in a folder) to Vorbis.
    /// </summary>
    public static int Recompress(string input, string outDir)
    {
        void W(string s) => Console.WriteLine(s);
        var banks = Directory.Exists(input)
            ? Directory.EnumerateFiles(input, "*.bnk", SearchOption.AllDirectories).OrderBy(x => x).ToList()
            : [input];
        W($"{banks.Count} bank(s)");
        Directory.CreateDirectory(outDir);

        long tb = 0, ta = 0;
        foreach (var b in banks)
        {
            var src = File.ReadAllBytes(b);
            var name = Path.GetFileName(b);
            var t0 = DateTime.UtcNow;
            var r = BankRecompress.Run(src, m => W($"  [{name}] {m}"));
            var rel = Directory.Exists(input) ? Path.GetRelativePath(input, b) : name;
            var dst = Path.Combine(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst))!);
            File.WriteAllBytes(dst, r.Bytes);
            tb += src.Length; ta += r.Bytes.Length;
            W($"  {name,-24} {src.Length,13:N0} -> {r.Bytes.Length,12:N0} B  " +
              $"({(src.Length == 0 ? 0 : 100.0 * r.Bytes.Length / src.Length),5:F1}%) " +
              $"converted={r.Converted} kept={r.Skipped} failed={r.Failed} " +
              $"in {(DateTime.UtcNow - t0).TotalSeconds:N1}s");
            foreach (var note in r.Notes.Take(5)) W("      " + note);
        }
        W("");
        W($"TOTAL {tb:N0} -> {ta:N0} B  ({(tb == 0 ? 0 : 100.0 * ta / tb):F1}%, " +
          $"{(ta == 0 ? 0 : (double)tb / ta):F1}x smaller)");
        return 0;
    }

    /// <summary>
    ///     MRAudioKit.exe --import &lt;audioFile&gt; &lt;out.wem&gt;
    /// Convert any supported audio file (wav, mp3, ogg, flac, m4a, aac, wma) to a
    /// Wwise Vorbis wem -- the same path a drag-and-drop takes.
    /// </summary>
    public static int Import(string inPath, string outWem)
    {
        void W(string s) => Console.WriteLine(s);
        var t0 = DateTime.UtcNow;

        WwiseWem.WavData pcm;
        try { pcm = AudioDecode.ToPcm(inPath); }
        catch (Exception ex) { W("DECODE FAILED: " + ex.Message); return 1; }

        var secs = pcm.Pcm.Length / 2.0 / pcm.Channels / pcm.SampleRate;
        W($"decoded: {pcm.SampleRate} Hz {pcm.Channels}ch {pcm.Bits}-bit, {secs:N1} s " +
          $"({pcm.Pcm.Length:N0} B PCM) in {(DateTime.UtcNow - t0).TotalMilliseconds:N0} ms");

        byte[] wem;
        var t1 = DateTime.UtcNow;
        try { wem = XzoundCore.Encode(pcm); }
        catch (Exception ex) { W("ENCODE FAILED: " + ex.Message); return 1; }
        File.WriteAllBytes(outWem, wem);

        var info = BnkBuilder.Inspect(wem);
        W($"wem: {wem.Length:N0} B  tag=0x{info.FormatTag:X4} {info.Codec} " +
          $"{info.SampleRate} Hz {info.Channels}ch  " +
          $"({100.0 * wem.Length / Math.Max(1, new FileInfo(inPath).Length):N1}% of the source file) " +
          $"in {(DateTime.UtcNow - t1).TotalMilliseconds:N0} ms");
        return 0;
    }

    /// <summary>
    ///     MRAudioKit.exe --openbank &lt;bnkOrPak&gt; [rows]
    /// What the window shows when someone opens a bank that did not come from the game.
    /// </summary>
    public static int OpenBank(string path, int show)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();

        ModBank.Opened opened;
        try { opened = ModBank.Open(path, settings); }
        catch (Exception ex) { W("OPEN FAILED: " + ex.Message); return 1; }

        var banks = opened.Banks;
        if (opened.Skipped.Count > 0)
            W($"skipped {opened.Skipped.Count} container(s): {string.Join(", ", opened.Skipped)}");
        W($"{banks.Count} bank(s) in {Path.GetFileName(path)}:");
        foreach (var b in banks) W($"  {b.Name,-34} {b.Bytes.Length,13:N0} B");

        // Names come from the game, so mount it -- but a bank must still open without it.
        GameSession session = null;
        try
        {
            session = new GameSession();
            session.Mount(settings, _ => { });
        }
        catch (Exception ex) { W("(game not mounted: " + ex.Message + ")"); session = null; }


        foreach (var b in banks)
        {
            var sounds = ModBank.ToSounds(session, b);
            var named = sounds.Rows.Count(r => r.EventName is not null);
            var vorbis = sounds.Rows.Count(r => r.Codec == "VORBIS");
            var held = sounds.Rows.Count(r => sounds.Raw.ContainsKey(r.MediaId));
            W("");
            var mod = sounds.Rows.Count(r => r.ModTag == "MODDED");
            var fresh = sounds.Rows.Count(r => r.ModTag == "NEW");
            var stock = sounds.Rows.Count(r => r.ModTag == "");
            W($"{b.Name}: {sounds.Rows.Count} media, {named} named, {vorbis} VORBIS, " +
              $"{sounds.Rows.Count - vorbis} other, {held} playable");
            W($"  vs shipped: {mod} modded, {fresh} new, {stock} unchanged" +
              (mod + fresh + stock == 0 ? "  (no shipped bank to compare against)" : ""));
            foreach (var r in sounds.Rows.Take(show))
                W($"  {r.MediaId,-11} {r.Codec,-7} {r.Length,-8} {r.Display}" +
                  (string.IsNullOrEmpty(r.Subtitle) ? "" : $"   \"{r.Subtitle}\""));
        }
        return 0;
    }

    /// <summary>
    ///     MRAudioKit.exe --volume &lt;skinId&gt; &lt;gain&gt; &lt;outFolder&gt; [bankFilter]
    /// Write banks with every sound scaled by gain, and check the result decodes.
    /// </summary>
    public static int Volume(string skinId, string gainText, string outDir, string bankFilter)
    {
        void W(string s) => Console.WriteLine(s);
        if (!double.TryParse(gainText, System.Globalization.CultureInfo.InvariantCulture, out var gain))
        { W("gain must be a number, e.g. 0.8"); return 2; }

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
        W($"{scope.Count} bank(s), gain {gain:0.0##}  ->  {VolumeTool.Label(gain)}");

        var t0 = DateTime.UtcNow;
        var r = VolumeBankBuilder.Build(session, scope, _ => gain, outDir, sounds,
                                        settings.VgmstreamPath, W);
        W("");
        W(r.Log);
        W($"{r.Scaled} scaled, {r.Skipped} untouched, {r.Failed} failed " +
          $"in {(DateTime.UtcNow - t0).TotalSeconds:N1}s");
        return r.Failed == 0 && r.Scaled > 0 ? 0 : 1;
    }

    /// <summary>
    ///     MRAudioKit.exe --translate &lt;skinId&gt; [--fetch]
    /// Show what the Category column translates to, and optionally fetch the rest
    /// from whichever provider is configured.
    /// </summary>
    public static int TranslateCategories(string skinId, bool fetch)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        var charId = skinId[..4];
        var skin = session.Characters.First(c => c.CharId == charId).Skins.First(s => s.SkinId == skinId);
        var sounds = SoundIndex.Build(session, skin, charId, _ => { });
        var cats = sounds.Rows.Select(r => r.Category).Where(c => !string.IsNullOrEmpty(c))
                              .Distinct().ToList();

        var missing = CategoryTranslator.Untranslatable(session, cats).ToList();
        W($"{cats.Count} categories, {missing.Count} phrase(s) with no translation, " +
          $"{LiveTranslate.Count} cached");
        W($"provider: {settings.TranslateProvider}  ({LiveTranslate.WhyNot(settings) ?? "ready"})");

        if (fetch && missing.Count > 0)
        {
            W($"sending {missing.Count} phrase(s) to {LiveTranslate.Destination(settings)}…");
            try
            {
                // Off the dispatcher thread: blocking on it while the awaits try to post
                // back to it is a deadlock, which is exactly what happened.
                var added = Task.Run(() => LiveTranslate.FetchAsync(settings, missing, W))
                                .GetAwaiter().GetResult();
                W($"fetched {added} of {missing.Count}");
            }
            catch (Exception ex) { W("FAILED: " + ex.Message); return 1; }
        }

        var report = new StringBuilder();
        var left = 0;
        foreach (var c in cats.OrderBy(x => x, StringComparer.Ordinal))
        {
            var t = CategoryTranslator.Translate(session, c);
            if (CategoryTranslator.NeedsTranslation(t)) left++;
            report.AppendLine($"{c}\t{t}");
        }
        var path = Path.Combine(Path.GetTempPath(), "MRAudioKit", "translated.tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
        W($"{cats.Count - left}/{cats.Count} translated; wrote {path}");
        return 0;
    }

    /// <summary>
    ///     MRAudioKit.exe --merge &lt;priority&gt; &lt;secondary&gt; &lt;outFolder&gt;
    /// Combine two mods, priority winning any sound both changed.
    /// </summary>
    public static int Merge(string priority, string secondary, string outDir)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        try
        {
            var r = ModMerge.Build(session, priority, secondary, outDir, settings,
                                   Path.Combine(outDir, "_extracted"), W);
            W("");
            W(r.Log);
            W($"{r.Banks} bank(s) written, {r.FromPriority} from priority, " +
              $"{r.FromSecondary} from secondary, {r.Collisions.Count} collision(s)");
            return r.Banks > 0 ? 0 : 1;
        }
        catch (Exception ex) { W("MERGE FAILED: " + ex.Message); return 1; }
    }

    /// <summary>MRAudioKit.exe --banks — what the picker can reach.</summary>
    public static int Banks()
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        var charBanks = session.Characters.SelectMany(c => c.Skins).SelectMany(s => s.Banks)
                               .Select(b => Path.GetFileName(b.Path))
                               .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var otherBanks = session.OtherBanks.SelectMany(g => g.Skins).Count();

        var shipped = session.Provider.Files.Keys
            .Where(k => k.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        W($"{shipped} distinct banks ship with the game (all languages).");
        W($"  characters: {charBanks} across {session.Characters.Count} heroes");
        W($"  other:      {otherBanks} in {session.OtherBanks.Count} group(s)");
        foreach (var g in session.OtherBanks)
            W($"     {g.Name,-28} e.g. {string.Join(", ", g.Skins.Take(3).Select(s => s.Label))}");

        // Prove one loads through the SAME path a character does, with no character.
        W("");
        foreach (var probe in new[] { "bnk_ui_battle", "bnk_amb_arakkoe01", "bnk_vo_boss_vampirepve_dracula" })
        {
            var entry = session.OtherBanks.SelectMany(g => g.Skins)
                .FirstOrDefault(s => s.SkinId.Equals(probe, StringComparison.OrdinalIgnoreCase));
            if (entry is null) { W($"  {probe}: not catalogued"); continue; }
            var t0 = DateTime.UtcNow;
            var rows = SoundIndex.Build(session, entry, "", _ => { });
            var named = rows.Rows.Count(r => r.EventName is not null);
            W($"  {probe,-34} {rows.Rows.Count,5} rows, {named,5} named, " +
              $"{rows.Embedded.Count,4} embedded media  ({(DateTime.UtcNow - t0).TotalSeconds:N1}s)");
        }
        return 0;
    }

    /// <summary>MRAudioKit.exe --dumpmedia &lt;id&gt; &lt;out.wem&gt; — pull one loose media file out.</summary>
    public static int DumpMedia(string idText, string outPath)
    {
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });
        if (!uint.TryParse(idText, out var id)) { Console.WriteLine("bad id"); return 2; }
        var f = session.LooseMedia(id);
        if (f is null) { Console.WriteLine("no loose media for " + id); return 2; }
        File.WriteAllBytes(outPath, f.Read());
        Console.WriteLine($"{f.Path} -> {outPath} ({new FileInfo(outPath).Length:N0} B)");
        return 0;
    }

    /// <summary>
    ///     MRAudioKit.exe --streamonly
    /// How much of the game's audio can ONLY be modded as a loose Media/ file --
    /// i.e. how much genuinely requires Vorbis, since PCM is fine inside a bank.
    /// </summary>
    public static int StreamOnly()
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });
        session.IndexBanks(_ => { });

        var inBank = session.MediaBank.Keys.ToHashSet();

        var loose = new Dictionary<uint, string>();
        const string root = "Marvel/Content/WwiseAudio/Media/";
        foreach (var k in session.Provider.Files.Keys)
        {
            if (!k.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)) continue;
            if (!k.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (uint.TryParse(Path.GetFileNameWithoutExtension(k), out var id)) loose[id] = k;
        }

        var both = loose.Keys.Count(id => inBank.Contains(id));
        var onlyLoose = loose.Keys.Where(id => !inBank.Contains(id)).ToList();

        W($"media embedded in some bank        : {inBank.Count:N0}");
        W($"loose files under WwiseAudio/Media : {loose.Count:N0}");
        W($"  also present in a bank (PCM ok)  : {both:N0}");
        W($"  LOOSE ONLY -> needs Vorbis       : {onlyLoose.Count:N0}");

        var byArea = onlyLoose.Select(id => loose[id])
            .Select(p => p[root.Length..].Split('/')[0])
            .GroupBy(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"^\d\d$") ? "(language-neutral)" : x)
            .OrderByDescending(g => g.Count());
        foreach (var g in byArea) W($"      {g.Count(),8:N0}  {g.Key}");

        // Size tells us what these are. Long files = music/ambience, which is exactly
        // the kind of thing people want to replace.
        long OnlySize(uint id) => session.Provider.Files[loose[id]].Size;
        var onlyBytes = onlyLoose.Sum(OnlySize);
        var bothBytes = loose.Keys.Where(id => inBank.Contains(id)).Sum(OnlySize);
        W("");
        W($"loose-only total  : {onlyBytes / 1024.0 / 1024.0:N0} MB, " +
          $"avg {onlyBytes / Math.Max(1, onlyLoose.Count) / 1024.0:N0} KB");
        W($"bank-backed total : {bothBytes / 1024.0 / 1024.0:N0} MB, " +
          $"avg {bothBytes / Math.Max(1, both) / 1024.0:N0} KB");
        W("largest loose-only files (decoded, to see what they actually are):");
        var prev = new AudioPreview { VgmstreamPath = settings.VgmstreamPath };
        foreach (var id in onlyLoose.OrderByDescending(OnlySize).Take(6))
        {
            var secs = prev.Probe(session.Provider.Files[loose[id]].Read(), id);
            W($"      {OnlySize(id) / 1024.0 / 1024.0,7:N1} MB  " +
              $"{(secs is null ? "?" : TimeSpan.FromSeconds(secs.Value).ToString(@"m\:ss")),8}  {loose[id]}");
        }
        return 0;
    }

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
    public static int BuildMod(string skinId, string wemDir, string outDir, bool prefetch = false)
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
        if (prefetch) W("prefetch mode ON");
        var r = ModBuilder.Build(session, skin, pending, outDir, bankPaths, prefetch);

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

        // ---- projects: notes, staged files, surviving a media-id change ------
        {
            var projPath = Path.Combine(Path.GetTempPath(), "MRAudioKit", "selftest.mrak");
            Directory.CreateDirectory(Path.GetDirectoryName(projPath)!);
            if (File.Exists(projPath)) File.Delete(projPath);

            var na = sounds.Rows.First(r => r.EventName is not null);
            var nb = sounds.Rows.Last(r => r.EventName is not null);

            var proj = Project.New();
            proj.Name = "selftest";
            proj.Character = charId; proj.Skin = skin.SkinId;
            na.Notes = "left click impact, 2 of 4";
            na.ExtraNotes = "loud - amplify -3 dB";
            nb.Notes = "ult voice";
            proj.SetNote(na); proj.SetNote(nb);
            proj.Replacements.Add(new ProjectReplacement
            { MediaId = na.MediaId, File = "audio/mine.wem", Note = "my clip" });
            proj.Save(projPath);

            W("");
            W("projects:");
            W($"  saved: {proj.Notes.Count} note(s), {proj.Replacements.Count} staged, dirty={proj.IsDirty}");

            foreach (var r in sounds.Rows) { r.Notes = ""; r.ExtraNotes = ""; }
            var re = Project.Load(projPath);
            re.ApplyNotes(sounds.Rows);
            W($"  reloaded: name=\"{re.Name}\" skin={re.Skin} notes={re.Notes.Count} staged={re.Replacements.Count}");
            W($"  {na.MediaId}: \"{na.Notes}\" / \"{na.ExtraNotes}\" -> " +
              $"{(na.Notes == "left click impact, 2 of 4" && na.ExtraNotes == "loud - amplify -3 dB" ? "ok" : "FAIL")}");
            W($"  {nb.MediaId}: \"{nb.Notes}\" -> {(nb.Notes == "ult voice" ? "ok" : "FAIL")}");

            var raw = File.ReadAllText(projPath)
                          .Replace($"\"MediaId\": {na.MediaId}", "\"MediaId\": 4242424242");
            File.WriteAllText(projPath, raw);
            foreach (var r in sounds.Rows) { r.Notes = ""; r.ExtraNotes = ""; }
            var movedCount = Project.Load(projPath).ApplyNotes(sounds.Rows);
            W($"  after the id changed: {movedCount} note(s) relinked by event name; " +
              $"{na.MediaId} reads \"{na.Notes}\" -> " +
              $"{(na.Notes == "left click impact, 2 of 4" ? "ok" : "FAIL")}");

            // A numbered test bank tags the sounds it numbered. Numbers go to MEDIA
            // ids, not rows: one clip can serve several events, so every row sharing
            // an id carries the same number and `carried` is legitimately higher than
            // the number of assignments. Assigning per row would hand the same id two
            // different numbers, which no real test bank can produce.
            foreach (var r in sounds.Rows) r.TestNumber = null;
            var assign = sounds.Rows.Select(r => r.MediaId).Distinct().Take(5)
                               .Select((id, i) => (Number: i * 7, MediaId: id)).ToList();
            var tagged = re.RecordTestNumbers(assign, sounds.Rows);
            var carried = sounds.Rows.Count(r => r.TestNumber is not null);
            var numbered = sounds.Rows.First(r => r.MediaId == assign[0].MediaId);
            var shared = sounds.Rows.Count(r => r.MediaId == assign[0].MediaId);
            W($"  test numbers: tagged {tagged} media id(s), rows carrying one {carried} " +
              $"({shared} share the first id), it shows \"{numbered.TestLabel}\" -> " +
              $"{(tagged == 5 && numbered.TestLabel == "#0" ? "ok" : "FAIL")}");
            re.Save(projPath);
            foreach (var r in sounds.Rows) r.TestNumber = null;
            Project.Load(projPath).ApplyNotes(sounds.Rows);
            numbered = sounds.Rows.First(r => r.MediaId == assign[0].MediaId);
            W($"  survives save/load: \"{numbered.TestLabel}\" -> " +
              $"{(numbered.TestLabel == "#0" ? "ok" : "FAIL")}");

            var relProbe = Path.Combine(Path.GetDirectoryName(projPath)!, "audio", "x.wem");
            W($"  path stored relative: \"{re.ToStoredPath(relProbe)}\" -> " +
              $"{(re.ToStoredPath(relProbe) == Path.Combine("audio", "x.wem") ? "ok" : "FAIL")}");
            File.Delete(projPath);
        }

        if (!string.IsNullOrWhiteSpace(reportPath))
            File.WriteAllText(reportPath, log.ToString(), new UTF8Encoding(true));
        return 0;
    }
}
