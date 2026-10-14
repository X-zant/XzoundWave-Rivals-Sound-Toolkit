using System.IO;

namespace MRAudioKit;

/// <summary>
/// The places where the filesystem has an opinion about the platform.
///
/// Every one of these was wrong somewhere before it was written down. A containment
/// check spelled with a backslash answers "no" on any host that does not use one, so a
/// file we downloaded looked like one the author had picked and was never replaced. Two
/// real paths compared case-insensitively make A.wem and a.wem the same file, which
/// they are on Windows and are not elsewhere, and the symptom is a write that silently
/// does not happen. A program unpacked from a zip has no execute bit anywhere but
/// Windows, and the failure reads as the tool being missing rather than unrunnable.
///
/// None of that shows up as an error on the machine it was written on, which is exactly
/// why it needs a test that states the rule rather than the platform.
/// </summary>
public static class PlatformSelfTest
{
    /// <summary>XzoundWave.exe --platform</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        var windows = OperatingSystem.IsWindows();
        W($"host: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        W($"separator: '{Path.DirectorySeparatorChar}'   case-sensitive paths: {!windows}");

        // ---- the same file, or two files ----------------------------------
        W("");
        W("telling one file from two");
        var dir = Path.Combine(Path.GetTempPath(), "xzw-platform-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var lower = Path.Combine(dir, "clip.wem");
            var upper = Path.Combine(dir, "CLIP.wem");

            Check("a path is the same file as itself", AppPaths.SamePath(lower, lower));
            Check("a relative and an absolute spelling are one file",
                  AppPaths.SamePath(lower, Path.Combine(dir, ".", "clip.wem")));
            Check("two different names are two files",
                  !AppPaths.SamePath(lower, Path.Combine(dir, "other.wem")));

            // The whole point. The answer differs by platform and must follow it.
            Check($"case {(windows ? "does not" : "does")} distinguish them here",
                  AppPaths.SamePath(lower, upper) == windows,
                  $"SamePath(clip.wem, CLIP.wem) = {AppPaths.SamePath(lower, upper)}");

            // And the filesystem agrees, which is the claim that actually matters.
            File.WriteAllText(lower, "one");
            var reallyOne = File.Exists(upper);
            Check("...and the filesystem itself says the same",
                  reallyOne == windows, $"writing clip.wem {(reallyOne ? "created" : "did not create")} CLIP.wem");

            // ---- a program's name ------------------------------------------
            W("");
            W("naming a program");
            Check("the suffix is there only where one is expected",
                  AppPaths.Exe("vgmstream-cli") == (windows ? "vgmstream-cli.exe" : "vgmstream-cli"),
                  AppPaths.Exe("vgmstream-cli"));
            Check("the unpacked tool is named the same way",
                  Path.GetFileName(Bundled.VgmstreamExe) == AppPaths.Exe("vgmstream-cli"),
                  Path.GetFileName(Bundled.VgmstreamExe));

            // ---- making it runnable ----------------------------------------
            var tool = Path.Combine(dir, AppPaths.Exe("pretend-tool"));
            File.WriteAllText(tool, "#!/bin/sh\necho hi\n");
            AppPaths.MakeExecutable(tool);
            if (windows)
                W("  --    execute bits do not apply on this host");
            else
                Check("an unpacked program comes out runnable",
                      File.GetUnixFileMode(tool).HasFlag(UnixFileMode.UserExecute),
                      File.GetUnixFileMode(tool).ToString());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // ---- is this file one we downloaded? -------------------------------
        W("");
        W("a file we manage, or one the author picked");
        var managed = Path.Combine(UsmapFetch.ManagedDir, "mappings.usmap");
        Check("a file inside our folder is ours", UsmapFetch.IsManaged(managed), managed);
        Check("...however the separators are spelled",
              UsmapFetch.IsManaged(managed.Replace(Path.DirectorySeparatorChar, '/')));
        Check("a file somewhere else is not",
              !UsmapFetch.IsManaged(Path.Combine(Path.GetTempPath(), "mine.usmap")));
        // The bug this replaces: the folder itself is not a file inside it, and a
        // sibling whose name merely starts the same way is not either.
        Check("the folder is not a file in itself", !UsmapFetch.IsManaged(UsmapFetch.ManagedDir));
        Check("a sibling with a longer name is not inside",
              !UsmapFetch.IsManaged(UsmapFetch.ManagedDir + "-old" + Path.DirectorySeparatorChar + "x.usmap"));

        // ---- what a project carries to another machine ----------------------
        //
        // Unlike everything above, none of these answers may follow the platform. A
        // stored path is the one thing here that a different machine has to read back,
        // so the spelling has to be one both agree on rather than this host's own.
        W("");
        W("what a project stores, and what it can read back");
        var projDir = Path.Combine(Path.GetTempPath(), "xzw-portable-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(projDir, "audio"));
        try
        {
            var projFile = Path.Combine(projDir, "portable" + Project.Extension);
            var staged = Path.Combine(projDir, "audio", "x.wem");
            File.WriteAllBytes(staged, [1, 2, 3, 4]);

            var project = Project.New();
            project.Save(projFile);

            var stored = project.ToStoredPath(staged);
            Check("a file under the project is stored relative to it",
                  stored == "audio/x.wem", stored);
            Check("...spelled with forward slashes on every host",
                  !stored.Contains('\\'), stored);

            project.Replacements.Add(new ProjectReplacement { MediaId = 4242, File = stored });
            project.Save();
            Check("...and that is what reaches the file on disk",
                  !File.ReadAllText(projFile).Contains('\\'));

            // The defect this replaces. A project written by a Windows build stored
            // "audio\x.wem", and a backslash is an ordinary character in a name
            // anywhere else, so combining it named a single file that has never
            // existed and the staged clip was reported missing.
            var fromWindows = project.ToFullPath("audio\\x.wem");
            Check("a path an older Windows build stored still resolves",
                  AppPaths.SamePath(fromWindows, staged), fromWindows);
            Check("...to the file that is really there", File.Exists(fromWindows));
            Check("...and to the name a person reads",
                  Path.GetFileName(fromWindows) == "x.wem", Path.GetFileName(fromWindows));

            var portable = project.ToFullPath("audio/x.wem");
            Check("the forward-slash spelling resolves to the same file",
                  File.Exists(portable) && AppPaths.SamePath(portable, fromWindows), portable);

            // Through a real save and load, because the loader is what an author
            // actually opens. No current build can write this any more, so the
            // replacement is spelled out by hand the way an old one left it.
            var legacy = Path.Combine(projDir, "legacy" + Project.Extension);
            var older = Project.New();
            older.Replacements.Add(new ProjectReplacement { MediaId = 4242, File = "audio\\x.wem" });
            older.Save(legacy);
            var reopened = Project.Load(legacy);
            var restaged = reopened.ToFullPath(reopened.Replacements[0].File);
            Check("an older project opens and finds its staged file",
                  File.Exists(restaged), restaged);

            // A path outside the project folder is this machine's own spelling of
            // somewhere it alone knows about, and rewriting it would help nobody.
            var outside = Path.Combine(Path.GetTempPath(), "elsewhere.wem");
            Check("a file outside the project is stored whole and untouched",
                  project.ToStoredPath(outside) == outside, project.ToStoredPath(outside));
        }
        finally { try { Directory.Delete(projDir, true); } catch { } }

        // ---- where a game might be -----------------------------------------
        W("");
        W("where to look for the game");
        var libs = AppPaths.SteamLibraries().ToList();
        Check("somewhere is suggested", libs.Count > 0, $"{libs.Count} place(s)");
        Check("they suit this platform",
              windows
                  ? libs.Any(l => l.Contains("Steam", StringComparison.OrdinalIgnoreCase))
                  : libs.All(l => !l.Contains(":\\", StringComparison.Ordinal)),
              string.Join(", ", libs.Take(3)));

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
