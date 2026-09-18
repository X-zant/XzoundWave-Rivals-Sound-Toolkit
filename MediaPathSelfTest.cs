using System.IO;

namespace MRAudioKit;

/// <summary>
/// Proves that a staged file lands exactly where the game ships that sound, rather
/// than at a path derived from a rule.
///
/// The distinction matters: 77,209 of the game's 80,664 loose media files live under
/// a language folder, and only 3,455 sit directly under Media/##/. A tool that applied
/// the bucket rule to everything would put 95.7% of files in the wrong place.
/// </summary>
public static class MediaPathSelfTest
{
    /// <summary>MRAudioKit.exe --mediapath [samplesPerFolder]</summary>
    public static int Run(int sample = 400)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        var fails = 0;
        const string root = MediaLayout.Root;

        // Every shipped loose file, grouped by the folder it actually sits in.
        var byFolder = new Dictionary<string, List<(uint Id, string Path)>>();
        foreach (var key in session.Provider.Files.Keys)
        {
            if (!key.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)) continue;
            if (!key.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (!uint.TryParse(Path.GetFileNameWithoutExtension(key), out var id)) continue;
            var rest = key[root.Length..].Split('/');
            var folder = rest.Length >= 3 ? rest[0] : "(no language folder)";
            if (!byFolder.TryGetValue(folder, out var list)) byFolder[folder] = list = [];
            list.Add((id, key));
        }

        W($"session language: {settings.Language}");
        W("");
        W($"resolving up to {sample:N0} ids from each folder back to their shipped path");

        foreach (var (folder, files) in byFolder.OrderByDescending(k => k.Value.Count))
        {
            var checkedCount = 0;
            var wrong = new List<string>();
            var missing = new List<uint>();

            // Spread the sample across the folder instead of taking the first N, which
            // would only ever exercise one bucket.
            var step = Math.Max(1, files.Count / sample);
            for (var i = 0; i < files.Count; i += step)
            {
                var (id, truePath) = files[i];
                checkedCount++;
                var found = session.LooseMedia(id);
                if (found is null) { missing.Add(id); continue; }
                if (!found.Path.Equals(truePath, StringComparison.OrdinalIgnoreCase)
                    && wrong.Count < 5)
                    wrong.Add($"{id}: got {found.Path}, ships at {truePath}");
            }

            var ok = wrong.Count == 0 && missing.Count == 0;
            W($"  {(ok ? "ok  " : "FAIL")}  {folder,-22} {checkedCount:N0} of {files.Count:N0} checked");
            foreach (var w in wrong) W($"          {w}");
            if (missing.Count > 0) W($"          {missing.Count} id(s) not found at all, e.g. {missing[0]}");
            if (!ok) fails++;
        }

        // The language folder is the part a rule cannot supply, so state plainly that
        // resolution does not depend on which language the session happens to be in.
        W("");
        W("a file is found whatever language the session is set to");
        var others = byFolder.Keys.Where(k => k != "(no language folder)"
                                           && !k.Equals(settings.Language, StringComparison.OrdinalIgnoreCase));
        foreach (var folder in others)
        {
            var (id, truePath) = byFolder[folder][0];
            var found = session.LooseMedia(id);
            var ok = found?.Path.Equals(truePath, StringComparison.OrdinalIgnoreCase) == true;
            W($"  {(ok ? "ok  " : "FAIL")}  {folder}: {id} -> {found?.Path ?? "not found"}");
            if (!ok) fails++;
        }

        // And the bucket rule, which is only ever the fallback, must survive a short id.
        W("");
        W("the fallback used when the game ships no loose original");
        foreach (var id in new uint[] { 7u, 42u, 12345u, 999999999u })
        {
            var path = MediaLayout.PathFor(id);
            var expected = $"{MediaLayout.Root}{id.ToString()[..Math.Min(2, id.ToString().Length)]}/{id}.wem";
            var ok = path == expected;
            W($"  {(ok ? "ok  " : "FAIL")}  {id,-10} -> {path}");
            if (!ok) fails++;
        }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
