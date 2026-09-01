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

    /// <summary>Folder of numbered test clips ({n}.wem, plus an optional silent one).</summary>
    public string TestWemDir { get; set; } = "";

    /// <summary>Optional: Audiokinetic WwiseConsole.exe and any .wproj, for Vorbis output.</summary>
    public string WwiseConsolePath { get; set; } = "";
    public string WwiseProjectPath { get; set; } = "";

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MRAudioKit");
    private static string File_ => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(File_))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(File_)) ?? new Settings();
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

        if (string.IsNullOrWhiteSpace(VgmstreamPath))
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "vgmstream-cli.exe");
            if (File.Exists(beside)) VgmstreamPath = beside;
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
