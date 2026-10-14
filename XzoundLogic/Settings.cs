using System.IO;
using System.Text.Json;

namespace MRAudioKit;

/// <summary>
/// Where the four things this tool needs live. NONE of them are shipped with it --
/// the AES key in particular is the user's to supply, never ours to redistribute.
/// </summary>
public sealed class Settings
{
    public string PaksDir { get; set; } = "";

    /// <summary>Build mod writes one mod per character (or per map, menu, music) instead of one in all.</summary>
    public bool SplitModsByGroup { get; set; }

    /// <summary>
    /// The game has loaded here before with these settings. Opening the window then loads it
    /// again by itself, in the language last picked, instead of waiting on the Setup tab.
    /// </summary>
    public bool LoadedOnce { get; set; }
    public string AesKey { get; set; } = "";
    public string UsmapPath { get; set; } = "";
    public string VgmstreamPath { get; set; } = "";
    public string Language { get; set; } = "English(US)";

    /// <summary>
    /// What the last usmap check found — see <see cref="UsmapFetch"/>. The ETag keeps a
    /// routine check free against GitHub's unauthenticated rate limit, and the sha says
    /// whether the file on disk is already the one the depot is offering.
    /// </summary>
    public string UsmapEtag { get; set; } = "";
    public string UsmapSha { get; set; } = "";
    public string UsmapFileName { get; set; } = "";

    /// <summary>Folder of numbered test clips ({n}.wem, plus an optional silent one).</summary>
    public string TestWemDir { get; set; } = "";

    /// <summary>
    /// Optional translator for the Chinese Category labels the game ships no English
    /// for. Off unless chosen: the local options keep everything on your machine, and
    /// nothing is ever sent without being asked for. See <see cref="LiveTranslate"/>.
    /// </summary>
    /// <summary>
    /// Grid columns the author turned off, by header. Kept here rather than in the
    /// project because it is a preference about the tool, not about a mod: it should
    /// survive opening a different project tomorrow.
    ///
    /// Null means "never chosen", which is not the same as "nothing hidden" -- it is
    /// what lets the first run start from a sensible set rather than every column.
    /// </summary>
    public List<string> HiddenColumns { get; set; }

    /// <summary>The list's column widths as they were dragged, by header. Null until one is.</summary>
    public Dictionary<string, double> ColumnWidths { get; set; }

    /// <summary>
    /// Kinds of row the author turned off, by key -- see <see cref="RowClasses"/>.
    /// Same reasoning as <see cref="HiddenColumns"/>: a preference about the tool, and
    /// null means "never chosen" rather than "nothing hidden".
    /// </summary>
    public List<string> HiddenRowClasses { get; set; }

    /// <summary>
    /// Which change to the row defaults this file has been brought through. 1: unnamed events
    /// are no longer hidden by default, and a file whose hidden list was exactly the old default
    /// -- never a choice its owner made -- goes back to the new one.
    /// </summary>
    public int RowDefaults { get; set; }

    /// <summary>
    /// Keep what each imported file encoded to, so restaging the same audio does not
    /// pay for the encode again -- see <see cref="ConvertedCache"/>. On by default: it
    /// costs disk that can be reclaimed at any time and saves the slowest step there is.
    /// </summary>
    public bool CacheConvertedAudio { get; set; } = true;

    /// <summary>Empty means beside the settings.</summary>
    public string ConvertedCacheDir { get; set; } = "";

    /// <summary>
    /// Write the converted .wem back over the file it came from, deleting the original.
    /// DESTRUCTIVE, and off unless asked for: it makes every later import a plain file
    /// read, at the cost of the source audio no longer existing.
    /// </summary>
    public bool ReplaceSourceWithWem { get; set; }

    /// <summary>
    /// Warnings the author has ticked "don't show this again" on — see
    /// <see cref="Notice"/>. Kept by key rather than as a single "quiet" flag, so
    /// silencing one thing never silences a different one.
    /// </summary>
    public List<string> SuppressedWarnings { get; set; } = [];

    /// <summary>
    /// Preview a sound the way the .bnk plays it rather than the way the file sits on
    /// disk. Off by default: the raw file is what you are editing, and hearing it
    /// unchanged is the thing you cannot get any other way.
    /// </summary>
    public bool PreviewAsInGame { get; set; }

    /// <summary>The naming rule in the drop pane, once you know it, is just clutter.</summary>
    public bool ShowDropHelp { get; set; } = true;

    /// <summary>
    /// Which of the right panel's two sub-panels was last picked. False is the staged list,
    /// which is what a fresh start should show: it is the one of the two that has to be seen
    /// while something is being dropped, and the details panel says nothing at all until a
    /// row is selected.
    ///
    /// A plain bool rather than the nullable one <see cref="EnglishCategories"/> uses: null
    /// exists there to let a property of the HOST decide, and nothing about which sub-panel
    /// to show comes from the host, so there is no third state to represent. Written only
    /// when the switch itself is clicked, because a switch the program makes, as a drop
    /// does, is not a choice and must not be remembered as one.
    /// </summary>
    public bool RightPanelDetails { get; set; }

    public string TranslateProvider { get; set; } = "none";
    public string DeepLKey { get; set; } = "";
    public string LocalTranslateUrl { get; set; } = "";
    public string LocalTranslateModel { get; set; } = "";

    /// <summary>
    /// Show the Category column in English rather than in the Chinese the game
    /// authored it in -- see <see cref="CategoryTranslator"/>, which needs no provider
    /// and no network to do most of it.
    ///
    /// Nullable for the same reason <see cref="HiddenColumns"/> is: null means never
    /// chosen, which is not the same as chosen-and-off. It is what lets a host with no
    /// font that can draw the script start in English, where the alternative is a
    /// column of empty boxes, without overruling anyone who decided otherwise.
    /// </summary>
    public bool? EnglishCategories { get; set; }

    /// <summary>Reopened on launch, plus the Open menu's recent list.</summary>
    public string LastProject { get; set; } = "";
    public List<string> RecentProjects { get; set; } = [];

    public void NoteRecentProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RecentProjects.RemoveAll(p => AppPaths.SamePath(p, path));
        RecentProjects.Insert(0, path);
        while (RecentProjects.Count > 10) RecentProjects.RemoveAt(RecentProjects.Count - 1);
        LastProject = path;
    }


    /// <summary>Settings, notes and unpacked tools all live here.</summary>
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XzoundWave");

    /// <summary>The folder used before the tool was renamed, read once so an existing
    /// user keeps their paks path and AES key instead of starting over.</summary>
    private static string LegacyDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MRAudioKit");

    private static string Dir => AppDataDir;
    private static string File_ => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(File_))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(File_)) ?? new Settings { RowDefaults = 1 };
                if (loaded.MigrateRowDefaults()) loaded.Save();
                return loaded;
            }

            // First run after the rename: adopt the old settings rather than asking
            // for the AES key and paks path again. The old folder is left alone.
            var legacy = Path.Combine(LegacyDir, "settings.json");
            if (File.Exists(legacy))
            {
                var moved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(legacy));
                if (moved is not null) { moved.MigrateRowDefaults(); moved.Save(); return moved; }
            }
        }
        catch { /* a corrupt settings file must not stop the app starting */ }
        // A first run starts on the current defaults, so nothing it later chooses is migrated away.
        return new Settings { RowDefaults = 1 };
    }

    /// <summary>
    /// Bring the row defaults up to date, once per file. True when anything changed. A hidden
    /// list that is exactly the old default ("unnamed" alone) was never chosen, so it goes;
    /// any other list was someone's choice and is kept as it is.
    /// </summary>
    public bool MigrateRowDefaults()
    {
        if (RowDefaults >= 1) return false;
        if (HiddenRowClasses is [var only] && only.Equals("unnamed", StringComparison.OrdinalIgnoreCase))
            HiddenRowClasses = null;
        RowDefaults = 1;
        return true;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(File_, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// Best-effort first-run guesses so a new user is not staring at four empty boxes.
    /// Everything here is a convenience; nothing is required to be found.
    /// </summary>
    public void AutoDetect()
    {
        if (string.IsNullOrWhiteSpace(PaksDir))
        {
            // Where a Steam library lives is the platform's business, not this
            // method's -- drive letters on Windows, the home directory everywhere
            // else. The rest of the path is the game's own and is the same either way,
            // including under Proton, which lays the files out natively.
            foreach (var lib in AppPaths.SteamLibraries())
            {
                var p = Path.Combine(lib, "steamapps", "common",
                    "MarvelRivals", "MarvelGame", "Marvel", "Content", "Paks");
                if (Directory.Exists(p)) { PaksDir = p; break; }
            }
        }

        // vgmstream ships inside the exe. A copy the user pointed at themselves wins;
        // otherwise one beside the exe, and failing that the bundled one is unpacked.
        if (!string.IsNullOrWhiteSpace(VgmstreamPath) && !File.Exists(VgmstreamPath))
            VgmstreamPath = "";
        if (string.IsNullOrWhiteSpace(VgmstreamPath))
        {
            var beside = Path.Combine(AppContext.BaseDirectory, AppPaths.Exe("vgmstream-cli"));
            VgmstreamPath = File.Exists(beside) ? beside : Bundled.EnsureVgmstream(out _) ?? "";
        }

        // A usmap sitting next to the exe is the common case once someone drops one in.
        if (string.IsNullOrWhiteSpace(UsmapPath))
        {
            var maps = Directory.Exists(AppContext.BaseDirectory)
                ? Directory.GetFiles(AppContext.BaseDirectory, "*.usmap") : [];
            if (maps.Length > 0) UsmapPath = maps[0];
        }
    }
}
