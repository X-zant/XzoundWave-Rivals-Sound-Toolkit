using System.IO;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.IO;
using UAssetTool.ZenPackage;

namespace MRAudioKit;

/// <summary>
/// The game's cooked assets as editable files: a package out of the game's IoStore
/// containers, converted to the legacy .uasset/.uexp pair that UAssetAPI edits, by
/// UAssetToolRivals' own converter.
///
/// The converter loads its own view of the containers, separate from CUE4Parse's, so a
/// package that exists in a base container and again in a patch could in principle come
/// out as the wrong copy. Every extraction is therefore checked against the copy the
/// <see cref="GameSession"/> resolves -- the one the game loads -- by export sizes, which
/// is what distinguished the two copies of 1024001_HeroVoice (41,922 vs 42,022 bytes).
/// </summary>
public sealed class GameAssetExtractor
{
    private readonly Settings _settings;
    private readonly GameSession _session;
    private FZenPackageContext _context;
    private readonly object _gate = new();

    public GameAssetExtractor(Settings settings, GameSession session)
    {
        _settings = settings;
        _session = session;
    }

    private FZenPackageContext Context(Action<string> progress)
    {
        lock (_gate)
        {
            if (_context is not null) return _context;
            progress?.Invoke("Loading the game's containers for asset extraction...");
            var ctx = new FZenPackageContext();
            ctx.SetAesKey(_settings.AesKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? _settings.AesKey[2..] : _settings.AesKey);
            // The same order UAssetToolRivals' own extraction uses: global.utoc for the
            // script objects, then every other container sorted by name, so patches
            // (Patch_*) load after the base chunks they override.
            var global = Path.Combine(_settings.PaksDir, "global.utoc");
            if (File.Exists(global))
            {
                ctx.LoadContainer(global);
                ctx.LoadScriptObjectsFromContainer(0);
            }
            foreach (var utoc in Directory.GetFiles(_settings.PaksDir, "*.utoc", SearchOption.TopDirectoryOnly)
                         .Where(f => !f.EndsWith("global.utoc", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f))
            {
                try { ctx.LoadContainer(utoc); }
                catch (Exception e) { progress?.Invoke($"  skipped {Path.GetFileName(utoc)}: {e.Message}"); }
            }
            return _context = ctx;
        }
    }

    /// <summary>
    /// Write the legacy pair for <paramref name="vfsPath"/> (as in
    /// Marvel/Content/Marvel/Characters/1024/1024001/1024001_VoiceItem.uasset) under
    /// <paramref name="outRoot"/> at the same relative path, and return the .uasset's path.
    /// </summary>
    public string Extract(string vfsPath, string outRoot, Action<string> progress = null)
    {
        var ctx = Context(progress);
        var id = ctx.FindPackageIdByVfsPath(vfsPath)
                 ?? throw new FileNotFoundException($"no container holds {vfsPath}");
        var bundle = new ZenToLegacyConverter(ctx, id).Convert();

        // The copy the game loads, per CUE4Parse and the session's read order.
        if (_session.Provider.Files.TryGetValue(vfsPath, out var file) && _session.Provider.LoadPackage(file) is IoPackage pkg)
        {
            var expected = pkg.ExportMap.Sum(e => (long)e.CookedSerialSize) + 4;   // + the legacy package tag
            if (bundle.ExportsData.Length != expected)
                throw new InvalidDataException(
                    $"{vfsPath}: the extracted copy's exports are {bundle.ExportsData.Length - 4:N0} B, " +
                    $"the copy the game loads has {expected - 4:N0} B; refusing to edit the wrong version");
        }

        var dest = Path.Combine(outRoot, vfsPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, bundle.AssetData);
        File.WriteAllBytes(Path.ChangeExtension(dest, ".uexp"), bundle.ExportsData);
        if (bundle.BulkData is { Length: > 0 }) File.WriteAllBytes(Path.ChangeExtension(dest, ".ubulk"), bundle.BulkData);
        return dest;
    }
}
