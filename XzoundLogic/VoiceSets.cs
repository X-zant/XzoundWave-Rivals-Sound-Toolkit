using CUE4Parse.UE4.Assets.Exports.Engine;
using Newtonsoft.Json.Linq;

namespace MRAudioKit;

/// <summary>
/// One voice set: the HeroVoice table (lines, subtitles, events) and the VoiceItem(s)
/// (what triggers them) that a group of skins share.
/// </summary>
/// <param name="TablePackage">as in /Game/Marvel/Characters/1024/1024001/1024001_HeroVoice</param>
/// <param name="ItemPackages">one per sculpt, usually one</param>
/// <param name="Sculpts">the hero's forms (sculpts) as the skins state them; an entry keyed by a
/// hero names one of these as its SculptID (Loki's kill lines are keyed for 0 and 1 alike)</param>
public sealed record VoiceSet(string HeroId, string HeroName, string TablePackage,
                              IReadOnlyList<string> ItemPackages, IReadOnlyList<int> SkinIds,
                              IReadOnlyList<int> Sculpts = null)
{
    /// <summary>The set the hero's default skin (SkinId 1) uses.</summary>
    public bool IsDefault => SkinIds.Contains(1);

    public string Key => TablePackage + "|" + string.Join(",", ItemPackages);

    public string Label => IsDefault
        ? HeroName
        : $"{HeroName}, skin{(SkinIds.Count > 1 ? "s" : "")} {string.Join(" / ", SkinIds)}";

    /// <summary>Cooked asset path of a package, as in Marvel/Content/Marvel/.../X.uasset.</summary>
    public static string VfsPath(string package) =>
        package.StartsWith("/Game/", StringComparison.Ordinal)
            ? "Marvel/Content/" + package["/Game/".Length..] + ".uasset"
            : throw new ArgumentException($"not a /Game/ package: {package}");
}

/// <summary>
/// Every voice set in the game, read from the hero skin table: each skin names its
/// VoiceTable, and each of its sculpts names a VoiceItem. Most skins share their hero's
/// default pair; 13 skin entries (Loki's 502/504/505/506, Strange 800 and others) carry a
/// set of their own, which is edited in place like any hero's -- the table here is only
/// READ, to find the sets. Nothing writes the skin table: a set lives entirely in its own
/// assets, so a voice mod stays inside the hero or skin it is for.
/// </summary>
public static class VoiceSets
{
    public const string SkinTable = "Marvel/Content/Marvel/Data/DataTable/MarvelHeroSkinTable.MarvelHeroSkinTable";

    public static List<VoiceSet> Discover(GameSession session)
    {
        var skins = session.Provider.LoadPackageObject<UDataTable>(SkinTable);
        var sets = new Dictionary<string, (string hero, string table, List<string> items, List<int> skinIds, SortedSet<int> sculpts)>();
        foreach (var (key, row) in skins.RowMap)
        {
            var specsProp = row.Properties.FirstOrDefault(p => p.Name.Text == "SkinSpecs");
            if (specsProp?.Tag?.GenericValue is null) continue;
            foreach (var s in JArray.FromObject(specsProp.Tag.GenericValue))
            {
                var table = Package(s["VoiceTable"]);
                if (table is null) continue;
                var items = s["SculptSpecs"]?.Select(x => Package(x["VoiceItem"])).Where(x => x is not null).Distinct().ToList() ?? [];
                if (items.Count == 0) continue;
                var setKey = table + "|" + string.Join(",", items);
                if (!sets.TryGetValue(setKey, out var set))
                    sets[setKey] = set = (key.Text, table, items, [], []);
                set.skinIds.Add((int)s["SkinId"]);
                var i = 0;
                foreach (var sculpt in s["SculptSpecs"] ?? new JArray())
                    set.sculpts.Add((int?)(sculpt["SculptId"] ?? sculpt["SculptID"]) ?? i++);
            }
        }
        return sets.Values
            .Select(x => new VoiceSet(x.hero, session.HeroName(x.hero), x.table, x.items, x.skinIds.OrderBy(i => i).ToList(),
                                      x.sculpts.Count > 0 ? x.sculpts.ToList() : [0]))
            .OrderBy(v => v.HeroName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.IsDefault ? 0 : 1)
            .ThenBy(v => v.SkinIds.FirstOrDefault())
            .ToList();
    }

    // "/Game/Marvel/Characters/1024/1024001/1024001_HeroVoice.1024001_HeroVoice" -> the package part
    private static string Package(JToken softPath)
    {
        var p = (string)softPath?["AssetPathName"];
        if (string.IsNullOrEmpty(p) || p == "None") return null;
        var dot = p.LastIndexOf('.');
        return dot > p.LastIndexOf('/') ? p[..dot] : p;
    }
}
