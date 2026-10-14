using System.Collections;
using System.IO;

namespace MRAudioKit;

/// <summary>
/// A staged folder as the IoStore bundle the game loads: .utoc/.ucas holding the cooked
/// packages, and the companion .pak holding everything else (the banks) plus /chunknames.
/// This is UAssetToolRivals' own create_mod_iostore, called in-process -- the same code the
/// command runs, measured to give the same packages and a byte-identical pak.
/// </summary>
public static class ModPackager
{
    /// <param name="outputBase">path and name without extension; a _P suffix makes it load as a patch</param>
    public static (bool Ok, string Message) Package(string stage, string outputBase)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputBase))!);
        var r = UAssetTool.Program.CreateModIoStoreJson(
            outputPath: outputBase, inputDir: stage, inputPak: null, mountPoint: null,
            compress: true, aesKey: null, parallel: true, obfuscate: false, hybrid: true);
        // A file it could not convert or read is skipped and listed, and the bundle is still
        // written and called a success. Every staged file is one the mod needs -- a missing
        // VoiceItem or table is a line that never plays -- so a skipped file fails the build.
        if (r.Success && r.Data is IDictionary data && data["errors"] is IEnumerable errors and not string)
        {
            var list = errors.Cast<object>().Select(e => e?.ToString()).ToList();
            if (list.Count > 0)
                return (false, $"{r.Message}, but {list.Count} file(s) were left out: {string.Join("; ", list.Take(5))}");
        }
        return (r.Success, r.Message);
    }

    /// <summary>
    /// Files only -- rebuilt banks, loose media -- as one .pak, the way create_pak writes it
    /// (UAssetToolRivals' PakWriter, its own mount point and key). A mod that adds no cooked
    /// asset needs nothing more; the IoStore bundle is for one that does.
    /// </summary>
    /// <param name="root">the folder the files were written under, mirroring the game's layout</param>
    /// <param name="files">the files to pack, relative to <paramref name="root"/> (Marvel/Content/...)</param>
    public static (bool Ok, string Message) PackagePak(string root, IReadOnlyCollection<string> files, string outputBase)
    {
        if (files.Count == 0) return (false, "nothing to pack");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputBase))!);
        using (var pak = new UAssetTool.IoStore.PakWriter())
        {
            foreach (var rel in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                pak.AddEntry(rel.Replace('\\', '/'), File.ReadAllBytes(Path.Combine(root, rel)));
            pak.Write(outputBase + ".pak");
        }
        return (true, $"{files.Count} file(s) in {Path.GetFileName(outputBase)}.pak");
    }
}
