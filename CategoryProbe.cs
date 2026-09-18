using System.IO;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.Core.i18N;

namespace MRAudioKit;

/// <summary>
/// Works out what the Category column actually is, and whether the game ships a
/// translation for it anywhere -- a derived mapping beats a dictionary typed out by
/// hand, which would go stale the moment a patch adds a category.
/// </summary>
public static class CategoryProbe
{
    /// <summary>MRAudioKit.exe --catprobe &lt;skinId&gt;</summary>
    public static int Run(string skinId)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        var charId = skinId[..4];
        var hit = session.Provider.Files.Keys.FirstOrDefault(k =>
            k.EndsWith($"/{skinId}_HeroVoice.uasset", StringComparison.OrdinalIgnoreCase))
            ?? session.Provider.Files.Keys.FirstOrDefault(k =>
            k.EndsWith($"/{charId}001_HeroVoice.uasset", StringComparison.OrdinalIgnoreCase));
        if (hit is null) { W("no HeroVoice table"); return 1; }
        W("table: " + hit);

        var pkg = hit[..^".uasset".Length];
        var dt = session.Provider.LoadPackageObject<UDataTable>(pkg + "." + Path.GetFileName(pkg));

        // What columns does a row actually carry? If a translated category exists at
        // all, it is most likely a sibling field we are simply not reading.
        var first = dt.RowMap.Values.FirstOrDefault();
        if (first is not null)
        {
            W("");
            W("columns on a row:");
            foreach (var prop in first.Properties)
                W($"  {prop.Name,-28} {prop.Tag?.GenericValue?.GetType().Name}");
        }

        var cats = new Dictionary<string, int>();
        foreach (var (_, row) in dt.RowMap)
        {
            var c = row.GetOrDefault<FName>("Category").Text;
            if (!string.IsNullOrEmpty(c)) cats[c] = cats.GetValueOrDefault(c) + 1;
        }
        W("");
        W($"{cats.Count} distinct categories:");
        foreach (var (c, n) in cats.OrderByDescending(x => x.Value))
            W($"  {n,4}x  {c}");

        // Does any of these strings appear in the localization data, so an English
        // form could be looked up rather than invented?
        W("");
        W("looking for these strings in the shipped localization...");
        var found = 0;
        foreach (var c in cats.Keys)
        {
            var en = session.Loc("", c);
            if (!string.IsNullOrEmpty(en)) { W($"  {c} -> {en}"); found++; }
        }
        W(found == 0
            ? "  none of them are localization keys, so the game ships no translation for this column."
            : $"  {found} resolved.");

        W("");
        W("translated:");
        var untouched = 0;
        foreach (var (c, n) in cats.OrderByDescending(x => x.Value))
        {
            var t = CategoryTranslator.Translate(session, c);
            if (CategoryTranslator.NeedsTranslation(t)) untouched++;
            W($"  {n,4}x  {c,-24} -> {t}");
        }
        W("");
        W($"{cats.Count - untouched}/{cats.Count} fully translated, {untouched} still carry Chinese.");

        // The console mangles CJK; the file is the readable copy.
        var report = Path.Combine(Path.GetTempPath(), "MRAudioKit", "categories.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, sb.ToString(), new UTF8Encoding(true));
        Console.WriteLine("wrote " + report);
        return 0;
    }
}
