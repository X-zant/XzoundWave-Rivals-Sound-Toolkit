using System.IO;

namespace MRAudioKit;

/// <summary>
/// Where a loose media file lives, and whether an id is reachable both ways.
///
/// Both questions matter for a Media-folder mod: the file has to land in the folder
/// the game looks in, and an id that exists in a bank AND as a loose file can be
/// modded either way -- which is a choice only the author can make.
/// </summary>
public static class MediaLayout
{
    public const string Root = "Marvel/Content/WwiseAudio/Media/";

    /// <summary>
    /// The bucket folder for a media id. Verified against every loose file the game
    /// ships rather than assumed from a handful: it is the first two characters of
    /// the decimal id, which for a one-digit id is simply that digit.
    /// </summary>
    public static string Bucket(uint mediaId)
    {
        var s = mediaId.ToString();
        return s.Length >= 2 ? s[..2] : s;
    }

    /// <summary>The path a loose media file takes inside a pak.</summary>
    public static string PathFor(uint mediaId, string language = null) =>
        string.IsNullOrEmpty(language)
            ? $"{Root}{Bucket(mediaId)}/{mediaId}.wem"
            : $"{Root}{language}/{Bucket(mediaId)}/{mediaId}.wem";

    /// <summary>XzoundWave.exe --medialayout — prove the rule and count the overlap.</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });
        session.IndexBanks(_ => { });

        int total = 0, matches = 0, langScoped = 0;
        var counterExamples = new List<string>();

        foreach (var key in session.Provider.Files.Keys)
        {
            if (!key.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)) continue;
            if (!key.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = key[Root.Length..].Split('/');
            if (rest.Length < 2) continue;
            var leaf = Path.GetFileNameWithoutExtension(rest[^1]);
            if (!uint.TryParse(leaf, out var id)) continue;

            total++;
            var folder = rest[^2];
            if (rest.Length == 3) langScoped++;

            if (folder.Equals(Bucket(id), StringComparison.Ordinal)) matches++;
            else if (counterExamples.Count < 10) counterExamples.Add($"{folder} != {Bucket(id)}   {key}");
        }

        W($"loose media files: {total:N0}  ({langScoped:N0} under a language folder)");
        W($"folder == first two digits of the id: {matches:N0}/{total:N0}" +
          (total > 0 ? $"  ({100.0 * matches / total:0.00}%)" : ""));
        if (counterExamples.Count > 0)
        {
            W("counter-examples:");
            foreach (var c in counterExamples) W("  " + c);
        }
        else W("  no counter-examples — the rule holds for every shipped file.");

        // Can an id be modded either way? That decides whether the author has to choose.
        var inBank = session.MediaBank.Keys.ToHashSet();
        var loose = new HashSet<uint>();
        foreach (var key in session.Provider.Files.Keys)
        {
            if (!key.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)) continue;
            if (!key.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) continue;
            if (uint.TryParse(Path.GetFileNameWithoutExtension(key), out var id)) loose.Add(id);
        }
        var both = loose.Count(inBank.Contains);

        // Can one id sit at more than one path? That decides whether a Media mod can
        // simply mirror where the game ships a file, or has to ask which copy is meant.
        var paths = new Dictionary<uint, List<string>>();
        foreach (var key in session.Provider.Files.Keys)
        {
            if (!key.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)) continue;
            if (!key.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) continue;
            if (!uint.TryParse(Path.GetFileNameWithoutExtension(key), out var id)) continue;
            if (!paths.TryGetValue(id, out var list)) paths[id] = list = [];
            list.Add(key);
        }
        var multi = paths.Where(kv => kv.Value.Count > 1).ToList();
        Console.WriteLine();
        Console.WriteLine($"distinct loose ids : {paths.Count:N0}");
        Console.WriteLine($"ids at >1 path     : {multi.Count:N0}");
        foreach (var kv in multi.Take(3))
        {
            Console.WriteLine($"  {kv.Key}:");
            foreach (var v in kv.Value.Take(4)) Console.WriteLine($"    {v}");
        }

        // Which folders hold them, and what is in the ones with no language at all.
        var byLang = new Dictionary<string, int>();
        foreach (var kv in paths)
            foreach (var v in kv.Value)
            {
                var rest = v[Root.Length..].Split('/');
                var lang = rest.Length >= 3 ? rest[0] : "(no language folder)";
                byLang[lang] = byLang.GetValueOrDefault(lang) + 1;
            }
        Console.WriteLine();
        Console.WriteLine("files per top-level folder under Media/:");
        foreach (var kv in byLang.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Value,8:N0}  {kv.Key}");
        W("");
        W($"ids in a bank      : {inBank.Count:N0}");
        W($"ids as loose media : {loose.Count:N0}");
        W($"ids BOTH ways      : {both:N0}  ({(loose.Count > 0 ? 100.0 * both / loose.Count : 0):0.0}% of loose)");
        W($"loose only         : {loose.Count - both:N0}");
        var bothWays = loose.Where(inBank.Contains).OrderBy(x => x).Where((_, i) => i % 9000 == 0).Take(6).ToList();
        if (bothWays.Count > 0)
            W($"  both-ways examples : {string.Join(", ", bothWays)}");
        var looseOnly = loose.Where(id => !inBank.Contains(id)).OrderBy(x => x).Take(5).ToList();
        if (looseOnly.Count > 0)
            W($"  examples           : {string.Join(", ", looseOnly)}");
        return 0;
    }
}
