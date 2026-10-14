using System.IO;

namespace MRAudioKit;

/// <summary>The command-line side of <see cref="GameHistory"/>.</summary>
public static class HistoryVerbs
{
    /// <summary>
    /// XzoundWave.exe --snapshot [paksDir] [--history dir]: record this version of the game, or the
    /// one in another Paks folder (an older install someone kept), unless it is already recorded.
    /// </summary>
    public static int Snapshot(string paksDir, string dir)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        if (paksDir is not null) settings.PaksDir = paksDir;
        var t0 = DateTime.UtcNow;
        var session = new GameSession();
        session.Mount(settings, _ => { });
        W($"mounted {settings.PaksDir} in {(DateTime.UtcNow - t0).TotalSeconds:N0}s");

        var t1 = DateTime.UtcNow;
        var taken = GameHistory.EnsureSnapshot(session, paksDir is null ? "this install" : paksDir, dir);
        var (version, _) = GameSnapshot.VersionOf(session.PaksDir);
        var path = GameHistory.PathFor(version, session.BankFingerprint(), dir);
        if (taken is null) { W($"already recorded: {path}"); return 0; }
        W($"{taken.Label} (bank set {taken.Fingerprint}): {taken.Media.Count:N0} media played by " +
          $"{taken.Media.Values.Sum(p => p.Length):N0} Sounds and music tracks in {taken.Banks.Count:N0} banks, " +
          $"in {(DateTime.UtcNow - t1).TotalSeconds:N0}s");
        W($"saved {path}  ({new FileInfo(path).Length:N0} B)");
        return 0;
    }

    /// <summary>XzoundWave.exe --history [dir]: what an update can read old media ids against.</summary>
    public static int List(string dir)
    {
        void W(string s) => Console.WriteLine(s);
        var history = dir is null ? GameHistory.Load() : GameHistory.Load(dir);
        W($"{history.Snapshots.Count} game version(s):");
        foreach (var s in history.Snapshots.OrderBy(s => s.Changelist).ThenBy(s => s.Taken))
            W($"  {s.Label,-24} bank set {s.Fingerprint}  {s.Media.Count,7:N0} media  taken {s.Taken:yyyy-MM-dd} from {s.Source}");
        W($"{history.Subs.Count} SoundKit list(s):");
        foreach (var s in history.Subs.OrderBy(s => s.Label))
            W($"  {s.Label,-24} {s.Language,-14} {s.Lines.Count,7:N0} voice lines");
        foreach (var p in history.Problems) W("  problem: " + p);
        return 0;
    }

    /// <summary>XzoundWave.exe --importsubs &lt;folder&gt; [--history dir]: SoundKit's old subtitle lists, copied in.</summary>
    public static int ImportSubs(string folder, string dir)
    {
        var found = GameHistory.ImportSubs(folder, dir);
        Console.WriteLine(found.Count == 0
            ? $"no SoundKit subtitle lists under {folder}"
            : $"imported {found.Count} list(s): {string.Join(", ", found)}");
        return found.Count == 0 ? 1 : 0;
    }
}
