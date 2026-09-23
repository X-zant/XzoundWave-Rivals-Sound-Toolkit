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

        // Three bands, in this order: alternatives inside a container, then sets that
        // only share a mixer, then rows that belong to neither. Sorting has to gather
        // each band rather than interleaving them.
        var alternatives = sounds.Rows.Where(r => r.VariantGroup.Length > 0).ToList();
        var families = sounds.Rows.Where(r => r.VariantGroup.Length == 0 &&
                                              r.FamilyMembers.Count > 1).ToList();
        var loners = sounds.Rows.Where(r => r.VariantGroup.Length == 0 &&
                                            r.FamilyMembers.Count <= 1).ToList();
        W($"    {alternatives.Count} in a container of alternatives, " +
          $"{families.Count} in a family only, {loners.Count} in neither");

        Check("alternatives sort before families",
              families.Count == 0 || alternatives.Count == 0 ||
              alternatives.Max(r => r.GroupSort) < families.Min(r => r.GroupSort));
        Check("families sort before rows in neither",
              loners.Count == 0 || families.Count == 0 ||
              families.Max(r => r.GroupSort) < loners.Min(r => r.GroupSort));
        Check("a row in neither sorts last of all",
              loners.All(r => r.GroupSort == long.MaxValue));

        // ---- sounds that belong together without sharing an event -----------
        W("");
        W("families");

        // True of every skin: a family is a set of rows sharing one mixer, and the
        // member list each row carries is that set.
        var inFamily = sounds.Rows.Where(r => r.FamilyMembers.Count > 1).ToList();
        Check("rows in a family share one mixer id",
              inFamily.GroupBy(r => r.Family)
                      .All(g => g.Select(r => r.Applied.FamilyId).Distinct().Count() == 1));
        Check("and one mixer never answers to two names",
              inFamily.GroupBy(r => r.Applied.FamilyId)
                      .All(g => g.Select(r => r.Family).Distinct().Count() == 1));
        Check("a row's member list is its family, no more and no less",
              inFamily.GroupBy(r => r.Applied.FamilyId)
                      .All(g => g.All(r => r.FamilyMembers.Count == g.Count())));

        var sizes = inFamily.GroupBy(r => r.Applied.FamilyId).Select(g => g.Count()).ToList();
        W($"    {sizes.Count} famil(y/ies), median size " +
          $"{(sizes.Count == 0 ? 0 : sizes.OrderBy(n => n).ElementAt(sizes.Count / 2))}, " +
          $"largest {(sizes.Count == 0 ? 0 : sizes.Max())}");

        // The worked example, which only this skin has in this shape: four events
        // called release1..release4, one sound each, held together by a mixer.
        if (skinId == "1029304")
        {
            var release = sounds.Rows
                .Where(r => r.EventName is not null &&
                            r.EventName.StartsWith("sfx_1029304_left_release",
                                                   StringComparison.OrdinalIgnoreCase))
                .ToList();
            Check("the four release events are found", release.Count == 4, $"{release.Count}");
            Check("they are one family in the data",
                  release.Select(r => r.Applied.FamilyId).Distinct().Count() == 1,
                  string.Join(", ", release.Select(r => r.Family).Distinct()));
            Check("...and that family holds only them",
                  release.All(r => r.FamilyMembers.Count == release.Count),
                  $"{release.FirstOrDefault()?.FamilyMembers.Count} members");
            Check("the column names the set", release.All(r => r.GroupLabel.StartsWith('F')),
                  release.FirstOrDefault()?.GroupLabel);
            foreach (var m in release[0].FamilyMembers) W("      " + m);

            // If neighbouring sets fell into the same family the grouping would be
            // worthless, because everything would be one family.
            var neighbours = sounds.Rows.Where(r =>
                r.EventName is not null &&
                r.EventName.Contains("_left_release", StringComparison.OrdinalIgnoreCase) &&
                !release.Contains(r)).ToList();
            Check("neighbouring sets are different families",
                  neighbours.Count > 0 &&
                  neighbours.All(r => r.Applied.FamilyId != release[0].Applied.FamilyId),
                  $"{neighbours.Select(r => r.Family).Distinct().Count()} other famil(y/ies)");
        }

        // The order really is absent from the bank: Wwise states one with a sequence
        // container, and this game uses none.
        var sequences = sounds.Rows.Count(r => r.Applied?.Alternative?.Mode == "sequence");
        W($"    {sequences} sound(s) sit in a sequence container (which WOULD state an order)");
        Check("nothing claims an order the .bnk does not state",
              sounds.Rows.All(r => !r.GroupDetail.Contains("plays first") ||
                                    r.GroupDetail.Contains("says nothing about")));

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
