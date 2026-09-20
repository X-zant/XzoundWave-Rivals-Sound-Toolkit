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

    /// <summary>XzoundWave.exe --doctor — what this machine provides.</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);

        W("XzoundWave " + (typeof(EnvCheck).Assembly.GetName().Version?.ToString() ?? "?"));
        W("");
        W($"  runtime       {RuntimeInformation.FrameworkDescription}");
        W($"  built for     .NET {RequiredMajor} (runs on newer)");
        W($"  process       {(Environment.Is64BitProcess ? "64-bit" : "32-BIT")}");
        W($"  os            {RuntimeInformation.OSDescription}");
        W($"  app data      {Settings.AppDataDir}");

        var vgm = Bundled.EnsureVgmstream(out var vgmError);
        W($"  vgmstream     {(vgm is not null ? vgm : "NOT AVAILABLE — " + vgmError)}");
        if (vgm is not null)
            W($"                {(File.Exists(vgm) ? new FileInfo(vgm).Length.ToString("N0") + " B, unpacked from the exe" : "missing")}");

        W($"  licences      {(Bundled.Licence is not null && Bundled.Notices is not null ? "embedded" : "MISSING")}");

        W("");
        var problems = Check();
        if (problems.Count == 0)
        {
            W("no problems found.");
            return 0;
        }
        foreach (var p in problems)
        {
            W($"{(p.Fatal ? "BLOCKING" : "warning ")}  {p.Title}");
            foreach (var line in p.Detail.Split('\n')) W("          " + line.TrimEnd());
        }
        return problems.Any(p => p.Fatal) ? 1 : 0;
    }
}
