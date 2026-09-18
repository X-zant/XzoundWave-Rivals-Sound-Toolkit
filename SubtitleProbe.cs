using System.IO;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.Core.i18N;

namespace MRAudioKit;

/// <summary>
/// Why some sounds show no subtitle. Compares the events a skin's banks actually
/// contain against the events its voice table describes, and hunts for other tables
/// that might cover the difference.
/// </summary>
public static class SubtitleProbe
{
    /// <summary>MRAudioKit.exe --subprobe &lt;skinId&gt;</summary>
    public static int Run(string skinId)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        var charId = skinId[..4];
        var skin = session.Characters.First(c => c.CharId == charId).Skins.First(s => s.SkinId == skinId);
        var sounds = SoundIndex.Build(session, skin, charId, _ => { });

        var named = sounds.Rows.Where(r => r.EventName is not null).ToList();
        var withText = named.Where(r => !string.IsNullOrEmpty(r.Subtitle)).ToList();
        W($"{sounds.Rows.Count} rows, {named.Count} with an event name, {withText.Count} with a subtitle");

        // Only VO banks carry dialogue; sfx and display rows having no subtitle is
        // correct, and mixing them in makes the coverage look far worse than it is.
        var vo = named.Where(r => r.Bank.Contains("_vo_", StringComparison.OrdinalIgnoreCase)).ToList();
        var voText = vo.Count(r => !string.IsNullOrEmpty(r.Subtitle));
        W($"VO rows only: {voText}/{vo.Count} have a subtitle" +
          (vo.Count > 0 ? $"  ({100.0 * voText / vo.Count:0.0}%)" : ""));

        // Every table the character could possibly draw from.
        var tables = session.Provider.Files.Keys
            .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                        k.Contains("Voice", StringComparison.OrdinalIgnoreCase) &&
                        (k.Contains($"/{charId}/", StringComparison.OrdinalIgnoreCase) ||
                         k.Contains("HeroVoice", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        W("");
        W($"{tables.Count} candidate voice table(s) reachable:");
        foreach (var t in tables.Take(40)) W("  " + t);

        // What does each one actually cover, of the events this skin plays?
        var wanted = named.Select(r => r.EventName).Distinct()
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        W("");
        W("coverage of this skin's events, per table:");
        foreach (var t in tables)
        {
            var events = EventsIn(session, t, out var rows);
            if (rows == 0) continue;
            var hit = events.Count(e => wanted.Contains(e));
            W($"  {hit,5} / {wanted.Count}   ({rows} rows, {events.Count} events)   {Path.GetFileName(t)}");
        }

        var missing = named.Where(r => string.IsNullOrEmpty(r.Subtitle))
                           .Select(r => r.EventName).Distinct().Take(15).ToList();
        W("");
        W($"examples with no subtitle ({missing.Count} of many):");
        foreach (var m in missing) W("  " + m);

        // Where do the boss / NPC / system banks get their text from, if anywhere?
        W("");
        W("non-character VO banks, against EVERY voice table:");
        var all = session.Provider.Files.Keys
            .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                        (k.Contains("HeroVoice", StringComparison.OrdinalIgnoreCase) ||
                         k.EndsWith("/MarvelHeroVoiceTable.uasset", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var t0 = DateTime.UtcNow;
        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in all)
            foreach (var ev in EventsIn(session, t, out _)) global.TryAdd(ev, t);
        W($"  {all.Count} tables hold {global.Count:N0} events, read in {(DateTime.UtcNow - t0).TotalSeconds:N1}s");

        foreach (var probe in new[] { "bnk_vo_npc", "bnk_vo_system", "bnk_vo_boss_vampirepve_dracula",
                                      "bnk_vo_tutorial_level", "bnk_vo_effect" })
        {
            var entry = session.OtherBanks.SelectMany(g => g.Skins)
                .FirstOrDefault(x => x.SkinId.Equals(probe, StringComparison.OrdinalIgnoreCase));
            if (entry is null) { W($"  {probe,-34} not catalogued"); continue; }
            var rows = SoundIndex.Build(session, entry, "", _ => { });
            var evs = rows.Rows.Where(r => r.EventName is not null).ToList();
            var withText2 = evs.Count(r => !string.IsNullOrEmpty(r.Subtitle));
            W($"  {probe,-34} {evs.Count,4} named, {withText2,4} now carry a subtitle");
        }

        // Is the remaining gap a MISSING row, or a row the game ships with no text?
        var blanks = named.Where(r => string.IsNullOrEmpty(r.Subtitle) &&
                                      r.Bank.Contains("_vo_", StringComparison.OrdinalIgnoreCase))
                          .ToList();
        int described = 0, absent = 0;
        foreach (var r in blanks)
        {
            if (session.VoiceLine(r.EventName) is not null) described++; else absent++;
        }
        W("");
        W($"VO rows with no subtitle: {blanks.Count}");
        W($"  {described} ARE in a voice table, but the game ships no line for them");
        W($"  {absent} are in no voice table at all");
        foreach (var r in blanks.Take(6)) W($"    {r.EventName}");

        var report = Path.Combine(Path.GetTempPath(), "MRAudioKit", "subtitles.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, sb.ToString(), new UTF8Encoding(true));
        Console.WriteLine("wrote " + report);
        return 0;
    }

    private static HashSet<string> EventsIn(GameSession session, string assetPath, out int rows)
    {
        rows = 0;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pkg = assetPath[..^".uasset".Length];
            var dt = session.Provider.LoadPackageObject<UDataTable>(pkg + "." + Path.GetFileName(pkg));
            if (dt is null) return set;
            foreach (var (_, row) in dt.RowMap)
            {
                rows++;
                var ev = row.GetOrDefault<FPackageIndex>("Event")?.Name;
                if (!string.IsNullOrEmpty(ev)) set.Add(ev);
            }
        }
        catch { }
        return set;
    }
}
