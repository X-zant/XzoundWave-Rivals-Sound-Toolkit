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
    /// Where a game might be installed, in the order worth looking. Returns library
    /// roots, not the game.
    ///
    /// The usual places come first, because each costs one directory check and between
    /// them they find most installs. After them come the libraries Steam itself was told
    /// about. Every folder added in Steam's storage settings is written to
    /// steamapps/libraryfolders.vdf under the client's own install, and a second library
    /// is exactly the case the usual places miss: a games drive mounted at /mnt/games,
    /// the Steam Deck's SD card under /run/media, a D:\Games\Steam that is neither of the
    /// names guessed at the root of a drive. Nothing is read until the usual places run
    /// out, so a game one of them finds never pays for the file.
    /// </summary>
    public static IEnumerable<string> SteamLibraries() =>
        Libraries(UsualLibraries(), SteamInstalls());

    /// <summary>
    /// The usual places, then what each Steam install lists, with every library named
    /// once. Apart from <see cref="SteamLibraries"/> so the self-test can hand it a
    /// Steam install of its own making instead of depending on what a machine has.
    ///
    /// "Once" by this host's rule for names, which is <see cref="Comparer"/>'s whole job:
    /// the same folder in another case is a repeat on Windows and a different folder
    /// elsewhere. Repeats are the normal case rather than the odd one. The file always
    /// lists the install it sits in, the registry and the installer default usually
    /// name that same install in two spellings, and a library on a drive root has
    /// already been tried as a usual place by the time the file mentions it.
    /// </summary>
    internal static IEnumerable<string> Libraries(IEnumerable<string> usual, IEnumerable<string> installs)
    {
        var seen = new HashSet<string>(Comparer);
        foreach (var lib in usual)
            if (seen.Add(Full(lib))) yield return lib;

        var read = new HashSet<string>(Comparer);
        foreach (var steam in installs)
        {
            if (string.IsNullOrWhiteSpace(steam) || !read.Add(Full(steam)) || !Directory.Exists(steam))
                continue;

            foreach (var lib in LibraryFolders(Path.Combine(steam, "steamapps", "libraryfolders.vdf")))
            {
                // Steam writes whole paths. A relative one would be resolved against
                // whatever folder this was started from, which is no library at all.
                if (!Path.IsPathRooted(lib)) continue;
                if (seen.Add(Full(lib))) yield return lib;
            }

            // The install is a library in its own right, and the file lists it first, in
            // the client's own spelling of it -- which is why the file goes first and this
            // second. Saying it here as well keeps it found when the file is missing or
            // unreadable, and is a repeat that costs nothing when it is not.
            if (seen.Add(Full(steam))) yield return steam;
        }
    }

    /// <summary>
    /// The places a library usually is, tried before anything is read.
    ///
    /// Windows spreads Steam libraries across drive letters; everywhere else they are
    /// under the home directory, and a game running through Proton keeps its files in
    /// the same place a native one would.
    /// </summary>
    internal static IEnumerable<string> UsualLibraries()
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
        // flatpak and snap builds are each walled off in a tree of their own and find
        // neither.
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam",
                                  ".local", "share", "Steam");
        yield return Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        yield return Path.Combine(home, "Games", "Steam");
    }

    /// <summary>
    /// Where the Steam client itself might be installed, which is where its list of
    /// libraries lives.
    ///
    /// On Windows that is the client's own record of it first, then the installer's
    /// default under either Program Files, then the usual places, since the client can
    /// be installed to any of those as well. Everywhere else the usual places already
    /// are the client's own folders.
    /// </summary>
    private static IEnumerable<string> SteamInstalls()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return RegistrySteamPath();
            foreach (var pf in new[] { Environment.SpecialFolder.ProgramFilesX86,
                                       Environment.SpecialFolder.ProgramFiles })
            {
                var root = Environment.GetFolderPath(pf);
                if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, "Steam");
            }
        }
        foreach (var lib in UsualLibraries()) yield return lib;
    }

    /// <summary>
    /// The install folder the Steam client recorded for itself, or null. It writes it
    /// with forward slashes and, on at least some machines, in lower case. Windows minds
    /// neither. The slashes are turned round so a report of where something was found
    /// reads like any other path; the case is left alone, since comparisons ignore it
    /// here and the install's own list, read first, names the folder in its real case.
    /// </summary>
    private static string RegistrySteamPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string p && !string.IsNullOrWhiteSpace(p)
                ? Path.GetFullPath(p) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Every library a Steam libraryfolders.vdf names, in the order it names them. Empty
    /// when the file is missing or unreadable, and whatever was read before the point it
    /// stopped making sense when it is malformed. It never throws: the file belongs to
    /// another program, and a shape nobody expected has to cost a box to fill in on the
    /// Setup tab, never a window that will not open.
    ///
    /// The format is Valve's KeyValues text: quoted keys and values, braces for nesting,
    /// a backslash written as two. The current client gives each library a numbered
    /// block with its folder under "path". Older clients wrote the folder as the
    /// numbered key's own value, beside a couple of bookkeeping keys, and a file nobody
    /// has opened Steam over since can still be in that shape, so both are read. Nothing
    /// else in the file is wanted -- the per-app sizes in particular are numbered keys
    /// too, a level further in -- so this keeps track of depth while it reads tokens
    /// rather than building a tree it would then have to walk.
    /// </summary>
    public static List<string> LibraryFolders(string vdfFile)
    {
        var found = new List<string>();
        string text;
        try
        {
            if (!File.Exists(vdfFile)) return found;
            text = File.ReadAllText(vdfFile);
        }
        catch { return found; }

        try
        {
            var depth = 0;
            string key = null;
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                // A brace ends whatever key was waiting: the key before an opening one
                // named the block, and one left hanging before a closing one had no value.
                if (c == '{') { depth++; key = null; i++; continue; }
                if (c == '}') { depth = Math.Max(0, depth - 1); key = null; i++; continue; }

                string token;
                if (c == '"')
                {
                    var sb = new System.Text.StringBuilder();
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        // Only the two escapes a path can need. A backslash before
                        // anything else is kept as written, so a file some other tool
                        // wrote with single backslashes still names the folder it meant.
                        if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '\\' or '"')
                        {
                            sb.Append(text[i + 1]);
                            i += 2;
                            continue;
                        }
                        sb.Append(text[i++]);
                    }
                    i++; // the closing quote, or past the end of a file that stopped without one
                    token = sb.ToString();
                }
                else
                {
                    // KeyValues allows bare tokens too. Nothing Steam writes here uses
                    // them, but reading one keeps the keys and values after it paired.
                    var start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('"' or '{' or '}'))
                        i++;
                    token = text[start..i];
                }

                if (key is null) { key = token; continue; }
                if (IsLibrary(key, token, depth)) found.Add(token);
                key = null;
            }
        }
        catch { /* what was read before it stopped making sense is still worth trying */ }
        return found;
    }

    private static bool IsLibrary(string key, string value, int depth)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0')) return false;
        if (key.Equals("path", StringComparison.OrdinalIgnoreCase)) return true;

        // The older shape: a numbered key straight inside the outer block, whose value is
        // the folder. The value has to look like one, since numbered keys with numbers
        // for values are what the same file uses for everything else.
        return depth == 1 && key.Length > 0 && key.All(char.IsAsciiDigit)
            && (value.Contains('/') || value.Contains('\\'));
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
