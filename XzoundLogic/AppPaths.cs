using System.IO;

namespace MRAudioKit;

/// <summary>
/// The handful of places where the filesystem has an opinion about the platform.
///
/// Three of them, and each was wrong somewhere before this existed: whether two names
/// are the same file, whether a program has a suffix, and where a game is installed.
/// They are together because they are the same question asked three ways, and because
/// a rule spread across five files is a rule that only holds in four of them.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Do these name the same file?
    ///
    /// Windows says A.wem and a.wem are one file; almost everything else says they are
    /// two. Comparing with OrdinalIgnoreCase everywhere -- which is what the code did --
    /// is right on Windows and quietly wrong elsewhere, and the symptom is a write that
    /// never happens because the destination "already exists".
    ///
    /// Note this is for real paths only. Names INSIDE a pak stay case-insensitive
    /// whatever the host, because that is Unreal's rule and not the filesystem's.
    /// </summary>
    public static bool SamePath(string a, string b)
    {
        if (a is null || b is null) return a is null && b is null;
        return string.Equals(Full(a), Full(b), Comparison);
    }

    public static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Full(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return p; }
    }

    /// <summary>A program's file name, with the suffix only where one is expected.</summary>
    public static string Exe(string name) =>
        OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>
    /// Make an unpacked program runnable. A zip carries no permission bits that any
    /// platform but Windows respects, so a tool extracted from one is not executable
    /// until something says so -- and the failure reads as "the tool is missing".
    /// </summary>
    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute
                                            | UnixFileMode.GroupExecute
                                            | UnixFileMode.OtherExecute);
        }
        catch { /* worth trying, not worth failing the unpack over */ }
    }

    /// <summary>
    /// Where a game might be installed, in the order worth looking.
    ///
    /// Windows spreads Steam libraries across drive letters; everywhere else they are
    /// under the home directory, and a game running through Proton keeps its files in
    /// the same place a native one would. Returns library roots, not the game.
    /// </summary>
    public static IEnumerable<string> SteamLibraries()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in Drives())
                foreach (var lib in new[] { "SteamLibrary", "Steam", "Games" })
                    yield return Path.Combine(drive, lib);
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) yield break;

        // ~/.steam/steam is the old symlink and still the one most setups have;
        // ~/.local/share/Steam is where a current client actually puts things; the
        // flatpak build is walled off in its own tree and finds neither.
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam",
                                  ".local", "share", "Steam");
        yield return Path.Combine(home, "Games", "Steam");
    }

    private static IEnumerable<string> Drives()
    {
        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); } catch { yield break; }
        foreach (var d in all)
        {
            string root;
            try { root = d.RootDirectory.FullName; } catch { continue; }
            yield return root;
        }
    }
}
