using System.IO;
using System.Security.Cryptography;
using System.Text;

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
///
/// And on the DECODER, because the source file is only half of what decides the bytes.
/// The same tone.mp3 encodes to a wem whose SHA-256 begins cfafed2e when Media
/// Foundation decoded it and fd9d6b11 when the bundled vgmstream did, so an entry
/// written by one decoder is not an answer to a question asked under another. Keyed on
/// the content alone, a machine that imported that file before the decoder changed went
/// on being served the old bytes for ever while a fresh install of the same build
/// produced the new ones: two people on one build shipped different mods, and the day
/// the first cleared their cache their own mod's bytes changed under them.
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

    /// <summary>SHA-256 of a file, as half of the cache key.</summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Where an entry lives: the source's own hash, sharded, and a digest of the decoder
    /// that produced it.
    ///
    /// The decoder is part of the NAME, so entries for one file under two decoders sit
    /// side by side and neither replaces the other. That is the whole reason to do it
    /// this way rather than emptying the directory whenever the decoder changes: a lookup
    /// under a decoder this cache has never seen cannot reach anything, which is a
    /// property of the layout, where invalidation is a property of a delete that has to
    /// have run and to have succeeded. It also needs nothing recorded about the entries
    /// already on disk, which is what the case that prompted it did not have: entries
    /// written before this existed carry no digest in their name, so nothing asks for
    /// them again, whether or not a sweep ever gets to them. And a reader who points
    /// Settings > Tools at their own vgmstream and back again keeps both sets rather than
    /// paying for a full re-encode in each direction.
    ///
    /// A digest rather than the identity itself because a revision string is free to
    /// contain anything, a file name is not, and twelve hex characters name more
    /// decoders than will ever exist.
    /// </summary>
    private static string EntryPath(Settings s, string hash, string decoder) =>
        Path.Combine(Dir(s), hash[..2], hash + "-" + Digest(decoder) + ".wem");

    private static string Digest(string decoder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(decoder ?? "")))
               .ToLowerInvariant()[..12];

    /// <summary>
    /// The conversion this decoder stored for these bytes, or null.
    /// </summary>
    /// <param name="decoder">
    /// What would decode the source now, from <see cref="AudioDecode.DecoderIdentity"/>.
    /// </param>
    public static byte[] Get(Settings s, string hash, string decoder)
    {
        if (s?.CacheConvertedAudio != true || hash is null) return null;
        try
        {
            var p = EntryPath(s, hash, decoder);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Store a conversion. Written to a temporary name and moved into place, so an
    /// interrupted write cannot leave a truncated entry that would later be served as
    /// if it were a real encode.
    /// </summary>
    public static void Put(Settings s, string hash, string decoder, byte[] wem)
    {
        if (s?.CacheConvertedAudio != true || hash is null || wem is null) return;
        try
        {
            var p = EntryPath(s, hash, decoder);
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
            // Same-file test on real paths, so it follows the filesystem's own rule.
            // Where case matters, A.wem and a.wem are two files, and treating them as
            // one would skip a write the author asked for.
            if (File.Exists(dest) && !AppPaths.SamePath(dest, sourcePath))
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
