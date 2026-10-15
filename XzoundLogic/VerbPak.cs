using System.IO;
using System.Text.RegularExpressions;

namespace MRAudioKit;

/// <summary>
/// The .pak a command-line build leaves beside the folder it wrote.
///
/// The window has always finished a build as a .pak: it builds where nothing ships from and
/// packs what it wrote, because a .pak is what goes into ~mods. The verbs stopped one step
/// short, at a Marvel folder, on the reasoning that whoever runs a command has repak to hand.
/// On Windows they usually do; the Linux release is this command line and nothing else, so
/// there it meant a build that could not be installed without a second tool. Each verb now
/// packs the mod files it wrote into a .pak in its output folder, named as the window names
/// the same kind of output, and keeps the Marvel folder and its logs where they always were,
/// because the self-tests and the people who check a build by hand read them there.
/// </summary>
public static class VerbPak
{
    /// <summary>
    /// What the window calls a mod when the project has no name of its own -- and the command
    /// line has no project at all, so every build it names after the project gets this
    /// (MainWindow.Project.cs, ModBaseName).
    /// </summary>
    public const string DefaultName = "XzoundMod";

    /// <summary>The suffix that makes a pak load after the game's own, as the window writes it.</summary>
    public const string PatchSuffix = "_9999999_P";

    /// <summary>
    /// A project-named build: XzoundMod_9999999_P for a build, XzoundMod_Test_9999999_P for a
    /// test build, XzoundMod_Vol-0.8_9999999_P for a level. The window's ModBaseName with the
    /// default project name, the tag between the name and the patch suffix.
    /// </summary>
    public static string ProjectName(string tag = null) =>
        DefaultName + (string.IsNullOrEmpty(tag) ? "" : "_" + SafeName(tag)) + PatchSuffix;

    /// <summary>
    /// A merge, named after both mods without the patch suffix either may already carry, as
    /// "Merge mods..." names it (MainWindow.Mods.cs, ModMergeAsync).
    /// </summary>
    public static string MergeName(string priority, string secondary) =>
        SafeName($"{Stem(priority)}+{Stem(secondary)}").Replace(' ', '_') + PatchSuffix;

    /// <summary>An updated mod, named after the mod with _updated, as "Update mod..." names it (ModUpdateAsync).</summary>
    public static string UpdateName(string mod) =>
        SafeName(Stem(mod)).Replace(' ', '_') + "_updated" + PatchSuffix;

    /// <summary>
    /// A mod's name without its extension and without the _NNN_P a pak carries to load as a
    /// patch. The window only ever picks a file here; a verb also takes a folder, whose name is
    /// the whole of it, dots and all, and a picked folder can end in a separator that leaves
    /// it no name. A name that comes out empty is the default one rather than a pak called
    /// "_updated_9999999_P".
    ///
    /// A folder is named as it is on disk, not as it was typed: run from inside a mod, "." or
    /// ".." is the folder, and its name as typed is a dot, which made a pak called
    /// "._updated_9999999_P" -- hidden on Linux, where that is how a command is most often given.
    /// </summary>
    public static string Stem(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path ?? "");
        var name = Directory.Exists(trimmed) ? Path.GetFileName(Path.GetFullPath(trimmed))
                                             : Path.GetFileNameWithoutExtension(trimmed);
        name = Regex.Replace(name, @"_\d+_P$", "", RegexOptions.IgnoreCase);
        return name.Length == 0 ? DefaultName : name;
    }

    /// <summary>A name a file system will take, the way the window makes one (ModSafeName).</summary>
    public static string SafeName(string name)
    {
        name ??= "";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    /// <summary>The mod files under a folder, by path from it, with when each was last written and its size.</summary>
    public sealed record Tree(IReadOnlyDictionary<string, (DateTime Written, long Size)> Files);

    /// <summary>
    /// The Marvel tree a folder holds right now, for <see cref="WrittenSince"/> to tell this run's
    /// files from an earlier run's. Only the Marvel folder is read: the output can also hold a
    /// merge's _extracted copies or an update's _unplaced files, which are never part of a mod.
    ///
    /// Keyed by this system's rule for real paths, not the pak's: on Linux English/1.wem and
    /// english/1.wem are two files, and folded into one key each would be compared with the
    /// other's time and size, so an earlier run's copy this run never touched read as rewritten
    /// and went into the pak in place of the one it did write.
    /// </summary>
    public static Tree Snapshot(string outDir)
    {
        var files = new Dictionary<string, (DateTime, long)>(AppPaths.Comparer);
        foreach (var f in MarvelFiles(outDir))
        {
            var info = new FileInfo(f);
            files[Path.GetRelativePath(outDir, f)] = (info.LastWriteTimeUtc, info.Length);
        }
        return new Tree(files);
    }

    /// <summary>
    /// The mod files a run wrote under outDir: every file under its Marvel folder that was not
    /// there before the run, or was rewritten by it.
    ///
    /// Asked of the folder rather than of the builder, because the volume, test and merge
    /// builders say what they wrote only in their logs. And not simply every file in the folder:
    /// these verbs write into a folder they do not empty first, so a second run with another
    /// bank filter finds the first run's banks still there, and packing those would ship a level
    /// or a merge nobody asked for this time.
    /// </summary>
    public static List<string> WrittenSince(string outDir, Tree before) =>
        MarvelFiles(outDir)
            .Select(f => (Rel: Path.GetRelativePath(outDir, f), Info: new FileInfo(f)))
            .Where(x => !before.Files.TryGetValue(x.Rel, out var was) ||
                        was.Written != x.Info.LastWriteTimeUtc || was.Size != x.Info.Length)
            .Select(x => x.Rel)
            .ToList();

    private static IEnumerable<string> MarvelFiles(string outDir) =>
        !Directory.Exists(outDir) ? []
        : Directory.EnumerateDirectories(outDir)
                   .Where(d => Path.GetFileName(d).Equals("Marvel", StringComparison.OrdinalIgnoreCase))
                   .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories));

    /// <summary>
    /// Pack these mod files, relative to outDir, into outDir/name.pak and say so: the path and
    /// size on success, why on failure. A run that wrote nothing gets no pak -- an empty one
    /// would install as a mod that does nothing, which reads in game as a build that failed.
    /// </summary>
    /// <returns>False only when packing failed; nothing to pack is said, and is not a failure of packing.</returns>
    public static bool Pack(string outDir, string name, IEnumerable<string> files, Action<string> say)
    {
        // Only what sits under Marvel/, whatever the caller handed over: the logs, legends and
        // reports a build writes beside its files describe the mod and are never part of it.
        var mod = files.Where(f => f.Replace('\\', '/').StartsWith("Marvel/", StringComparison.OrdinalIgnoreCase))
                       .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // In full, and in this system's separators whatever the folder was typed with, since the
        // path is said for someone to copy.
        var outputBase = Path.GetFullPath(Path.Combine(outDir, name));
        if (mod.Count == 0)
        {
            say($"pak: none -- no mod file was written, so there is nothing to pack" +
                (File.Exists(outputBase + ".pak")
                    ? $" (the {name}.pak already in {Path.GetDirectoryName(outputBase)} is an earlier run's, left as it was)" : "") +
                ".");
            return true;
        }

        // The pak is written in place, under the very name a good one has, so a write that fails
        // partway leaves a pak that installs and plays as broken. What was there before is noted
        // to tell that from an earlier run's pak this attempt never reached, which is left alone.
        var pak = outputBase + ".pak";
        DateTime? had = File.Exists(pak) ? File.GetLastWriteTimeUtc(pak) : null;
        (bool Ok, string Message) packed;
        try { packed = ModPackager.PackagePak(outDir, mod, outputBase); }
        catch (Exception ex) { packed = (false, ex.Message); }
        if (!packed.Ok)
        {
            var left = "";
            if (File.Exists(pak) && File.GetLastWriteTimeUtc(pak) != had)
            {
                try { File.Delete(pak); left = "; what was written of it was removed"; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { left = $"; what was written of it could not be removed, so do not install {pak}"; }
            }
            else if (File.Exists(pak)) left = $"; the {name}.pak already there is an earlier run's, left as it was";
            say($"PACK FAILED: {name}.pak: {(left.Length > 0 ? packed.Message.TrimEnd('.') : packed.Message)}{left}");
            return false;
        }

        // A bundle an earlier build left under the same name would load its .utoc beside this
        // .pak; this mod is the .pak alone. The window removes it for the same reason.
        var stale = new[] { ".utoc", ".ucas" }.Where(e => File.Exists(outputBase + e)).ToList();
        foreach (var e in stale) File.Delete(outputBase + e);
        say($"pak: {outputBase}.pak  ({new FileInfo(outputBase + ".pak").Length:N0} B, {mod.Count} file(s))" +
            (stale.Count > 0 ? $"; removed the {string.Join("/", stale)} an earlier build left under that name" : ""));
        return true;
    }
}
