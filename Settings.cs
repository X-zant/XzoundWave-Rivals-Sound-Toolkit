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

    /// <summary>
    /// Kinds of row the author turned off, by key -- see <see cref="RowClasses"/>.
    /// Same reasoning as <see cref="HiddenColumns"/>: a preference about the tool, and
    /// null means "never chosen" rather than "nothing hidden".
    /// </summary>
    public List<string> HiddenRowClasses { get; set; }

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

    /// <summary>The naming rule in the drop pane, once you know it, is just clutter.</summary>
    public bool ShowDropHelp { get; set; } = true;

    public string TranslateProvider { get; set; } = "none";
    public string DeepLKey { get; set; } = "";
    public string LocalTranslateUrl { get; set; } = "";
    public string LocalTranslateModel { get; set; } = "";

    /// <summary>Reopened on launch, plus the Open menu's recent list.</summary>
    public string LastProject { get; set; } = "";
    public List<string> RecentProjects { get; set; } = [];

    public void NoteRecentProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
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
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(File_)) ?? new Settings();

            // First run after the rename: adopt the old settings rather than asking
            // for the AES key and paks path again. The old folder is left alone.
            var legacy = Path.Combine(LegacyDir, "settings.json");
            if (File.Exists(legacy))
            {
                var moved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(legacy));
                if (moved is not null) { moved.Save(); return moved; }
            }
        }
        catch { /* a corrupt settings file must not stop the app starting */ }
        return new Settings();
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
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                foreach (var lib in new[] { "SteamLibrary", "Steam", "Games" })
                {
                    var p = Path.Combine(drive.RootDirectory.FullName, lib,
                        "steamapps", "common", "MarvelRivals", "MarvelGame", "Marvel", "Content", "Paks");
                    if (Directory.Exists(p)) { PaksDir = p; break; }
                }
                if (PaksDir.Length > 0) break;
            }
        }

        // vgmstream ships inside the exe. A copy the user pointed at themselves wins;
        // otherwise one beside the exe, and failing that the bundled one is unpacked.
        if (!string.IsNullOrWhiteSpace(VgmstreamPath) && !File.Exists(VgmstreamPath))
            VgmstreamPath = "";
        if (string.IsNullOrWhiteSpace(VgmstreamPath))
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "vgmstream-cli.exe");
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
