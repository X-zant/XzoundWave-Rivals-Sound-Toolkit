using System.IO;
using System.Security.Cryptography;

namespace MRAudioKit;

/// <summary>
/// Remembers what a file encoded to, so the same audio is never encoded twice.
///
/// Encoding is the slow part of importing: a few hundred milliseconds a file, and a
/// voice pack is hundreds of files. Restaging the same folder tomorrow, or dropping
/// one file onto a second character, paid that cost again every time for a result
/// that could not have changed.
///
/// Keyed on the CONTENT of the source file rather than its path. Hashing is an order
/// of magnitude faster than encoding, and it means a file that was renamed, copied or
/// moved still hits — while a file that was edited correctly misses, which a path and
/// timestamp would get wrong in both directions.
/// </summary>
public static class ConvertedCache
{
    /// <summary>
    /// The settings the import path reads. Set once at startup rather than threaded
    /// through every call site: staging runs from the window, from headless verbs and
    /// from the self-tests, and none of those wanted a settings argument. Null means
    /// no caching, which is what the tests want anyway.
    /// </summary>
    public static Settings Options { get; set; }

    /// <summary>Where entries live. Beside the settings unless the author moved it.</summary>
    public static string Dir(Settings s) =>
        string.IsNullOrWhiteSpace(s?.ConvertedCacheDir)
            ? Path.Combine(Settings.AppDataDir, "converted")
            : s.ConvertedCacheDir;

    /// <summary>SHA-256 of a file, as the cache key.</summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string EntryPath(Settings s, string hash) =>
        Path.Combine(Dir(s), hash[..2], hash + ".wem");

    /// <summary>The stored conversion, or null.</summary>
    public static byte[] Get(Settings s, string hash)
    {
        if (s?.CacheConvertedAudio != true || hash is null) return null;
        try
        {
            var p = EntryPath(s, hash);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Store a conversion. Written to a temporary name and moved into place, so an
    /// interrupted write cannot leave a truncated entry that would later be served as
    /// if it were a real encode.
    /// </summary>
    public static void Put(Settings s, string hash, byte[] wem)
    {
        if (s?.CacheConvertedAudio != true || hash is null || wem is null) return;
        try
        {
            var p = EntryPath(s, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var tmp = p + ".part";
            File.WriteAllBytes(tmp, wem);
            File.Move(tmp, p, overwrite: true);
        }
        catch { /* a cache that cannot be written is a slow tool, not a broken one */ }
    }

    public sealed record Size(int Files, long Bytes);

    public static Size Stats(Settings s)
    {
        try
        {
            var dir = Dir(s);
            if (!Directory.Exists(dir)) return new Size(0, 0);
            var files = Directory.GetFiles(dir, "*.wem", SearchOption.AllDirectories);
            return new Size(files.Length, files.Sum(f => new FileInfo(f).Length));
        }
        catch { return new Size(0, 0); }
    }

    /// <summary>Delete every entry. Nothing here cannot be recreated by re-encoding.</summary>
    public static int Clear(Settings s)
    {
        var removed = 0;
        try
        {
            var dir = Dir(s);
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in Directory.GetFiles(dir, "*.wem", SearchOption.AllDirectories))
            {
                try { File.Delete(f); removed++; } catch { }
            }
            foreach (var d in Directory.GetDirectories(dir))
            {
                try { if (Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d); } catch { }
            }
        }
        catch { }
        return removed;
    }

    /// <summary>
    /// Write the conversion back over the source file, as a .wem beside it, and remove
    /// the original.
    ///
    /// Destructive on purpose and off by default: it means the next import is a plain
    /// file read with nothing to decode. The original is only deleted after the new
    /// file exists and reads back as the bytes we meant to write — losing someone's
    /// only copy of a master to save a few hundred milliseconds would be a bad trade.
    /// </summary>
    public static string ReplaceSource(string sourcePath, byte[] wem, out string error)
    {
        error = null;
        try
        {
            if (AudioDecode.IsWem(sourcePath))
            {
                // Same name, same extension: overwrite in place via a temporary.
                var tmpSame = sourcePath + ".new";
                File.WriteAllBytes(tmpSame, wem);
                if (!Verify(tmpSame, wem)) { Delete(tmpSame); error = "written file did not read back"; return null; }
                File.Move(tmpSame, sourcePath, overwrite: true);
                return sourcePath;
            }

            var dest = Path.ChangeExtension(sourcePath, ".wem");
            if (File.Exists(dest) && !string.Equals(dest, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                error = Path.GetFileName(dest) + " already exists";
                return null;
            }
            var tmp = dest + ".part";
            File.WriteAllBytes(tmp, wem);
            if (!Verify(tmp, wem)) { Delete(tmp); error = "written file did not read back"; return null; }
            File.Move(tmp, dest, overwrite: true);

            // Only now is the original expendable.
            File.Delete(sourcePath);
            return dest;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    private static bool Verify(string path, byte[] expected)
    {
        try { return File.ReadAllBytes(path).AsSpan().SequenceEqual(expected); }
        catch { return false; }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
