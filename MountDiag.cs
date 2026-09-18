using System.IO;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace MRAudioKit;

/// <summary>
/// Reports what actually happened when a folder of mod containers was mounted: which
/// mounted, which did not, how many files each contributed and whether encryption is
/// in the way. "0 files" on its own never says which of fifteen mods failed.
/// </summary>
public static class MountDiag
{
    /// <summary>MRAudioKit.exe --mountdiag &lt;folder&gt;</summary>
    public static int Run(string dir)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();

        if (!Directory.Exists(dir)) { W("not a folder: " + dir); return 2; }

        var provider = new DefaultFileProvider(dir, SearchOption.TopDirectoryOnly,
            new VersionContainer(EGame.GAME_MarvelRivals), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        try { provider.SubmitKey(new FGuid(), new FAesKey(settings.AesKey)); }
        catch (Exception ex) { W("AES rejected: " + ex.Message); }

        W($"{dir}");
        W($"{provider.Files.Count:N0} files total");
        W("");

        // Which container each file came from, so a container that mounted but
        // contributed nothing is distinguishable from one that carried the content.
        var perVfs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bnkPerVfs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, file) in provider.Files)
        {
            var owner = file is CUE4Parse.UE4.VirtualFileSystem.VfsEntry v ? v.Vfs.Name : "(unknown)";
            perVfs[owner] = perVfs.GetValueOrDefault(owner) + 1;
            if (key.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
                bnkPerVfs[owner] = bnkPerVfs.GetValueOrDefault(owner) + 1;
        }

        W("MOUNTED:");
        foreach (var v in provider.MountedVfs.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            W($"  {v.Name,-56} {perVfs.GetValueOrDefault(v.Name),6:N0} files" +
              (bnkPerVfs.TryGetValue(v.Name, out var b) ? $"   {b} .bnk" : ""));

        W("");
        W("NOT MOUNTED:");
        foreach (var v in provider.UnloadedVfs.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            W($"  {v.Name,-56} encrypted={v.IsEncrypted} keyGuid={v.EncryptionKeyGuid}");

        var wem = provider.Files.Keys.Count(k => k.EndsWith(".wem", StringComparison.OrdinalIgnoreCase));
        var bnk = provider.Files.Keys.Count(k => k.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase));
        W("");
        W($"audio here: {bnk} .bnk, {wem} .wem");
        foreach (var k in provider.Files.Keys
                     .Where(k => k.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) ||
                                 k.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
                     .Take(20))
            W("  " + k);
        return 0;
    }
}
