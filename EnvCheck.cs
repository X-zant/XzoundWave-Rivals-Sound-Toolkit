using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace MRAudioKit;

/// <summary>
/// What the machine has to provide, and a plain warning when it does not.
///
/// A word on the .NET runtime, because the obvious check is impossible: if the
/// runtime is absent, nothing in this file ever runs. Windows shows the launcher's
/// own "you must install .NET Desktop Runtime" dialog before a single line of managed
/// code executes. What is checkable from in here is everything *after* that — the
/// runtime being older than the app was built for, the wrong architecture, and Media
/// Foundation missing, which is real on Windows N editions and shows up as mp3 and m4a
/// imports failing with nothing obviously wrong.
/// </summary>
public static class EnvCheck
{
    public const int RequiredMajor = 10;
    public const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";
    public const string RuntimeName = ".NET Desktop Runtime 10 (x64)";

    public sealed record Problem(string Title, string Detail, bool Fatal);

    /// <summary>Everything wrong, worst first. Empty when the machine is fine.</summary>
    public static List<Problem> Check()
    {
        var problems = new List<Problem>();

        // Roll-forward can land us on a newer runtime, which is fine and intended.
        // Older than we were built for is not, and produces failures far from here.
        if (Environment.Version.Major < RequiredMajor)
            problems.Add(new Problem(
                $"This needs {RuntimeName}",
                $"It is running on .NET {Environment.Version}, which is older than the " +
                $"version XzoundWave was built for.\n\nInstall it from:\n{DownloadUrl}",
                Fatal: true));

        if (!Environment.Is64BitProcess)
            problems.Add(new Problem(
                "This is a 64-bit application",
                "It is running as 32-bit, which means the x86 runtime is installed " +
                $"instead of the x64 one.\n\nInstall the x64 build from:\n{DownloadUrl}",
                Fatal: true));

        // vgmstream is a native binary we unpack and run. Existing is not enough: a
        // missing Visual C++ runtime or an antivirus quarantine leaves the file sitting
        // there and playback silently doing nothing.
        var vgm = Bundled.EnsureVgmstream(out var unpackError);
        if (vgm is null)
            problems.Add(new Problem(
                "Audio playback is unavailable",
                "vgmstream could not be unpacked, so nothing can be played, measured or " +
                "volume-scaled.\n\n" + (unpackError ?? "unknown reason") +
                "\n\nSettings > Tools can point at your own copy of vgmstream-cli.exe.",
                Fatal: false));
        else if (AudioPreview.LaunchProblem(vgm) is { } why)
            problems.Add(new Problem(
                "Audio playback is unavailable",
                "vgmstream is unpacked but will not run on this machine, so nothing can " +
                "be played, measured or volume-scaled.\n\n" + why +
                "\n\nIt lives in:\n" + Bundled.ToolsDir,
                Fatal: false));

        if (AudioPreview.OutputProblem() is { } outErr)
            problems.Add(new Problem(
                "No audio output",
                "Nothing can be heard because Windows reports no playback device.\n\n" +
                outErr + "\n\nEverything else in the tool still works.",
                Fatal: false));

        if (!MediaFoundationWorks(out var mfError))
            problems.Add(new Problem(
                "Windows Media Foundation is not available",
                "mp3, m4a, aac and wma files cannot be imported without it. wav, ogg, " +
                "flac and wem still work, and so does everything else.\n\n" +
                "This usually means a Windows 'N' edition without the Media Feature " +
                "Pack.\n\n" + mfError,
                Fatal: false));

        return problems;
    }

    /// <summary>
    /// Ask Media Foundation to open something, rather than asking Windows whether it
    /// is installed. A feature that is present but broken fails the same way for us.
    /// </summary>
    private static bool MediaFoundationWorks(out string error)
    {
        error = null;
        try
        {
            NAudio.MediaFoundation.MediaFoundationApi.Startup();
            NAudio.MediaFoundation.MediaFoundationApi.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Show anything wrong, once, at startup. Returns false when the app should not
    /// carry on. A non-fatal problem is reported and then got out of the way.
    /// </summary>
    public static bool WarnIfNeeded()
    {
        var problems = Check();
        if (problems.Count == 0) return true;

        foreach (var p in problems.OrderByDescending(p => p.Fatal))
        {
            MessageBox.Show(
                p.Detail, p.Title, MessageBoxButton.OK,
                p.Fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
            if (p.Fatal) return false;
        }
        return true;
    }

    /// <summary>
    /// XzoundWave.exe --playtest &lt;mediaId&gt; — decode one sound and actually play it.
    ///
    /// The decode and the playback are separate failures with the same symptom, and a
    /// tester whose machine reported no problems at all still heard nothing. This runs
    /// the whole chain so the two can be told apart.
    /// </summary>
    public static int PlayTest(uint mediaId)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();

        W($"audio out     {AudioPreview.OutputSummary()}");
        var outProblem = AudioPreview.OutputProblem();
        if (outProblem is not null) { W("FAIL  " + outProblem); return 1; }

        var session = new GameSession();
        session.Mount(settings, _ => { });

        var file = session.LooseMedia(mediaId);
        if (file is null) { W($"FAIL  media {mediaId} is not a loose file in this build."); return 1; }

        using var preview = new AudioPreview { VgmstreamPath = settings.VgmstreamPath };
        var wav = preview.Decode(file.Read(), mediaId, out var decodeError);
        if (wav is null) { W("FAIL  decode: " + decodeError); return 1; }
        W($"decoded       {new FileInfo(wav).Length:N0} B -> {wav}");

        string failure = null;
        var finished = false;
        preview.PlaybackFailed += m => failure = m;
        preview.Finished += () => finished = true;

        var started = DateTime.UtcNow;
        preview.Play(wav);

        // Watch the player itself rather than waiting for Finished: that event is
        // posted to this thread's synchronization context, and this loop is what is
        // occupying it. Give it a moment to start, then wait for it to stop.
        System.Threading.Thread.Sleep(300);
        var everPlayed = preview.IsPlaying;
        while (preview.IsPlaying && failure is null && (DateTime.UtcNow - started).TotalSeconds < 30)
            System.Threading.Thread.Sleep(100);

        var elapsed = (DateTime.UtcNow - started).TotalSeconds;
        if (failure is not null) { W("FAIL  playback: " + failure); return 1; }
        if (!everPlayed) { W("FAIL  the player never started."); return 1; }
        if (preview.IsPlaying) { W($"FAIL  still playing after {elapsed:0.0}s."); return 1; }

        W($"played        started, ran {elapsed:0.0}s, stopped on its own");
        _ = finished;
        W("");
        W("ALL CHECKS PASSED");
        return 0;
    }

    /// <summary>Where the report is always saved, so it can be sent to someone.</summary>
    public static string ReportPath => Path.Combine(Settings.AppDataDir, "diagnostics.txt");

    /// <summary>
    /// The report as text. Built rather than printed straight out, because this is a
    /// windowed application: started from Explorer or cmd there is no console, and the
    /// person who most needs this is the one least able to read a pipe.
    /// </summary>
    public static string Report()
    {
        var sb = new System.Text.StringBuilder();
        void W(string s) => sb.AppendLine(s);

        W("XzoundWave " + (typeof(EnvCheck).Assembly.GetName().Version?.ToString() ?? "?"));
        W("");
        W($"  runtime       {RuntimeInformation.FrameworkDescription}");
        W($"  built for     .NET {RequiredMajor} (runs on newer)");
        W($"  process       {(Environment.Is64BitProcess ? "64-bit" : "32-BIT")}");
        W($"  os            {RuntimeInformation.OSDescription}");
        W($"  app data      {Settings.AppDataDir}");

        var vgmPath = Bundled.EnsureVgmstream(out var vgmError);
        W($"  vgmstream     {(vgmPath is not null ? vgmPath : "NOT AVAILABLE — " + vgmError)}");
        if (vgmPath is not null)
        {
            W($"                {(File.Exists(vgmPath) ? new FileInfo(vgmPath).Length.ToString("N0") + " B, unpacked from the exe" : "missing")}");
            // The question that matters is whether it RUNS, not whether it is there.
            var why = AudioPreview.LaunchProblem(vgmPath);
            W($"                {(why is null ? "runs: yes" : "RUNS: NO — " + why)}");
            if (why is null)
            {
                var dlls = Directory.Exists(Bundled.ToolsDir)
                    ? Directory.GetFiles(Bundled.ToolsDir, "*.dll").Length : 0;
                W($"                {dlls} codec dll(s) beside it");
            }
        }

        W($"  audio out     {AudioPreview.OutputSummary()}");
        W($"  licences      {(Bundled.Licence is not null && Bundled.Notices is not null ? "embedded" : "MISSING")}");

        W("");
        var problems = Check();
        if (problems.Count == 0) W("no problems found.");
        foreach (var p in problems)
        {
            W($"{(p.Fatal ? "BLOCKING" : "warning ")}  {p.Title}");
            foreach (var line in p.Detail.Split('\n')) W("          " + line.TrimEnd());
        }
        return sb.ToString();
    }

    /// <summary>
    /// XzoundWave.exe --doctor. Prints when there is a console, and shows a window when
    /// there is not — the double-click case. It also always saves a copy, because "I
    /// ran it and nothing happened" is the least useful bug report there is, and that
    /// is exactly what a windowed program printing to a console it does not have looks
    /// like from the outside.
    /// </summary>
    public static int Run()
    {
        var text = Report();
        var problems = Check();

        try
        {
            Directory.CreateDirectory(Settings.AppDataDir);
            File.WriteAllText(ReportPath, text);
        }
        catch { /* a report we cannot save is still a report we can show */ }

        if (ConsoleBridge.HasConsole)
        {
            Console.Write(text);
            Console.WriteLine();
            Console.WriteLine("saved to " + ReportPath);
        }
        else
        {
            MessageBox.Show(
                text + Environment.NewLine + "Saved to:" + Environment.NewLine + ReportPath,
                "XzoundWave diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        return problems.Any(p => p.Fatal) ? 1 : 0;
    }
}
