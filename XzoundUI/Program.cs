using Avalonia;

namespace MRAudioKit.UI;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // This is a WinExe, so Windows hands it no console and every verb below would
        // print into nothing. The WPF head has always done this and this one never did,
        // which is why --doctor from here reported through the no-console path even when
        // it was run from a shell with a perfectly good stdout. Only when there is a
        // switch on the line, because opening the window from Explorer has no caller's
        // console to attach to and no output to place there either.
        if (args.Any(a => a.StartsWith("--", StringComparison.Ordinal)))
            ConsoleBridge.Attach();

        // Where the diagnostics go when there is no console at all, which is the
        // double-click case. A report saved to a file nobody is told about is a report
        // nobody reads, so it is opened with whatever the desktop uses for text. A
        // dialog is the WPF head's answer and would be the wrong shape here anyway:
        // this text is forty lines, not a sentence.
        if (!ConsoleBridge.HasConsole) EnvCheck.Tell = ShowAsFile;

        // The verbs come first and never need a window, which is the whole point of
        // the split -- the same table runs from the command-line host.
        if (args.Length > 0 && Verbs.Dispatch(args, out var exit)) return exit;

        // Render the window to a file instead of a screen. This is how the layout gets
        // looked at on a machine with no display, and how a test can assert about it.
        if (args.Any(a => a.Equals("--uitest", StringComparison.OrdinalIgnoreCase)))
            return UiSelfTest.Run();

        if (args.Any(a => a.Equals("--shot-coldrag", StringComparison.OrdinalIgnoreCase))) return Shot.TakeColumnDrag();

        var ui = Array.FindIndex(args, a => a.Equals("--shot-ui", StringComparison.OrdinalIgnoreCase));
        if (ui >= 0 && args.Length > ui + 1)
            return Shot.TakeUi(args[ui + 1]);

        var rc = Array.FindIndex(args, a => a.Equals("--shot-recompress", StringComparison.OrdinalIgnoreCase));
        if (rc >= 0 && args.Length > rc + 2)
            return Shot.TakeRecompress(args[rc + 1], args.Skip(rc + 2).ToList());

        var mm = Array.FindIndex(args, a => a.Equals("--shot-mediamod", StringComparison.OrdinalIgnoreCase));
        if (mm >= 0 && args.Length > mm + 1)
            return Shot.TakeMediaMod(args[mm + 1]);

        var ms = Array.FindIndex(args, a => a.Equals("--shot-missing", StringComparison.OrdinalIgnoreCase));
        if (ms >= 0 && args.Length > ms + 2)
            return Shot.TakeMissing(args[ms + 1], args[ms + 2]);

        var up = Array.FindIndex(args, a => a.Equals("--shot-update", StringComparison.OrdinalIgnoreCase));
        if (up >= 0 && args.Length > up + 2)
            return Shot.TakeUpdate(args[up + 1], args[up + 2]);

        var rl = Array.FindIndex(args, a => a.Equals("--shot-relink", StringComparison.OrdinalIgnoreCase));
        if (rl >= 0 && args.Length > rl + 2)
            return Shot.TakeRelink(args[rl + 1], args[rl + 2],
                                   loose: args.Any(a => a.Equals("--loose", StringComparison.OrdinalIgnoreCase)));

        var sp = Array.FindIndex(args, a => a.Equals("--shot-split", StringComparison.OrdinalIgnoreCase));
        if (sp >= 0 && args.Length > sp + 3)
            return Shot.TakeSplit(args[sp + 1], args[sp + 2], args[sp + 3]);

        var xt = Array.FindIndex(args, a => a.Equals("--shot-extract", StringComparison.OrdinalIgnoreCase));
        if (xt >= 0 && args.Length > xt + 2)
            return Shot.TakeExtract(args[xt + 1], args[xt + 2]);

        var mg = Array.FindIndex(args, a => a.Equals("--shot-merge", StringComparison.OrdinalIgnoreCase));
        if (mg >= 0 && args.Length > mg + 3)
            return Shot.TakeMerge(args[mg + 1], args[mg + 2], args[mg + 3]);

        var vol = Array.FindIndex(args, a => a.Equals("--shot-volumes", StringComparison.OrdinalIgnoreCase));
        if (vol >= 0 && args.Length > vol + 2)
            return Shot.TakeVolumes(args[vol + 1], args[vol + 2]);

        var vl = Array.FindIndex(args, a => a.Equals("--shot-voicelines", StringComparison.OrdinalIgnoreCase));
        if (vl >= 0)
            return Shot.TakeVoiceLines(args.Length > vl + 1 ? args[vl + 1] : "voicelines.png", buildAudio: After(args, "--then-build"));

        var shot = Array.FindIndex(args, a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));
        if (shot >= 0)
        {
            // The two sizes the verb has always documented and never read, so every render
            // anyone has taken was 1440x840 -- which is why a layout that overflows the
            // window's own minimum width was never looked at. Taken as the bare numbers
            // after the path, stopping at the first thing that is not one, so the switches
            // below can follow in any order.
            var size = args.Skip(shot + 2).TakeWhile(a => int.TryParse(a, out var n) && n > 0)
                           .Select(int.Parse).ToList();
            return Shot.Take(args.Length > shot + 1 ? args[shot + 1] : "window.png",
                             width: size.Count > 0 ? size[0] : 1440,
                             height: size.Count > 1 ? size[1] : 840,
                             withGame: args.Any(a => a.Equals("--with-game", StringComparison.OrdinalIgnoreCase)),
                             skin: Array.FindIndex(args, a => a.Equals("--skin", StringComparison.OrdinalIgnoreCase)) is var k
                                   && k >= 0 && k + 1 < args.Length ? args[k + 1] : "1029001",
                             // Everything up to the next switch, so one render can show
                             // the staged pane with a handful of files or a whole folder.
                             stage: Array.FindIndex(args, a => a.Equals("--stage", StringComparison.OrdinalIgnoreCase)) is var s
                                    && s >= 0 ? args.Skip(s + 1).TakeWhile(a => !a.StartsWith("--")).ToList() : null,
                             // The right panel's two sub-panels, and what the details one is
                             // given to describe. A switch nobody can click and a selection
                             // nobody can make are layouts nobody can look at.
                             panel: After(args, "--panel"),
                             select: int.TryParse(After(args, "--select"), out var many) ? many : 0,
                             // --pick and not --media: the verb table already owns that name
                             // and is dispatched from anywhere on the line, so a render asked
                             // for one media id ran the media verb instead and took no shot.
                             media: uint.TryParse(After(args, "--pick"), out var id) ? id : 0,
                             bank: After(args, "--bank") ?? "vo",
                             // The details panel's expanders, which are shut on purpose and
                             // hold the two lists that open longer than the panel is tall.
                             expand: args.Any(a => a.Equals("--open", StringComparison.OrdinalIgnoreCase)),
                             // One of the drawn boxes, pressed once the frame is set up. They
                             // are the only surface in this window a render could not reach,
                             // and what a press does to the panel AROUND them is the thing the
                             // panel's subject rule exists for.
                             click: int.TryParse(After(args, "--click"), out var box) ? box : 0,
                             // Which tab's toolbar to draw, and a real mod to open first.
                             tab: After(args, "--tab"),
                             mod: After(args, "--mod"));
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex) when (NoWindowingSystem(ex))
        {
            // A stack trace ending in "Aborted" is what this used to print, which tells
            // someone running the program on a server image nothing at all. Every verb
            // above this line already worked without a screen, so the useful thing to
            // say is that the toolkit is still there and only the window is not.
            Console.Error.WriteLine(
                "XzoundWave could not open a window: " + ex.Message + Environment.NewLine +
                Environment.NewLine +
                "This needs a desktop. Every Linux desktop already has what it wants, but a" +
                Environment.NewLine +
                "server or container image often does not; on Debian or Ubuntu the missing" +
                Environment.NewLine +
                "pieces are libx11-6, libice6 and libsm6." + Environment.NewLine +
                Environment.NewLine +
                "Nothing else is affected. Run with --help to see the verbs, which build," +
                Environment.NewLine +
                "convert and inspect banks with no screen at all.");
            return 3;
        }
    }

    /// <summary>
    /// Put a report in front of someone who has no console, by writing it out and asking
    /// the desktop to open it. UseShellExecute is what picks the reader, so this is
    /// Notepad on Windows and whatever xdg-open resolves to elsewhere; when there is no
    /// handler the text is still on disk and the path was still printed, so the worst
    /// case is the one that already applied.
    /// </summary>
    private static void ShowAsFile(string title, string body, bool fatal)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "XzoundWave-" + title.Replace(' ', '-') + ".txt");
            File.WriteAllText(path, title + Environment.NewLine + Environment.NewLine + body);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // Nowhere to write and nothing to open with. Falling back to the console is
            // not useless even here: a shell that redirects our output still receives it.
            Console.WriteLine(title);
            Console.WriteLine(body);
        }
    }

    /// <summary>
    /// The word after a switch, or null when it is absent or is the last thing on the line.
    /// The switches the render verb takes are all one value long and all optional, so they
    /// are read this way rather than by position.
    /// </summary>
    private static string After(string[] args, string flag)
    {
        var at = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>
    /// Whether the window failed because there is nowhere to put it, rather than because
    /// something in the program is broken. Matched on the message because the platform
    /// backends do not agree on a type: X11 raises a plain Exception saying XOpenDisplay
    /// failed, and a real fault must still come out as a crash with its stack intact
    /// rather than being dressed up as a missing desktop.
    /// </summary>
    private static bool NoWindowingSystem(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message.Contains("XOpenDisplay", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("display", StringComparison.OrdinalIgnoreCase)
                   && e.Message.Contains("fail", StringComparison.OrdinalIgnoreCase)
                || e is DllNotFoundException)
                return true;
        return false;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
