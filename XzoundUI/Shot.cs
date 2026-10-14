using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MRAudioKit.UI;

/// <summary>
/// Render the window to a file instead of a screen.
///
/// A layout claim is a visual claim, and the usual way to check one is to open the app
/// and look — which needs a screen, needs the person to be at it, and proves nothing
/// afterwards. Avalonia can run with no windowing system at all and hand back the
/// frame it would have drawn, so the layout can be looked at on any machine and, more
/// usefully, asserted about by a test.
///
/// This is also the honest way to show work: a screenshot of what was actually built,
/// rather than a description of it.
/// </summary>
public static class Shot
{
    /// <summary>XzoundWave --shot &lt;file.png&gt; [width] [height]</summary>
    /// <param name="stage">
    /// Files or folders to stage before the frame is taken. An empty pane on the right
    /// shows that the pane exists and nothing at all about whether its columns read
    /// correctly, and those columns are worked out from the open skin and the bank
    /// index rather than from the file, so they can only be looked at with real audio
    /// against real ids.
    /// </param>
    /// <param name="panel">
    /// Which of the right panel's sub-panels to show, "files" or "details". The switch is a
    /// click and a render has nobody to do the clicking, so the two sub-panels could
    /// otherwise not be looked at. Null leaves whichever one the settings remember.
    /// </param>
    /// <param name="select">
    /// How many of the list's rows to select first. The details sub-panel says one thing for
    /// one row, another for several and another again for none, and all three are layouts
    /// somebody has to be able to look at.
    /// </param>
    /// <param name="media">
    /// Select the row with this media id instead of the first ones. What the details panel
    /// draws depends entirely on what the row turns out to carry, so a render of an
    /// arbitrary row cannot show the sections that only some rows have.
    /// </param>
    /// <param name="bank">
    /// Which of the skin's .bnk files to open, by any part of its name. The voice bank by
    /// default, which is what every render has shown; the rows worth looking at are not all
    /// in it, and a silenced sound or a music clip cannot be rendered from one.
    /// </param>
    /// <param name="expand">
    /// Open the details panel's expanders. Both of them are shut on purpose, and one of them
    /// now carries a box per medium for a mixer family that runs to two hundred of them, so
    /// what that looks like could otherwise be rendered only as a closed header.
    /// </param>
    /// <param name="click">
    /// Press the label half of this box, counting from one, after everything else. What a
    /// click on a box does to the panel it is drawn on is the panel's central rule and was
    /// the shape of a defect in it, and until now it was the one surface here that no render
    /// could reach.
    /// </param>
    /// <summary>
    /// XzoundWave --shot-voicelines &lt;file.png&gt;: the voice-lines window after a few lines are
    /// made through its own controls, and the main window's "New lines" node they appear
    /// under (to &lt;file&gt;-main.png). Nothing is saved: the project is the unsaved one the
    /// harness starts with.
    /// </summary>
    /// <param name="buildAudio">
    /// Also press Build, as the button does: this clip staged on every new line and, as a
    /// regular swap, on one of Hela's own voice media, so the lines are built on top of a bank
    /// the regular build just rewrote. Output in &lt;file&gt;'s folder under build/, with the
    /// project saved beside it for <c>--voicelinecheck</c>.
    /// </param>
    public static int TakeVoiceLines(string path, int width = 1100, int height = 720, string buildAudio = null)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();

            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            Dispatcher.UIThread.RunJobs();

            var window = main.OpenVoiceLinesForShot()
                         ?? throw new InvalidOperationException("the voice-lines window did not open (game not loaded?)");
            window.Width = width;
            window.Height = height;
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine(window.DriveForShot(() => Dispatcher.UIThread.RunJobs()));
            Dispatcher.UIThread.RunJobs();

            Save(window.CaptureRenderedFrame(), path);

            // The lines survive a save and an open with every id they were given: an id that
            // changed would orphan the line's staged audio and its row in a built mod.
            var file = Path.ChangeExtension(Path.GetFullPath(path), Project.Extension);
            main.CurrentProject.Save(file);
            var back = Project.Load(file);
            string Lines(Project p) => string.Join("\n", p.VoiceLines.Select(l =>
                $"{l.Set}|{l.Target}|{l.Subtitle}|{l.HearSelf}{l.HearAllies}{l.HearEnemies}|{l.VoiceId}|{l.MediaId}"));
            Console.WriteLine(Lines(back) == Lines(main.CurrentProject) && back.VoiceLines.Count > 0
                ? $"project round trip: {back.VoiceLines.Count} line(s) identical after save and open ({file})"
                : "project round trip: FAILED, the lines read back differ");

            main.RevealNewLines();
            Dispatcher.UIThread.RunJobs();
            Save(main.CaptureRenderedFrame(), Path.ChangeExtension(path, null) + "-main.png");

            if (buildAudio is not null)
            {
                var session = main.CurrentSession;
                if (!session.BankIndexReady) session.IndexBanks(_ => { });
                var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "build");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                var staged = Directory.CreateDirectory(Path.Combine(dir, "staged")).FullName;
                var heroBank = session.Bank("Marvel/Content/WwiseAudio/English(US)/bnk_vo_1024001.bnk").Read();
                var swap = BnkBuilder.ReadDidx(BnkBuilder.ReadSections(heroBank).Single(s => s.Tag == "DIDX").Body).First().Id;
                var ext = Path.GetExtension(buildAudio);
                var files = new List<string>();
                void Copy(uint id, string note)
                {
                    var to = Path.Combine(staged, $"{id}-{note}{ext}");
                    File.Copy(buildAudio, to, true);
                    files.Add(to);
                }
                Copy(swap, "swap");
                foreach (var l in main.CurrentProject.VoiceLines) Copy(l.MediaId, "line");
                Console.WriteLine($"staged: {main.StageSync(files)} file(s); the swap is media {swap}");
                Dispatcher.UIThread.RunJobs();

                var outDir = Path.Combine(dir, "out");
                var build = main.BuildForShot(outDir);
                while (!build.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
                Dispatcher.UIThread.RunJobs();
                Console.WriteLine("status: " + main.Status.Text);
                main.CurrentProject.Save(Path.Combine(dir, "project.xzw"));
                Console.WriteLine($"check with: --voicelinecheck \"{Path.Combine(dir, "project.xzw")}\" \"{outDir}\" {swap}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not render the voice-lines window: " + ex);
            return 1;
        }

        static void Save(Bitmap frame, string to)
        {
            if (frame is null) throw new InvalidOperationException("no frame was rendered");
            var full = Path.GetFullPath(to);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var file = File.Create(full)) frame.Save(file);
            Console.WriteLine($"-> {full}");
        }
    }

    /// <summary>
    /// XzoundWave --shot-volumes &lt;folder&gt; &lt;levels&gt;: Hela's voice bank, built at each level
    /// through the button's own path, as it would be from the Build tab.
    /// </summary>
    public static int TakeVolumes(string folder, string levels)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            Dispatcher.UIThread.RunJobs();
            main.ShowSkinSync("1024001", "vo");
            Dispatcher.UIThread.RunJobs();
            var build = main.BuildVolumesForShot(folder, levels);
            while (!build.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine("status: " + main.Status.Text);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the volume build: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// XzoundWave --shot-split &lt;audio&gt; &lt;out&gt; &lt;install&gt;: one project holding a swap in Hela's
    /// voice bank, a swap of a loose map-ambience wem no bank embeds, and a new Loki line, built
    /// with "One .pak per character" on and installed into &lt;install&gt; (never the game's ~mods).
    /// </summary>
    public static int TakeSplit(string audio, string outDir, string installDir)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            Dispatcher.UIThread.RunJobs();
            var session = main.CurrentSession;
            if (!session.BankIndexReady) session.IndexBanks(_ => { });

            var helaBank = session.Bank("Marvel/Content/WwiseAudio/English(US)/bnk_vo_1024001.bnk").Read();
            var helaMedia = BnkBuilder.ReadDidx(BnkBuilder.ReadSections(helaBank).Single(x => x.Tag == "DIDX").Body).First().Id;
            var ambient = session.Provider.Files.Keys
                .Where(k => k.Contains("/WwiseAudio/Media/", StringComparison.OrdinalIgnoreCase) && !k.Contains("(", StringComparison.Ordinal))
                .Select(k => uint.Parse(Path.GetFileNameWithoutExtension(k)))
                .First(id => !session.MediaBank.ContainsKey(id) && session.StreamingBank(id) is { } b && b.Contains("bnk_amb_"));
            Console.WriteLine($"swaps: Hela {helaMedia}, ambience {ambient} (streamed by {Path.GetFileName(session.StreamingBank(ambient))})");

            var sets = main.VoiceSetsOrNull();
            var loki = sets.First(x => x.HeroId == "1016" && x.IsDefault);
            var line = new VoiceLine
            {
                Set = loki.Key, Target = "HealthPackVoices.Voices", Subtitle = "split build test",
                VoiceId = VoiceLineBuilder.AllocateVoiceId(session, loki, [], null),
            };
            main.CurrentProject.VoiceLines.Add(line);
            main.VoiceLinesChanged();

            var staged = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(outDir) + "-staged")).FullName;
            var ext = Path.GetExtension(audio);
            var files = new[] { (helaMedia, "hela"), (ambient, "ambience"), (line.MediaId, "loki-line") }
                .Select(x => { var to = Path.Combine(staged, $"{x.Item1}-{x.Item2}{ext}"); File.Copy(audio, to, true); return to; }).ToList();
            Console.WriteLine($"staged: {main.StageSync(files)}");
            Dispatcher.UIThread.RunJobs();

            var run = main.BuildAndInstallForShot(outDir, installDir, split: true);
            while (!run.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine("status: " + main.Status.Text);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the split build: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// XzoundWave --shot-relink &lt;audio&gt; &lt;dir&gt;: a file staged on a Hela sound and the project
    /// saved; the saved file then rewritten as a project from before a patch would be (the file
    /// on a media id the game no longer gives that sound); and opened again. The file has to
    /// follow its Sound back to the medium it plays now.
    /// </summary>
    /// <param name="loose">A medium only a loose Media file holds, streamed by an ambience bank's Sound, instead of a voice line.</param>
    public static int TakeRelink(string audio, string dir, bool loose = false)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            Dispatcher.UIThread.RunJobs();
            var session = main.CurrentSession;
            if (!session.BankIndexReady) session.IndexBanks(_ => { });
            void Wait(Task t) { while (!t.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); } Dispatcher.UIThread.RunJobs(); }

            uint media;
            if (loose)
            {
                var path = session.Provider.Files.Keys.First(k => k.EndsWith("/bnk_amb_asgarde01.bnk", StringComparison.OrdinalIgnoreCase));
                var shape = ModUpdate.Read(session.Provider.Files[path].Read());
                media = shape.SoundSource.Where(kv => shape.SoundBodies[kv.Key].PlaysMedia).Select(kv => kv.Value)
                             .Where(m => !session.MediaBank.ContainsKey(m) && session.LooseMedia(m) is not null).Min();
                Console.WriteLine($"loose-only medium {media}: no bank holds it, {session.LooseMedia(media).Path} does");
            }
            else
            {
                var bank = session.Bank("Marvel/Content/WwiseAudio/English(US)/bnk_vo_1024001.bnk").Read();
                media = BnkBuilder.ReadDidx(BnkBuilder.ReadSections(bank).Single(x => x.Tag == "DIDX").Body).First().Id;
            }
            Directory.CreateDirectory(dir);
            var staged = Path.Combine(dir, $"{media}-relink{Path.GetExtension(audio)}");
            File.Copy(audio, staged, true);
            main.StageSync([staged]);
            var project = Path.Combine(dir, "relink.xzw");
            Wait(main.SaveProjectForShot(project));

            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(project))!;
            var r = json["Replacements"]![0]!;
            Console.WriteLine($"saved: media {r["MediaId"]}, anchored to bank {r["Bank"]}, Sound {r["Sound"]}");
            const uint oldId = 123456789;
            r["MediaId"] = oldId;
            File.WriteAllText(project, json.ToJsonString());
            Console.WriteLine($"rewritten as before a patch: media {oldId}");

            Wait(main.OpenProjectForShot(project));
            var now = main.StagedFiles.Select(x => x.MediaId).ToList();
            Console.WriteLine($"after opening: staged on {string.Join(", ", now)} (expected {media}); status: {main.Status.Text}");
            Console.WriteLine(now.SequenceEqual([media]) ? "RELINK OK" : "RELINK FAILED");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the relink test: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// XzoundWave --shot-ui &lt;dir&gt;: the window as the app opens it -- loading the game by itself in
    /// the saved language once it has loaded before -- a column dragged wider by its header's edge,
    /// and every tab drawn at the usual size and at the window's smallest, into dir.
    /// </summary>
    public static int TakeUi(string dir)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();
            Directory.CreateDirectory(dir);
            void Pump(int ms = 0)
            {
                var until = DateTime.UtcNow.AddMilliseconds(ms);
                do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); } while (DateTime.UtcNow < until);
            }
            void Shoot(MainWindow w, string name)
            {
                Pump();
                for (var i = 0; i < 6; i++) { Thread.Sleep(60); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
                var frame = w.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
                using var file = File.Create(Path.Combine(dir, name + ".png"));
                frame.Save(file);
                Console.WriteLine($"  {name}.png  {w.Width:0}x{w.Height:0}");
            }

            // Opened the way the app opens it, on settings that have loaded before.
            var main = new MainWindow { Width = 1440, Height = 840, AutoLoadOnOpen = true };
            main.Config.LoadedOnce = true;
            var language = main.Config.Language;
            var t0 = DateTime.UtcNow;
            main.Show();
            while (main.CurrentSession.Provider is null || !main.CurrentSession.BankIndexReady)
            {
                Pump(200);
                if ((DateTime.UtcNow - t0).TotalSeconds > 300) { Console.WriteLine("AUTOLOAD: did not finish in 5 minutes"); break; }
            }
            Pump(500);
            Console.WriteLine($"AUTOLOAD: loaded by itself in {(DateTime.UtcNow - t0).TotalSeconds:N0}s, language {main.CurrentSession.Language} " +
                              $"(saved: {language}); {main.CurrentSession.Characters.Count} characters; status: {main.Status.Text}");

            // A column header's right edge, dragged 120 px to the right.
            var grid = main.FindControl<DataGrid>("Grid_");
            main.ShowSkinSync("1029001", "vo");
            Pump(300);
            var header = main.GetVisualDescendants().OfType<DataGridColumnHeader>()
                             .FirstOrDefault(h => h.Content?.ToString() == "Event");
            var column = grid.Columns.First(c => c.Header?.ToString() == "Event");
            var before = column.ActualWidth;
            if (header is not null)
            {
                var edge = header.TranslatePoint(new Point(header.Bounds.Width - 1, header.Bounds.Height / 2), main) ?? default;
                main.MouseMove(edge);
                main.MouseDown(edge, MouseButton.Left);
                for (var dx = 10; dx <= 120; dx += 10) { main.MouseMove(edge + new Point(dx, 0)); Dispatcher.UIThread.RunJobs(); }
                main.MouseUp(edge + new Point(120, 0), MouseButton.Left);
                Pump(200);
            }
            Console.WriteLine($"COLUMNS: resizing on: {grid.CanUserResizeColumns}; Event column {before:0} px -> {column.ActualWidth:0} px after a 120 px drag of its edge");

            // Every tab, at the usual size and at the smallest the window allows.
            foreach (var (w, h) in new[] { (1440, 840), (1100, 600) })
            {
                main.Width = w; main.Height = h;
                Pump(200);
                foreach (var tab in new[] { "Browse", "Test", "Build", "Mods", "Setup" })
                {
                    main.SelectTabForShot(tab);
                    Shoot(main, $"{tab.ToLowerInvariant()}-{w}");
                }
            }
            main.SelectTabForShot("Browse");
            main.Width = 1440; main.Height = 840;
            main.ShowSkinSync("bnk_amb_asgarde01");
            main.PaneShow(details: true);
            Pump(300);
            Shoot(main, "asgarde-details");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the UI check: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// XzoundWave --shot-paklist &lt;out&gt; &lt;0-PAK-NAME-LIST.txt&gt; &lt;audio&gt; &lt;mediaId&gt;...: one file staged on each
    /// media id, Build mod with the list, and what each mod it wrote holds.
    /// </summary>
    public static int TakePakList(string outDir, string list, string audio, IReadOnlyList<string> ids)
    {
        try
        {
            AppBuilder.Configure<App>().UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            Dispatcher.UIThread.RunJobs();
            if (!main.CurrentSession.BankIndexReady) main.CurrentSession.IndexBanks(_ => { });
            var stage = Path.Combine(outDir + "-staged");
            Directory.CreateDirectory(stage);
            var staged = ids.Select(id => { var f = Path.Combine(stage, $"{id}-test{Path.GetExtension(audio)}"); File.Copy(audio, f, true); return f; }).ToList();
            main.StageSync(staged);
            main.PakNameListForShot = list;
            var run = main.BuildForShot(outDir);
            while (!run.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine("BUILD: " + main.Status.Text);
            var settings = Settings.Load();
            settings.AutoDetect();
            foreach (var pak in Directory.GetFiles(outDir, "*.pak", SearchOption.AllDirectories).OrderBy(p => p))
            {
                var o = ModBank.Open(pak, settings, withMedia: true);
                Console.WriteLine($"  {Path.GetRelativePath(outDir, pak)}: " +
                                  string.Join(", ", o.Banks.Select(b => b.Name).Concat(o.Media.Select(m => Path.GetFileName(m.MountedAt)))));
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the pak-list test: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// XzoundWave --shot-testbuild &lt;out&gt;: the Test tab on a map's ambience bank, where some sounds live in the
    /// bank and some only in loose files -- Test the selection, Test only the selection and Silent .bnk on one
    /// of each, and what each .pak holds.
    /// </summary>
    public static int TakeTestBuild(string outDir)
    {
        try
        {
            AppBuilder.Configure<App>().UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            Dispatcher.UIThread.RunJobs();
            var session = main.CurrentSession;
            if (!session.BankIndexReady) session.IndexBanks(_ => { });
            void Pump(Task t) { while (!t.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); } Dispatcher.UIThread.RunJobs(); }
            var settings = Settings.Load(); settings.AutoDetect();
            void Show(string dir)
            {
                foreach (var pak in Directory.GetFiles(dir, "*.pak"))
                {
                    var o = ModBank.Open(pak, settings, withMedia: true);
                    Console.WriteLine($"  {Path.GetFileName(pak)}: banks {string.Join(", ", o.Banks.Select(b => b.Name))}; loose " +
                                      string.Join(", ", o.Media.Select(m => $"{Path.GetFileName(m.MountedAt)} {m.Bytes.Length:N0} B")));
                }
                Console.WriteLine("  beside it: " + string.Join(", ", Directory.GetFiles(dir).Where(f => !f.EndsWith(".pak")).Select(Path.GetFileName)));
            }

            main.ShowSkinSync("bnk_amb_asgarde01");
            Dispatcher.UIThread.RunJobs();
            var rows = main.SoundsForShot.Rows;
            var loose = rows.First(r => !session.MediaBank.ContainsKey(r.MediaId) && session.LooseMedia(r.MediaId) is not null).MediaId;
            var inBank = rows.First(r => session.MediaBank.ContainsKey(r.MediaId)).MediaId;
            Console.WriteLine($"rows {rows.Count}; loose-only {loose}, in a bank {inBank}");
            main.TestClipDirForShot = Path.Combine(outDir, "clips");

            foreach (var (name, run) in new (string, Func<Task>)[]
                     { ("selection", main.TestSelectionForShot), ("only-selection", main.TestOnlySelectionForShot), ("silent", main.TestSilentForShot) })
            {
                var dir = Path.Combine(outDir, name);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                main.BuildFolderForShot = dir;
                Console.WriteLine($"{name.ToUpperInvariant()}: selected {main.SelectMediaForShot(loose, inBank)}");
                Pump(run());
                Console.WriteLine("  status: " + main.Status.Text);
                if (Directory.Exists(dir)) Show(dir);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the test-build check: " + ex);
            return 1;
        }
    }

    /// <summary>XzoundWave --shot-coldrag: what is under a column header's edge, and what a drag there does.</summary>
    public static int TakeColumnDrag()
    {
        AppBuilder.Configure<App>().UseSkia()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var main = new MainWindow { Width = 1440, Height = 840 };
        main.Show();
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        var grid = main.FindControl<DataGrid>("Grid_");
        Console.WriteLine($"grid resize: {grid.CanUserResizeColumns}");
        var headers = main.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.IsVisible).ToList();
        foreach (var h in headers)
        {
            var at = h.TranslatePoint(new Point(0, 0), main) ?? default;
            Console.WriteLine($"  header '{h.Content}' at x={at.X:0} w={h.Bounds.Width:0}");
        }
        var ev = headers.First(h => h.Content?.ToString() == "Event");
        var col = grid.Columns.First(c => c.Header?.ToString() == "Event");
        void Drag(int dx, string what)
        {
            for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            var h = main.GetVisualDescendants().OfType<DataGridColumnHeader>().First(x => x.IsVisible && x.Content?.ToString() == "Event");
            var e0 = h.TranslatePoint(new Point(h.Bounds.Width - 2, h.Bounds.Height / 2), main) ?? default;
            var w0 = col.ActualWidth;
            main.MouseMove(e0); Dispatcher.UIThread.RunJobs();
            main.MouseDown(e0, MouseButton.Left);
            var step = Math.Sign(dx) * 5;
            for (var d = step; Math.Abs(d) <= Math.Abs(dx); d += step) { main.MouseMove(e0 + new Point(d, 0), RawInputModifiers.LeftMouseButton); Dispatcher.UIThread.RunJobs(); }
            main.MouseUp(e0 + new Point(dx, 0), MouseButton.Left);
            for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Console.WriteLine($"{what}: Event {w0:0} -> {col.ActualWidth:0}; Category {grid.Columns.First(c => c.Header?.ToString() == "Category").ActualWidth:0}; Subtitle {grid.Columns.First(c => c.Header?.ToString() == "Subtitle / line").ActualWidth:0}");
        }
        Drag(-60, "shrink 60 at 1440");
        main.Width = 2400; Drag(+80, "grow 80 at 2400");
        main.PaneShow(details: false);
        foreach (var inset in new[] { 1.0 })
        {
            var edge = ev.TranslatePoint(new Point(ev.Bounds.Width - inset, ev.Bounds.Height / 2), main) ?? default;
            var hit = main.InputHitTest(edge);
            var before = col.ActualWidth;
            main.MouseMove(edge);
            Dispatcher.UIThread.RunJobs();
            var cursor = (hit as Control)?.Cursor ?? ev.Cursor;
            main.MouseDown(edge, MouseButton.Left);
            for (var dx = 5; dx <= 80; dx += 5) { main.MouseMove(edge + new Point(dx, 0), RawInputModifiers.LeftMouseButton); Dispatcher.UIThread.RunJobs(); }
            main.MouseUp(edge + new Point(80, 0), MouseButton.Left);
            for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Console.WriteLine($"inset {inset}: hit {hit?.GetType().Name}, cursor {cursor}, width {before:0} -> {col.ActualWidth:0} ({col.Width})");
        }
        return 0;
    }

    /// <summary>XzoundWave --shot-recompress &lt;out&gt; &lt;input&gt;...: "Recompress…" through its own path.</summary>
    public static int TakeRecompress(string outDir, IReadOnlyList<string> inputs)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            Dispatcher.UIThread.RunJobs();
            var run = main.RecompressForShot(inputs, outDir);
            while (!run.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine("RECOMPRESS: " + main.Status.Text);
            // Into the folder an input sits in: refused, nothing written over it.
            var again = main.RecompressForShot([inputs[0]], Path.GetDirectoryName(Path.GetFullPath(inputs[0]))!);
            while (!again.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine("SAME FOLDER: " + main.Status.Text);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the recompress test: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// XzoundWave --shot-mediamod &lt;out&gt;: two mods of loose Media wems only, made from the game's
    /// own loose-only files (ambience streams and music tracks, an id nothing plays, a file the
    /// game's own bytes), through open, the rows, Play's bytes, Extract, Merge, a volume build,
    /// Update, Close and a mute build. Nothing is played aloud.
    /// </summary>
    public static int TakeMediaMod(string outDir)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            Dispatcher.UIThread.RunJobs();
            var session = main.CurrentSession;
            if (!session.BankIndexReady) session.IndexBanks(_ => { });

            void W(string line) => Console.WriteLine(line);
            void Pump(Task t)
            {
                while (!t.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
                Dispatcher.UIThread.RunJobs();
                if (t.IsFaulted) throw t.Exception!;
            }
            bool LooseOnly(uint m) => !session.MediaBank.ContainsKey(m) && session.LooseMedia(m) is not null;
            var settings = Settings.Load();
            settings.AutoDetect();
            ModBank.Opened Pak(string path) => ModBank.Open(path, settings, withMedia: true);

            // The game's own loose-only files: three an ambience bank streams, two a music bank's tracks place.
            const string amb = "bnk_amb_asgarde01";
            var ambPath = session.Provider.Files.Keys.First(k => k.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileNameWithoutExtension(k).Equals(amb, StringComparison.OrdinalIgnoreCase));
            var shape = ModUpdate.Read(session.Provider.Files[ambPath].Read());
            var ambLoose = shape.SoundSource.Where(kv => shape.SoundBodies[kv.Key].PlaysMedia).Select(kv => kv.Value)
                                .Distinct().Where(LooseOnly).OrderBy(x => x).Take(3).ToList();
            uint m1 = 0, m2 = 0;
            string musicSkin = null;
            foreach (var bank in session.OtherBanks.SelectMany(g => g.Skins).SelectMany(sk => sk.Banks.Select(b => (sk, b))))
            {
                if (m2 != 0) break;
                if (!Path.GetFileName(bank.b.Path).Contains("music", StringComparison.OrdinalIgnoreCase)) continue;
                var ids = ModUpdate.Lines.Of(bank.b.Read(), bank.b.Path).TrackMedia.Values.SelectMany(x => x)
                                   .Distinct().Where(LooseOnly).OrderBy(x => x).Take(2).ToList();
                if (ids.Count == 2) { m1 = ids[0]; m2 = ids[1]; musicSkin = bank.sk.SkinId; }
            }
            if (ambLoose.Count < 3 || m2 == 0) { W("could not find the loose-only files to test with"); return 1; }
            uint x1 = ambLoose[0], x2 = ambLoose[1], x3 = ambLoose[2];
            const uint unplayed = 4000000123;
            byte[] G(uint id) => session.LooseMedia(id).Read();
            string P(uint id) => session.LooseMedia(id).Path;
            W($"ids: ambience {x1} {x2} {x3} ({amb}), music {m1} {m2} ({musicSkin}), unplayed {unplayed}");

            // Mod A: x1 and x2 swapped, x3 the game's own bytes, m1 given m2's, an id nothing plays.
            // Mod B: x1 again (a collision), m2 given x1's.
            string Make(string name, params (string Path, byte[] Bytes)[] files)
            {
                var root = Path.Combine(outDir, name + "-src");
                if (Directory.Exists(root)) Directory.Delete(root, true);
                foreach (var (rel, bytes) in files)
                {
                    var dest = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, bytes);
                }
                var outBase = Path.Combine(outDir, name + "_9999999_P");
                ModPackager.PackagePak(root, files.Select(f => f.Path).ToList(), outBase);
                return outBase + ".pak";
            }
            // ...and x2's bytes under x3's id in a bucket folder that is not x3's, which the game never reads.
            var modA = Make("MediaA", (P(x1), G(x2)), (P(x2), G(x1)), (P(x3), G(x3)), (P(m1), G(m2)),
                            (MediaLayout.PathFor(unplayed), G(x1)), (MediaLayout.Root + $"99/{x3}.wem", G(x2)));
            var modB = Make("MediaB", (P(x1), G(m1)), (P(m2), G(x1)));

            // 1. open
            Pump(main.OpenModForShot(modA));
            W("OPEN: " + main.Status.Text);
            foreach (var l in main.ModsForShot.LooseFiles.OrderBy(l => l.Id))
                W($"  loose {l.Id} at {l.MountedAt[MediaLayout.Root.Length..]}: tag '{l.Tag}', reaches {l.Reaches}, plays in {l.Stem ?? "(nothing)"}");
            var rows = main.SoundsForShot?.Rows ?? [];
            W($"  rows on screen {rows.Count}: MODDED {rows.Count(r => r.ModTag == "MODDED")}, NEW {rows.Count(r => r.ModTag == "NEW")}, " +
              $"untouched '' {rows.Count(r => r.ModTag == "")}, untagged {rows.Count(r => r.ModTag is null)}");
            foreach (var id in new[] { x1, x2, x3, m1 })
                W($"  {id}: " + (rows.FirstOrDefault(r => r.MediaId == id) is { } r ? $"row in {r.Bank}, tag '{r.ModTag}'" : "NO ROW ON SCREEN"));

            // 2. Play's bytes, the mod's and the game's
            var mine = main.PlayBytesForShot(x1, original: false);
            var stock = main.PlayBytesForShot(x1, original: true);
            W($"PLAY {x1}: mod bytes are the mod's file {(mine is not null && mine.AsSpan().SequenceEqual(G(x2)) ? "YES" : "NO")}, " +
              $"original is the game's {(stock is not null && stock.AsSpan().SequenceEqual(G(x1)) ? "YES" : "NO")}");

            // 3. extract (opens A again, which takes its own files' place)
            var xdir = Path.Combine(outDir, "extract");
            if (Directory.Exists(xdir)) Directory.Delete(xdir, true);
            Pump(main.ExtractForShot(modA, xdir));
            W("EXTRACT: " + main.Status.Text);
            foreach (var f in Directory.Exists(xdir) ? Directory.GetFiles(xdir, "*", SearchOption.AllDirectories) : [])
                W($"  {Path.GetRelativePath(xdir, f)}  {new FileInfo(f).Length:N0} B");

            // 4. merge
            var mdir = Path.Combine(outDir, "merge");
            if (Directory.Exists(mdir)) Directory.Delete(mdir, true);
            Pump(main.MergeForShot(modA, modB, mdir));
            W("MERGE: " + main.Status.Text);
            foreach (var pak in Directory.Exists(mdir) ? Directory.GetFiles(mdir, "*.pak") : [])
                foreach (var m in Pak(pak).Media)
                {
                    var id = ModUpdate.MediaIdOf(m.MountedAt) ?? 0;
                    var who = m.Bytes.AsSpan().SequenceEqual(G(x2)) ? "A's x1 (priority)" : m.Bytes.AsSpan().SequenceEqual(G(m1)) ? "B's x1"
                            : id == m2 && m.Bytes.AsSpan().SequenceEqual(G(x1)) ? "B's m2" : "";
                    W($"  {m.MountedAt}  {m.Bytes.Length:N0} B  {who}");
                }
            if (File.Exists(Path.Combine(mdir, "build-log.txt")))
                foreach (var line in File.ReadLines(Path.Combine(mdir, "build-log.txt")).Take(8)) W("  log: " + line);
            if (File.Exists(Path.Combine(mdir, "merge-collisions.csv")))
                foreach (var line in File.ReadAllLines(Path.Combine(mdir, "merge-collisions.csv"))) W("  csv: " + line);

            // 5. volume: x1 and x3 at 0.5 by hand, bulk 1.0, with mod A loaded (x1's source is the mod's file)
            main.ShowSkinSync(amb);
            Dispatcher.UIThread.RunJobs();
            foreach (var r in main.SoundsForShot.Rows.Where(r => r.MediaId == x1 || r.MediaId == x3)) { r.Volume = 0.5; r.VolumeByHand = true; }
            var vdir = Path.Combine(outDir, "volume");
            if (Directory.Exists(vdir)) Directory.Delete(vdir, true);
            Pump(main.BuildVolumesForShot(vdir, "1.0"));
            W("VOLUME: " + main.Status.Text);
            foreach (var pak in Directory.Exists(vdir) ? Directory.GetFiles(vdir, "*.pak") : [])
            {
                var opened = Pak(pak);
                W($"  {Path.GetFileName(pak)}: {opened.Banks.Count} bank(s), {opened.Media.Count} loose");
                foreach (var m in opened.Media)
                {
                    var id = ModUpdate.MediaIdOf(m.MountedAt) ?? 0;
                    W($"  {m.MountedAt}  {m.Bytes.Length:N0} B, {BnkBuilder.Seconds(m.Bytes):0.00}s " +
                      (id == x1 ? $"(mod's file {BnkBuilder.Seconds(G(x2)):0.00}s, game's {BnkBuilder.Seconds(G(x1)):0.00}s)" : $"(game's {BnkBuilder.Seconds(G(id)):0.00}s)"));
                }
            }
            foreach (var r in main.SoundsForShot.Rows) { r.Volume = null; r.VolumeByHand = false; }

            // 6. update: a mod of loose files only names no Sound
            var udir = Path.Combine(outDir, "update");
            if (Directory.Exists(udir)) Directory.Delete(udir, true);
            Pump(main.UpdateForShot(modA, udir));
            W("UPDATE: " + main.Status.Text);
            var report = Path.Combine(udir, ModUpdate.ReportName);
            if (File.Exists(report))
                foreach (var line in File.ReadLines(report).Where(l => l.Contains("ships no .bnk") || l.Contains("NOT CARRIED")).Take(3))
                    W("  report: " + line.Trim()[..Math.Min(200, line.Trim().Length)]);

            // 7. close: the game's rows go back to no tag
            Pump(main.CloseModsForShot());
            main.ShowSkinSync(amb);
            Dispatcher.UIThread.RunJobs();
            W($"CLOSE: {main.ModsForShot.LooseFiles.Count} loose loaded, rows with a tag {main.SoundsForShot.Rows.Count(r => r.ModTag is not null)}");

            // 8. a mute on a sound no bank holds, built
            foreach (var r in main.SoundsForShot.Rows.Where(r => r.MediaId == x3)) r.Muted = true;
            var bdir = Path.Combine(outDir, "mute");
            if (Directory.Exists(bdir)) Directory.Delete(bdir, true);
            Pump(main.BuildForShot(bdir));
            W("MUTE BUILD: " + main.Status.Text);
            foreach (var pak in Directory.Exists(bdir) ? Directory.GetFiles(bdir, "*.pak") : [])
                foreach (var m in Pak(pak).Media)
                    W($"  {m.MountedAt}  {m.Bytes.Length:N0} B (game's {G(x3).Length:N0} B), {BnkBuilder.Seconds(m.Bytes):0.00}s");
            foreach (var r in main.SoundsForShot.Rows) r.Muted = false;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the media-mod test: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// XzoundWave --shot-missing &lt;audio&gt; &lt;out&gt;: the loose-wem cases in one map ambience bank --
    /// a wem only loose, and a streamed sound whose file the game does not ship -- as rows, what
    /// Play says for the second and for a Silence row, and a build with audio staged on it.
    /// </summary>
    public static int TakeMissing(string audio, string outDir)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            Dispatcher.UIThread.RunJobs();
            var session = main.CurrentSession;
            if (!session.BankIndexReady) session.IndexBanks(_ => { });

            const string stem = "bnk_amb_asgarde01";
            var path = session.Provider.Files.Keys.First(k => Path.GetFileNameWithoutExtension(k).Equals(stem, StringComparison.OrdinalIgnoreCase));
            var shape = ModUpdate.Read(session.Provider.Files[path].Read());
            var played = shape.SoundSource.Where(kv => shape.SoundBodies[kv.Key].PlaysMedia).Select(kv => kv.Value).Distinct().ToList();
            var looseOnly = played.Where(m => !session.MediaBank.ContainsKey(m) && session.LooseMedia(m) is not null).ToList();
            var missing = played.Where(m => !session.MediaBank.ContainsKey(m) && session.LooseMedia(m) is null).ToList();
            Console.WriteLine($"{stem}: {played.Count} media played, {looseOnly.Count} only loose, {missing.Count} not shipped");

            main.ShowSkinSync(stem);
            Dispatcher.UIThread.RunJobs();
            var rows = main.SoundsForShot?.Rows ?? [];
            Console.WriteLine($"rows: {rows.Count}; only-loose with a row: {looseOnly.Count(m => rows.Any(r => r.MediaId == m))}/{looseOnly.Count}; " +
                              $"not-shipped with a row: {missing.Count(m => rows.Any(r => r.MediaId == m))}/{missing.Count}");
            if (missing.Count > 0 && main.PlayMediaSync(missing[0])) Console.WriteLine("play not-shipped: " + main.Status.Text);

            main.ShowSkinSync("1056001", "vo");
            Dispatcher.UIThread.RunJobs();
            var silence = main.SoundsForShot?.Rows.FirstOrDefault(SoundSources.IsGenerator);
            if (silence is not null && main.PlayMediaSync(silence.MediaId)) Console.WriteLine($"play generator ({silence.Codec}): " + main.Status.Text);

            if (missing.Count > 0)
            {
                Directory.CreateDirectory(outDir + "-staged");
                var staged = Path.Combine(outDir + "-staged", $"{missing[0]}-new-ambience{Path.GetExtension(audio)}");
                File.Copy(audio, staged, true);
                main.StageSync([staged]);
                Dispatcher.UIThread.RunJobs();
                var run = main.BuildForShot(outDir);
                while (!run.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
                Dispatcher.UIThread.RunJobs();
                Console.WriteLine($"build onto {missing[0]}: " + main.Status.Text);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the loose-wem test: " + ex);
            return 1;
        }
    }

    /// <summary>XzoundWave --shot-update &lt;mod&gt; &lt;folder&gt;: "Update mod…" through its own path.</summary>
    public static int TakeUpdate(string mod, string folder)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            Dispatcher.UIThread.RunJobs();
            var run = main.UpdateForShot(mod, folder);
            while (!run.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine("status: " + main.Status.Text);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the update: " + ex);
            return 1;
        }
    }

    /// <summary>XzoundWave --shot-extract &lt;mod&gt; &lt;folder&gt;: a mod opened, then "Extract modded wems…".</summary>
    public static int TakeExtract(string mod, string folder)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            Dispatcher.UIThread.RunJobs();
            var run = main.ExtractForShot(mod, folder);
            while (!run.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine("status: " + main.Status.Text);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the extract: " + ex);
            return 1;
        }
    }

    /// <summary>XzoundWave --shot-merge &lt;priority&gt; &lt;secondary&gt; &lt;folder&gt;: "Merge mods…" through its own path.</summary>
    public static int TakeMerge(string priority, string secondary, string folder)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            Dispatcher.UIThread.RunJobs();
            var merge = main.MergeForShot(priority, secondary, folder);
            while (!merge.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine("status: " + main.Status.Text);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the merge: " + ex);
            return 1;
        }
    }

    public static int Take(string path, int width = 1440, int height = 840,
                           bool withGame = false, string skin = "1029001",
                           IReadOnlyList<string> stage = null, string panel = null,
                           int select = 0, uint media = 0, string bank = "vo",
                           bool expand = false, int click = 0, string tab = null, string mod = null)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();

            var window = new MainWindow { Width = width, Height = height };
            window.Show();

            // Real rows, not a sketch. A layout that only looks right with placeholder
            // text is a layout that has not been checked: the widths that matter are
            // the ones a 40-character event name and a Chinese category actually take.
            if (withGame)
            {
                // Synchronous on purpose: this thread is the UI thread and is about to
                // block on the result, so anything awaited would queue behind the wait.
                window.LoadGameSync();
                Dispatcher.UIThread.RunJobs();

                if (mod is not null)
                {
                    // A real mod, opened the way the button opens one, instead of the mockup's counts.
                    var opening = window.OpenModForShot(mod);
                    while (!opening.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
                    Dispatcher.UIThread.RunJobs();
                    Console.WriteLine("mod: " + window.Status.Text);
                }
                else
                    // What a loaded mod looks like, using the counts from the pak the
                    // mockup was drawn with, so the two can be put side by side.
                    window.PretendModded(new Dictionary<string, int>
                    {
                        ["bnk_vo_1029001"] = 279, ["bnk_vo_1030001"] = 29,
                        ["bnk_vo_1024001"] = 73,  ["bnk_vo_1034001"] = 247,
                        ["bnk_vo_1016001"] = 62,  ["bnk_vo_1050001"] = 53,
                    }, expandModded: true);
                window.ShowSkinSync(skin, bank);
            }

            // After the skin, because "Replaces" reads the open rows and would otherwise
            // be rendered blank for no better reason than the order two lines were in.
            if (stage is { Count: > 0 })
            {
                Console.WriteLine($"staged: {window.StageSync(stage)} file(s)");
                Dispatcher.UIThread.RunJobs();
            }

            // After the rows and after the staging, because both of these are about what the
            // right panel has to draw and neither can be done before there is something to
            // draw it from.
            if (media > 0 || select > 0)
                Console.WriteLine($"selected: {window.SelectSync(select, media)} row(s)");
            if (tab is not null) window.SelectTabForShot(tab);
            if (panel is not null)
                window.PaneShow(details: panel.StartsWith("d", StringComparison.OrdinalIgnoreCase));
            Dispatcher.UIThread.RunJobs();

            // After the panel is showing, because it draws nothing at all while it is hidden
            // and there would be no expander to open.
            if (expand)
                Console.WriteLine($"opened: {window.ExpandDetailsSync()} expander(s)");
            Dispatcher.UIThread.RunJobs();

            // Last of all, because a box can only be pressed once it has been drawn, and what
            // is being rendered is the panel AFTER the press.
            if (click > 0)
            {
                Console.WriteLine($"clicked box {click} of {window.ClickBoxSync(click)}");
                Dispatcher.UIThread.RunJobs();
            }

            // Let layout settle before asking for the pixels: a frame captured during
            // the first pass shows controls at their unmeasured size, which reads as a
            // broken layout when nothing is wrong.
            Dispatcher.UIThread.RunJobs();

            // Half a second of animation: headless renders a frame only when its timer is
            // ticked, and an animation's clock runs on real time, so the two have to be
            // interleaved -- otherwise the Setup tab's pulse is always caught at its first frame.
            for (var i = 0; i < 10; i++)
            {
                Thread.Sleep(90);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }

            var frame = window.CaptureRenderedFrame()
                        ?? throw new InvalidOperationException("no frame was rendered");

            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var file = File.Create(full)) frame.Save(file);

            // The size that was drawn rather than the size that was asked for. A window is
            // clamped to its own MinWidth, so a render asked for narrower than that is a
            // render of the minimum, and printing the request would name a frame nobody saw.
            Console.WriteLine($"{window.Bounds.Width:0}x{window.Bounds.Height:0} -> {full}" +
                              (Math.Abs(window.Bounds.Width - width) > 0.5 ||
                               Math.Abs(window.Bounds.Height - height) > 0.5
                               ? $"   (asked for {width}x{height}; the window's own minimum " +
                                 $"is {window.MinWidth:0}x{window.MinHeight:0})" : ""));

            // What the picture cannot be squinted at for: whether a column is one pixel short
            // of its own header. Printed beside every render because the widths that clip are
            // the ones nobody set out to look at, and the same figures are what --uitest
            // asserts on.
            foreach (var fit in window.ColumnFits().Concat(window.PendingColumnFits()))
                Console.WriteLine($"  {(fit.Fits ? "fits " : "CLIPS")}  {fit}");

            // And the other thing a picture cannot be squinted at for: how much vertical space
            // the details panel's prose takes before the first box. A shorter panel is a claim
            // about pixels, so it is printed as pixels beside the render that shows it.
            Console.WriteLine("  " + window.DetailsShape());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not render the window: " + ex);
            return 1;
        }
    }
}
