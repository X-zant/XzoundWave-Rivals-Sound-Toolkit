using System.IO;
using System.Text.RegularExpressions;

namespace MRAudioKit;

/// <summary>
/// Columns whose text contains a number have to sort by the number.
///
/// "V10" comes before "V2" as text, and "#100" before "#2", which in a column of
/// variation groups or test numbers reads as the list being broken. The fix is to sort
/// on the value behind the label rather than the label, and the failure it guards
/// against is silent: drop the sort path and the column still works, still sorts, and
/// still puts V10 second.
/// </summary>
public static class SortingSelfTest
{
    /// <summary>XzoundWave.exe --sorting &lt;skinId&gt;</summary>
    public static int Run(string skinId)
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        var ch = session.Characters.FirstOrDefault(c => c.CharId == skinId[..4]);
        var skin = ch?.Skins.FirstOrDefault(s => s.SkinId == skinId);
        if (skin is null) { W($"no such skin: {skinId}"); return 1; }
        var sounds = SoundIndex.Build(session, skin, skinId[..4], _ => { });

        // ---- the group column ---------------------------------------------
        W("variation groups");
        var grouped = sounds.Rows.Where(r => r.VariantGroup.Length > 0).ToList();
        var groups = grouped.Select(r => r.VariantGroupNumber).Distinct().OrderBy(n => n).ToList();
        Check("there are enough groups for the difference to show",
              groups.Count >= 10, $"{groups.Count} groups, up to V{groups.LastOrDefault()}");

        var byKey = grouped.OrderBy(r => r.GroupSort).ToList();
        var keyOrder = byKey.Select(r => r.VariantGroupNumber).Distinct().ToList();
        Check("sorting by the key gives V1, V2, V3 …",
              keyOrder.SequenceEqual(keyOrder.OrderBy(n => n)),
              string.Join(", ", keyOrder.Take(12).Select(n => "V" + n)));

        // The control: without it, this test would pass on a broken build too.
        var byText = grouped.OrderBy(r => r.VariantGroup, StringComparer.OrdinalIgnoreCase).ToList();
        var textOrder = byText.Select(r => r.VariantGroupNumber).Distinct().ToList();
        Check("...where sorting the label would not",
              !textOrder.SequenceEqual(textOrder.OrderBy(n => n)),
              string.Join(", ", textOrder.Take(12).Select(n => "V" + n)));

        // Within a group the members should still read 1,2,3 rather than jumbled.
        var inOrder = byKey.GroupBy(r => r.VariantGroupNumber)
            .All(g => g.Select(r => r.Applied.Alternative.Index)
                       .SequenceEqual(g.Select(r => r.Applied.Alternative.Index).OrderBy(i => i)));
        Check("and a group's own members stay in order", inOrder);

        Check("a row in no group sorts after every group",
              sounds.Rows.Where(r => r.VariantGroup.Length == 0).All(r => r.GroupSort == long.MaxValue));

        // ---- test numbers ---------------------------------------------------
        W("");
        W("test numbers");
        // Numbers are assigned by a test build, so this checks the shape rather than
        // needing one: the column sorts on the int, and the label is only a label.
        var numbered = sounds.Rows.Take(120).ToList();
        for (var i = 0; i < numbered.Count; i++) numbered[i].TestNumber = i + 1;
        var byNumber = numbered.OrderBy(r => r.TestNumber).Select(r => r.TestNumber.Value).ToList();
        var byLabel = numbered.OrderBy(r => r.TestLabel, StringComparer.Ordinal)
                              .Select(r => r.TestNumber.Value).ToList();
        Check("sorting by the number counts up", byNumber.SequenceEqual(byNumber.OrderBy(n => n)),
              string.Join(",", byNumber.Take(8)));
        Check("...where sorting the label would not",
              !byLabel.SequenceEqual(byLabel.OrderBy(n => n)),
              "#" + string.Join(", #", byLabel.Take(8)));
        foreach (var r in numbered) r.TestNumber = null;

        // ---- the columns actually say so ------------------------------------
        W("");
        W("the columns point at those values");
        var xaml = FindXaml();
        if (xaml is null)
        {
            W("  MainWindow.xaml not beside the binary — skipped (this check is for the repo).");
        }
        else
        {
            var text = File.ReadAllText(xaml);
            foreach (var (header, path) in new[]
                     {
                         ("Group", "GroupSort"), ("Test #", "TestNumber"),
                         ("Len", "Seconds"), ("Vol", "Volume"),
                         ("Ingame", "AppliedSort"),
                     })
            {
                var column = Regex.Match(text,
                    @"<DataGridTextColumn[^>]*Header=""" + Regex.Escape(header) + @"""[^>]*>",
                    RegexOptions.Singleline);
                Check($"the {header} column sorts on {path}",
                      column.Success && column.Value.Contains($"SortMemberPath=\"{path}\""),
                      column.Success ? "" : "column not found");
            }

            // A sort path naming a property that no longer exists fails silently back
            // to sorting the text, which is the bug this whole file is about.
            foreach (var path in new[]
                     { "GroupSort", "TestNumber", "Seconds", "Volume", "AppliedSort" })
                Check($"SoundRow still has {path}",
                      typeof(SoundRow).GetProperty(path) is not null);
        }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>The source file, when the binary is sitting inside the checkout.</summary>
    private static string FindXaml()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "MainWindow.xaml");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }
}
