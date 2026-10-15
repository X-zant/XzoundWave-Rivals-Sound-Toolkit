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

        // What this machine would actually be given, said rather than checked: a host
        // without the game installed is not a host that failed anything.
        var fromSteam = libs.Except(AppPaths.UsualLibraries(), AppPaths.Comparer).ToList();
        W($"  --    {fromSteam.Count} of them from Steam's own list" +
          (fromSteam.Count == 0 ? "" : $": {string.Join(", ", fromSteam)}"));
        W($"  --    the game here: {Settings.FindPaks() ?? "not in any of them"}");

        // ---- the libraries Steam itself lists -------------------------------
        //
        // A second library is the case the usual places miss, and Steam keeps every one
        // of them in libraryfolders.vdf. The reading is tested on a file written here,
        // because a machine with one library has nothing in its own file to get wrong.
        W("");
        W("reading Steam's own list of libraries");
        var steamDir = Path.Combine(Path.GetTempPath(), "xzw-steam-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(steamDir);
        try
        {
            // The current client's shape. Both kinds of folder sit in one file on
            // purpose: which host reads the file must not change what comes out of it,
            // and the backslashes in a Windows folder are written doubled.
            var sample = Path.Combine(steamDir, "libraryfolders.vdf");
            File.WriteAllText(sample, """
                "libraryfolders"
                {
                    "0"
                    {
                        "path"        "D:\\Games\\Steam"
                        "label"       ""
                        "contentid"   "4721958234023650941"
                        "totalsize"   "0"
                        "apps"
                        {
                            "2767030"     "40453453824"
                        }
                    }
                    "1"
                    {
                        "path"        "/run/media/mmcblk0p1/SteamLibrary"
                        "label"       "SD card"
                        "contentid"   "1873641873649813746"
                        "totalsize"   "512110190592"
                        "apps"
                        {
                        }
                    }
                }
                """);
            var listed = AppPaths.LibraryFolders(sample);
            Check("both libraries come out, and nothing else does",
                  listed.Count == 2, string.Join(" | ", listed));
            Check("a Windows folder comes out with single backslashes",
                  listed.ElementAtOrDefault(0) == @"D:\Games\Steam", listed.ElementAtOrDefault(0));
            Check("a Linux folder comes out as written",
                  listed.ElementAtOrDefault(1) == "/run/media/mmcblk0p1/SteamLibrary",
                  listed.ElementAtOrDefault(1));

            // The older shape, where the folder is the numbered key's own value.
            var older = Path.Combine(steamDir, "older.vdf");
            File.WriteAllText(older, """
                "LibraryFolders"
                {
                    "TimeNextStatsReport"   "1626342145"
                    "ContentStatsID"        "-4721958234023650941"
                    "1"                     "E:\\SteamLibrary"
                }
                """);
            var olderListed = AppPaths.LibraryFolders(older);
            Check("the older shape is read too",
                  olderListed is [@"E:\SteamLibrary"], string.Join(" | ", olderListed));

            // What another program's file can do to us. None of these may throw, and
            // a file cut short, here in the middle of a quoted value, still gives up
            // what it got to before it stopped.
            var cut = Path.Combine(steamDir, "cut.vdf");
            var whole = File.ReadAllText(sample);
            File.WriteAllText(cut, whole[..(whole.IndexOf("\"contentid\"", StringComparison.Ordinal) + 17)]);
            var cutListed = AppPaths.LibraryFolders(cut);
            Check("a file cut off part way gives what it had",
                  cutListed is [@"D:\Games\Steam"], string.Join(" | ", cutListed));

            var stray = Path.Combine(steamDir, "stray.vdf");
            File.WriteAllText(stray, "}}}} \"path\" { \"1\" } \"2\" \"x/y\" \"path");
            var strayListed = AppPaths.LibraryFolders(stray);
            Check("stray braces and keys without values give nothing",
                  strayListed.Count == 0, $"{strayListed.Count} found");
            Check("a missing file gives nothing",
                  AppPaths.LibraryFolders(Path.Combine(steamDir, "absent.vdf")).Count == 0);

            // And through the same search the game is looked for with: the usual places
            // first, then Steam's own folder, then what its file adds, each named once.
            var install = Path.Combine(steamDir, "Steam");
            Directory.CreateDirectory(Path.Combine(install, "steamapps"));
            var usual = Path.Combine(steamDir, "Lib-A");
            var recased = Path.Combine(steamDir, "LIB-A");
            var added = Path.Combine(steamDir, "Lib-B");
            string Q(string p) => "\"" + p.Replace("\\", "\\\\") + "\"";
            File.WriteAllText(Path.Combine(install, "steamapps", "libraryfolders.vdf"),
                "\"libraryfolders\"\n{\n" +
                $"  \"0\" {{ \"path\" {Q(install)} }}\n" +
                $"  \"1\" {{ \"path\" {Q(usual)} }}\n" +
                $"  \"2\" {{ \"path\" {Q(recased)} }}\n" +
                $"  \"3\" {{ \"path\" {Q(added)} }}\n" +
                "}\n");
            var order = AppPaths.Libraries([usual],
                [install, Path.Combine(steamDir, "not-installed"), install]).ToList();
            var names = string.Join(", ", order.Select(Path.GetFileName));
            Check("the usual places still come first", order.FirstOrDefault() == usual, names);
            Check("then Steam's own folder, then the library it adds",
                  order.IndexOf(install) == 1 && order.LastOrDefault() == added, names);
            Check("a library already tried is not tried again",
                  order.Count(p => p == usual) == 1 && order.Count(p => p == install) == 1, names);
            Check($"the same folder in another case {(windows ? "is a repeat" : "is another library")} here",
                  order.Contains(recased) == !windows, names);
            Check("an install that is not there is passed over",
                  order.Count == (windows ? 3 : 4), $"{order.Count} place(s)");

            // An install whose list is gone is still a library: it is where the client
            // puts games until told otherwise.
            var bare = Path.Combine(steamDir, "Bare");
            Directory.CreateDirectory(bare);
            var bareOrder = AppPaths.Libraries([], [bare]).ToList();
            Check("a Steam folder with no list is still a library",
                  bareOrder is [var only] && only == bare, string.Join(", ", bareOrder));
        }
        finally { try { Directory.Delete(steamDir, true); } catch { } }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
