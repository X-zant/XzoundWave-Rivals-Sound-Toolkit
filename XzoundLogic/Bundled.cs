using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace MRAudioKit;

/// <summary>
/// Things that ship inside the exe and have to become real files before they are any
/// use — vgmstream and the licence texts.
///
/// The release is a single file on purpose, so nothing can be shipped "next to" it.
/// vgmstream is a separate program we invoke, not a library we link, so it has to
/// exist on disk to be run: it is written once into app data and reused after that.
/// </summary>
public static class Bundled
{
    /// <summary>Where unpacked tools live. Beside the settings, not beside the exe,
    /// which may sit somewhere the user cannot write.</summary>
    public static string ToolsDir => Path.Combine(Settings.AppDataDir, "vgmstream");

    public static string VgmstreamExe => Path.Combine(ToolsDir, "vgmstream-cli.exe");

    private const string ZipResource = "XzoundWave.vgmstream.zip";
    private const string LicenceResource = "XzoundWave.LICENSE";
    private const string NoticesResource = "XzoundWave.THIRD-PARTY-NOTICES.md";

    /// <summary>Marks which build unpacked the folder, so an upgrade replaces it.</summary>
    private static string StampFile => Path.Combine(ToolsDir, "unpacked-by.txt");
    private static string Stamp =>
        typeof(Bundled).Assembly.GetName().Version?.ToString() ?? "unknown";

    public static bool HasVgmstream => File.Exists(VgmstreamExe);

    /// <summary>
    /// Make sure vgmstream is on disk and return the path to its exe, or null with a
    /// reason. Cheap to call repeatedly: it does nothing once unpacked by this build.
    /// </summary>
    public static string EnsureVgmstream(out string error)
    {
        error = null;
        try
        {
            var stamped = File.Exists(StampFile) ? File.ReadAllText(StampFile).Trim() : null;
            if (HasVgmstream && stamped == Stamp) return VgmstreamExe;

            using var res = typeof(Bundled).Assembly.GetManifestResourceStream(ZipResource);
            if (res is null)
            {
                error = "this build has no bundled vgmstream — set the path in Settings.";
                return HasVgmstream ? VgmstreamExe : null;
            }

            Directory.CreateDirectory(ToolsDir);
            using (var zip = new ZipArchive(res, ZipArchiveMode.Read))
                foreach (var entry in zip.Entries)
                {
                    if (entry.Length == 0 && entry.Name.Length == 0) continue;   // folder
                    var dest = Path.Combine(ToolsDir, entry.Name);
                    // A running vgmstream-cli.exe cannot be overwritten. That is not
                    // worth failing over when the file we would write is identical.
                    try { entry.ExtractToFile(dest, overwrite: true); }
                    catch (IOException) when (File.Exists(dest)) { }
                }

            WriteText(Path.Combine(ToolsDir, "XzoundWave-LICENSE.txt"), LicenceResource);
            WriteText(Path.Combine(ToolsDir, "XzoundWave-THIRD-PARTY-NOTICES.md"), NoticesResource);

            File.WriteAllText(StampFile, Stamp);
            return HasVgmstream ? VgmstreamExe : null;
        }
        catch (Exception ex)
        {
            error = "could not unpack vgmstream: " + ex.Message;
            return HasVgmstream ? VgmstreamExe : null;
        }
    }

    /// <summary>The text of an embedded document, or null.</summary>
    public static string ReadText(string resource)
    {
        using var s = typeof(Bundled).Assembly.GetManifestResourceStream(resource);
        if (s is null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static string Licence => ReadText(LicenceResource);
    public static string Notices => ReadText(NoticesResource);

    private static void WriteText(string path, string resource)
    {
        var text = ReadText(resource);
        if (text is not null) File.WriteAllText(path, text);
    }

    /// <summary>XzoundWave.exe --licences — print what ships inside the binary.</summary>
    public static int PrintLicences()
    {
        Console.WriteLine(Licence ?? "(licence resource missing)");
        Console.WriteLine();
        Console.WriteLine(new string('-', 78));
        Console.WriteLine();
        Console.WriteLine(Notices ?? "(notices resource missing)");
        return Licence is null || Notices is null ? 1 : 0;
    }
}
