using System.Buffers.Binary;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MRAudioKit.UI;

/// <summary>
/// The window, asserted about rather than looked at.
///
/// A layout claim is a visual claim, and the usual way to check one is to open the app
/// and squint. That needs a screen, needs someone at it, and proves nothing tomorrow.
/// Avalonia runs with no windowing system, so the same claims can be checks: the tabs
/// are in the agreed order, the list has the seven agreed columns, a tab really does
/// change the toolbar and nothing else, and a row a mod changed is tinted the same
/// colour the tree uses for it.
///
/// What the pane is asked about is what it has DRAWN, cell by cell, and not what it was
/// handed. The two are different questions, and asking the easier one is how this file
/// stayed green through a period when the pane was visibly drawing blank cells: the grid
/// is handed the staged collection itself, so reading its items back compared that list
/// with itself. Anything about a column's width is asked of <see cref="MainWindow.ColumnFits"/>
/// for the same reason.
/// </summary>
public static class UiSelfTest
{
    /// <summary>XzoundWave --uitest</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();
        }
        catch (Exception ex)
        {
            W("could not start headless: " + ex.Message);
            return 1;
        }

        var window = new MainWindow { Width = 1440, Height = 840 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<ListBox>("Tabs");
        var grid = window.FindControl<DataGrid>("Grid_");
        var tree = window.FindControl<TreeView>("Tree");
        var toolbar = window.FindControl<WrapPanel>("Toolbar");
        var pane = window.FindControl<Border>("DropPane");
        var pending = window.FindControl<DataGrid>("PendingGrid");

        W("the window is the agreed shape");
        Check("it has a tab strip, a tree, a list and a toolbar",
              tabs is not null && tree is not null && grid is not null && toolbar is not null);

        // The pane was left behind in the port and nothing caught it, because everything
        // it did still worked and only the SEEING of it was gone: a window that stages
        // without showing what is staged passes every other check in this file.
        Check("the staged pane and its list are in the window",
              pane is not null && pending is not null);

        // The order was already the order of the work, and I reordered it once for no
        // reason. This is so that cannot happen again quietly.
        var names = tabs.Items.OfType<ListBoxItem>().Select(i => i.Content?.ToString()).ToList();
        Check("the tabs are Browse, Test, Build, Mods, Setup",
              names.SequenceEqual(new[] { "Browse", "Test", "Build", "Mods", "Setup" }),
              string.Join(", ", names));

        // What the list OFFERS, which is fixed, rather than what is visible, which is
        // a preference read from this machine's settings. Asserting the visible set
        // made this test pass or fail depending on which columns whoever ran it had
        // switched off, which is a test of the tester.
        var offered = grid.Columns.Select(c => c.Header?.ToString()).ToList();
        Check("the list offers the mockup's seven columns, plus Len for measured durations",
              offered.SequenceEqual(new[]
              { "Mod", ".bnk", "Event", "Media ID", "Category", "Subtitle / line", "Tags", "Len" }),
              string.Join(" | ", offered));

        var hidden = offered.Where(h =>
            !grid.Columns.First(c => c.Header?.ToString() == h).IsVisible).ToList();
        W($"    hidden on this machine: {(hidden.Count == 0 ? "none" : string.Join(", ", hidden))}");
        Check("nothing is hidden that the list does not offer",
              hidden.All(offered.Contains));

        Check("the tree lets you pick more than one .bnk",
              tree.SelectionMode.HasFlag(Avalonia.Controls.SelectionMode.Multiple),
              tree.SelectionMode.ToString());

        W("");
        W("a tab changes the toolbar and nothing else");
        var seen = new List<string>();
        var treeBefore = tree.ItemsSource;
        for (var i = 0; i < names.Count; i++)
        {
            tabs.SelectedIndex = i;
            Dispatcher.UIThread.RunJobs();
            var labels = toolbar.Children.OfType<Button>()
                                .Select(b => b.Content?.ToString()).ToList();
            W($"    {names[i],-7} {labels.Count} button(s): {string.Join(", ", labels)}");
            seen.Add(string.Join("|", labels));
            Check($"{names[i]} has buttons", labels.Count > 0);
        }
        Check("every tab shows a different toolbar", seen.Distinct().Count() == seen.Count);
        Check("the tree is untouched by changing tab", ReferenceEquals(tree.ItemsSource, treeBefore));

        W("");
        W("the staged pane shows what is staged");

        var paneColumns = pending.Columns.Select(c => c.Header?.ToString()).ToList();
        Check("the pane offers Media ID, Your note and Lands in",
              paneColumns.SequenceEqual(new[] { "Media ID", "Your note", "Lands in" }),
              string.Join(" | ", paneColumns));

        // Room for both. The pane and the list share the splitter between them, so a
        // pane with no floor under the list can be dragged until there is no list, and
        // a pane with no floor of its own can be dragged until its own columns are gone.
        var body = pane.Parent as Grid;
        var columns = body?.ColumnDefinitions;
        Check("the body is tree, list and pane with a splitter between each",
              columns is { Count: 5 }, columns is null ? "no body grid" : $"{columns.Count} columns");
        if (columns is { Count: 5 })
            Check("neither the list nor the pane can be dragged away to nothing",
                  columns[2].MinWidth > 0 && columns[4].MinWidth > 0,
                  $"list floor {columns[2].MinWidth}, pane {columns[4].Width} " +
                  $"floor {columns[4].MinWidth}");

        Check("the pane starts empty", Drawn().Count == 0, $"{Drawn().Count} row(s) drawn");

        // Rows for the staged ids to be found in, because two of the pane's four columns
        // are worked out from the open list and would otherwise be legitimately empty --
        // which is the state the broken pane was in, so a check run without rows cannot
        // tell the two apart. See PretendRows for why this is not a mounted game.
        var open = new SkinSounds();
        open.Rows.Add(new SoundRow
        {
            Kind = "VO", Bank = "bnk_vo_1030001", MediaId = 1000585642,
            EventName = "Play_1030001_Select", Codec = "VORBIS",
            Subtitle = "Khonshu has chosen me.",
        });
        open.Rows.Add(new SoundRow
        {
            Kind = "VO", Bank = "bnk_vo_1030001", MediaId = 424242,
            EventName = "Play_1030001_Emote", Codec = "VORBIS",
            Subtitle = "The night is mine.",
        });
        window.PretendRows(open);
        Dispatcher.UIThread.RunJobs();

        // Files through the real staging path, not a PendingWem handed straight to the
        // grid: what was broken was the WIRING between staging and the pane, and a check
        // that skips the wiring is a check of nothing.
        var folder = Path.Combine(Path.GetTempPath(), "xzoundwave-uitest");
        Directory.CreateDirectory(folder);
        var one = Path.Combine(folder, "1000585642-uitest note.wav");
        var again = Path.Combine(folder, "1000585642-corrected take.wav");
        var earlier = Path.Combine(folder, "424242-sorts first.wav");
        var nowhere = Path.Combine(folder, "4242424242-no bank holds this.wav");
        File.WriteAllBytes(one, Wav(12000));
        File.WriteAllBytes(again, Wav(9000));
        File.WriteAllBytes(earlier, Wav(6000));
        File.WriteAllBytes(nowhere, Wav(6000));

        var staged = window.StageSync([one]);
        Dispatcher.UIThread.RunJobs();
        var rows = Drawn();
        W($"    staged {staged}, drawn: {Describe(rows)}");
        Check("staging a file draws a row in the pane", rows.Count == 1, $"{rows.Count} row(s) drawn");
        if (rows.Count == 1)
        {
            Check("the drawn row carries the media id from the filename",
                  rows[0].GetValueOrDefault("Media ID") == "1000585642",
                  $"\"{rows[0].GetValueOrDefault("Media ID")}\"");
            Check("and the note after the first hyphen",
                  rows[0].GetValueOrDefault("Your note") == "uitest note",
                  $"\"{rows[0].GetValueOrDefault("Your note")}\"");
            Check("and where it lands",
                  rows[0].GetValueOrDefault("Lands in")?.Length > 0,
                  $"\"{rows[0].GetValueOrDefault("Lands in")}\"");
        }

        // The same id twice is an author correcting themselves, and this is the exact bug
        // that shipped: the replacement is a DIFFERENT object, bound by the grid with its
        // computed cells still empty and filled in a moment later by code the grid is not
        // watching. Every reading the pane takes comes out the same as before, so a guard
        // on the readings alone cannot tell that anything needs re-drawing, and the
        // corrected file sits there with a blank "Lands in" -- a file that lands nowhere.
        // A DIFFERENT file, so the swap is visible in a cell rather than only in identity.
        window.StageSync([again]);
        Dispatcher.UIThread.RunJobs();
        rows = Drawn();
        W($"    restaged, drawn: {Describe(rows)}");
        Check("staging the same id again replaces rather than appends",
              rows.Count == 1, $"{rows.Count} row(s) drawn");
        if (rows.Count == 1)
        {
            Check("the corrected file's own note is what the pane draws",
                  rows[0].GetValueOrDefault("Your note") == "corrected take",
                  $"\"{rows[0].GetValueOrDefault("Your note")}\"");
            Check("and its Lands in cell is still filled in",
                  rows[0].GetValueOrDefault("Lands in")?.Length > 0,
                  $"\"{rows[0].GetValueOrDefault("Lands in")}\"");
        }

        // Staging something that sorts AHEAD of what is already there, which is the one
        // staging that reorders the list. The selection is what to watch: re-listing the
        // grid costs it, and losing it means the Remove button someone was reaching for
        // has nothing to act on.
        var picked = pending.GetVisualDescendants().OfType<DataGridRow>()
                            .OrderBy(r => r.Index)
                            .Select(r => r.DataContext).OfType<PendingWem>().FirstOrDefault();
        if (picked is not null) pending.SelectedItems?.Add(picked);
        Check("a staged row can be picked in the pane",
              pending.SelectedItems?.Count == 1, $"{pending.SelectedItems?.Count} selected");

        window.StageSync([earlier]);
        Dispatcher.UIThread.RunJobs();
        rows = Drawn();
        W($"    staged one that sorts first, drawn: {Describe(rows)}");
        Check("the pane draws both, in media id order",
              rows.Select(r => r.GetValueOrDefault("Media ID"))
                  .SequenceEqual(new[] { "424242", "1000585642" }),
              string.Join(", ", rows.Select(r => r.GetValueOrDefault("Media ID"))));
        Check("and the row that was picked is still picked",
              pending.SelectedItems?.Contains(picked) == true,
              $"{pending.SelectedItems?.Count} selected");

        // Your note is a star column that ellipsises after about a dozen characters, and
        // the pane has no Codec column at all, so for both of them the row's tooltip is
        // the only place the whole answer can be read. Asked of the realised row, because
        // the tip is set as the row is built and a tip nobody set is a tip nobody reads.
        var tips = pending.GetVisualDescendants().OfType<DataGridRow>()
                          .Where(r => r.DataContext is PendingWem)
                          .OrderBy(r => r.Index)
                          .Select(r => ToolTip.GetTip(r)?.ToString() ?? "").ToList();
        var tip = (tips.FirstOrDefault() ?? "").Replace("\n", " / ");
        W($"    the first row's tooltip: {tip}");
        Check("the row's tooltip carries the note its column cannot show whole",
              tip.Contains("sorts first"), tip);
        Check("and the codec, which no longer has a column", tip.Contains("VORBIS"), tip);
        // Replaces went to the details sub-panel, where it is prose and needs no width. The
        // tooltip is what keeps it reachable from the staged list itself, which matters
        // because the details panel is showing whichever row is SELECTED and that is not
        // usually the one being pointed at here.
        Check("and the line it is about to overwrite, whose column has gone",
              tip.Contains("The night is mine."), tip);

        W("");
        W("the toolbar's staged count is the number that is staged");

        var toolbarStaged = window.FindControl<TextBlock>("StatStaged");
        var filter = window.FindControl<TextBox>("TbFilter");
        W($"    {rows.Count} drawn in the pane, toolbar says \"{toolbarStaged?.Text}\"");
        Check("with every staged id on screen it says so plainly, with no star",
              toolbarStaged?.Text == "2 staged", $"\"{toolbarStaged?.Text}\"");

        // An id no open .bnk holds is staged, builds, and ships, so a count that leaves it
        // out is wrong rather than merely cautious. It cannot be tinted anywhere, hence
        // the star this window already uses for "and some you cannot see".
        window.StageSync([nowhere]);
        Dispatcher.UIThread.RunJobs();
        W($"    {Drawn().Count} drawn in the pane, toolbar says \"{toolbarStaged?.Text}\"");
        Check("an id no open .bnk holds is counted, and starred",
              toolbarStaged?.Text == "3 staged *", $"\"{toolbarStaged?.Text}\"");
        Check("the pane draws all three of them", Drawn().Count == 3, Describe(Drawn()));

        // The measured version of the same bug: with a word typed that matches nothing the
        // count read as the empty string while three files were staged and about to build.
        filter.Text = "nothing matches this";
        Dispatcher.UIThread.RunJobs();
        W($"    with a filter typed: toolbar says \"{toolbarStaged?.Text}\", " +
          $"{Drawn().Count} still drawn in the pane");
        Check("and a filter that hides every row does not empty the count",
              toolbarStaged?.Text == "3 staged *", $"\"{toolbarStaged?.Text}\"");
        filter.Text = "";
        Dispatcher.UIThread.RunJobs();

        W("");
        W("the right panel is two sub-panels with a switch");

        var paneTabs = window.FindControl<ListBox>("PaneTabs");
        var staging = window.FindControl<Grid>("PaneStaging");
        var detailsPane = window.FindControl<ScrollViewer>("PaneDetails");
        var details = window.FindControl<StackPanel>("Details");
        var filesTab = window.FindControl<ListBoxItem>("PaneTabFiles");
        // The status line, which is where a press on one of the details panel's boxes is
        // answered: what it says after a press is the evidence of which path the press took.
        var status = window.FindControl<TextBlock>("Status");

        Check("the panel holds a staged list, a details panel and the switch between them",
              paneTabs is not null && staging is not null && detailsPane is not null &&
              details is not null && filesTab is not null);

        var subPanels = paneTabs.Items.OfType<ListBoxItem>()
                                .Select(i => i.Content?.ToString() ?? "").ToList();
        Check("the switch names both of them",
              subPanels.Count == 2 && subPanels[0].StartsWith("Staged files") &&
              subPanels[1] == "Details", string.Join(" | ", subPanels));

        // WHICH one starts showing is a setting, so asserting it would be a test of whoever
        // ran this, the same way asserting the visible columns was. The invariant that holds
        // for everybody is that the panel is showing one of them and not both or neither.
        Check("exactly one of them is showing", staging.IsVisible != detailsPane.IsVisible,
              $"staged list {staging.IsVisible}, details {detailsPane.IsVisible}");

        // Driven through PaneShow, which is the route the drop uses. Setting SelectedIndex
        // here instead would write this machine's settings file from a test run.
        var chosen = window.Config.RightPanelDetails;
        window.PaneShow(details: true);
        Dispatcher.UIThread.RunJobs();
        Check("switching to the details panel shows it and hides the staged list",
              detailsPane.IsVisible && !staging.IsVisible,
              $"staged list {staging.IsVisible}, details {detailsPane.IsVisible}");
        Check("and a switch the program made is not remembered as the reader's choice",
              window.Config.RightPanelDetails == chosen,
              $"setting was {chosen}, is {window.Config.RightPanelDetails}");

        W("");
        W("the details panel says what is true of the current selection");

        var first = open.Rows[0];
        window.SelectSync(1, first.MediaId);
        Dispatcher.UIThread.RunJobs();
        var drawn = Panel();
        W($"    {drawn}");
        Check("it names the row that is selected",
              drawn.Contains(first.Display) && drawn.Contains(first.MediaId.ToString()), drawn);
        Check("and the whole subtitle, which is the column that collapses first",
              drawn.Contains(first.Subtitle), drawn);
        Check("and which file is staged over it",
              drawn.Contains("corrected take"), drawn);

        var second = open.Rows[1];
        window.SelectSync(1, second.MediaId);
        Dispatcher.UIThread.RunJobs();
        drawn = Panel();
        Check("it follows the selection rather than staying on the row it drew first",
              drawn.Contains(second.Display) && !drawn.Contains(first.Subtitle), drawn);

        grid.SelectedItems.Clear();
        Dispatcher.UIThread.RunJobs();
        drawn = Panel();
        Check("with nothing selected it reads the list instead of the last row",
              drawn.Contains("Nothing selected") && !drawn.Contains(second.Subtitle), drawn);

        W("");
        W("a drop stages exactly once, whichever sub-panel is showing");

        // The wiring has two ways in and registering it twice would stage one dropped file
        // twice, reported as "N staged (0 new, N updated)", which looks plausible. Called
        // again here on purpose: the guard inside it is the thing being checked.
        window.HookDragAndDrop();
        var staged3 = Drawn().Count;

        var whileStaging = Path.Combine(folder, "77777-dropped on the staged list.wav");
        var whileDetails = Path.Combine(folder, "88888-dropped on the details panel.wav");
        File.WriteAllBytes(whileStaging, Wav(6000));
        File.WriteAllBytes(whileDetails, Wav(6000));

        window.PaneShow(details: false);
        window.StageSync([whileStaging]);
        Dispatcher.UIThread.RunJobs();
        Check("with the staged list showing, one file becomes one row",
              Drawn().Count == staged3 + 1, $"{staged3} before, {Drawn().Count} after");

        window.PaneShow(details: true);
        window.StageSync([whileDetails]);
        Dispatcher.UIThread.RunJobs();
        // Read off the switch, because with the staged list hidden its rows are not realised
        // and the label is the only thing on screen saying the drop landed at all.
        Check("with the details panel showing, the count on the switch says so",
              filesTab.Content?.ToString()?.Contains((staged3 + 2).ToString()) == true,
              $"\"{filesTab.Content}\"");

        window.PaneShow(details: false);
        Dispatcher.UIThread.RunJobs();
        var back = Drawn();
        Check("and switching back finds it, once and not twice",
              back.Count == staged3 + 2 &&
              back.Count(r => r.GetValueOrDefault("Media ID") == "88888") == 1,
              $"{back.Count} row(s), {back.Count(r => r.GetValueOrDefault("Media ID") == "88888")} for 88888");

        // The structural half of the same claim. The window owns the only drop handler and
        // reads where the pointer was; a sub-panel that took drops of its own would see the
        // same file after it, and both would stage it.
        Check("neither sub-panel takes a drop of its own",
              !staging.IsSet(DragDrop.AllowDropProperty) &&
              !detailsPane.IsSet(DragDrop.AllowDropProperty) &&
              !details.IsSet(DragDrop.AllowDropProperty) &&
              DragDrop.GetAllowDrop(window),
              $"window {DragDrop.GetAllowDrop(window)}, staged list " +
              $"{staging.IsSet(DragDrop.AllowDropProperty)}, details " +
              $"{detailsPane.IsSet(DragDrop.AllowDropProperty)}");

        // The panel's own floor, stated here the way the panel derives it, so the two have to
        // agree. A floor below this is a panel that can be dragged to where its own grid
        // scrolls sideways, which is what 260 allowed.
        if (columns is { Count: 5 })
        {
            var needs = pending.Columns.Where(c => c.IsVisible).Sum(c => c.MinWidth)
                        + pending.Margin.Left + pending.Margin.Right
                        + pane.BorderThickness.Left + pane.BorderThickness.Right;
            Check("the panel cannot be dragged narrower than its own columns need",
                  columns[4].MinWidth >= needs,
                  $"floor {columns[4].MinWidth:0}, columns need {needs:0}");

            // And what the columns read like there, which is the width nobody had looked at
            // because the splitter is the only way to reach it and a render has no pointer.
            var wide = columns[4].Width;
            columns[4].Width = new GridLength(columns[4].MinWidth);
            Dispatcher.UIThread.RunJobs();
            Fits($"the panel dragged to its own floor of {columns[4].MinWidth:0}");
            columns[4].Width = wide;
            Dispatcher.UIThread.RunJobs();
        }

        W("");
        W("every column fits what it has to show");
        Fits($"{window.Width:0}x{window.Height:0}, the size every render has been taken at");

        // And at the narrowest the window is allowed to be, which is the size nobody had
        // looked at, because the verb that renders it could not be given one until now.
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        Dispatcher.UIThread.RunJobs();
        Fits($"{window.Width:0}x{window.Height:0}, the window's own minimum");

        W("");
        W("the details panel counts sounds and container slots, not the rows they are on");

        // Last, and after the widths, because both of the counts below need the details
        // sub-panel showing and the staged list's rows stop being realised while it is
        // hidden, which is what the three checks above read.
        //
        // A sound two events reach is two rows, and both counts were reading those rows. The
        // shape is the game's: on skin 1030001 a mixer of 6 sounds is 9 rows and the panel
        // said "one of 9", and a container of 2 alternatives that 2 events reach listed its
        // set as four lines numbered 1, 1, 2, 2 and then said the bank states 2. Rows built
        // by hand rather than mounted, for the reason PretendRows gives, and one selection
        // covers both because in the bank the two share a cause.
        var twice = new SkinSounds();
        // The container's own two slots, which is where the set comes from: gathering the
        // rows below would find four members for a container of two.
        // Worded by Variation itself rather than typed out, because the per-cent sign is
        // placed by the machine's own locale and a literal here would make this fixture
        // read differently from every real set on the same screen.
        var half = new WwiseProps.Variation(0, 2, "random", 0.5, true, 999, []).Share;
        WwiseProps.Alternate[] bothSlots = [new(0, half, [100u]), new(1, half, [200u])];
        var variants = new List<WwiseProps.Variation>
        {
            new(0, 2, "random", 0.5, true, 999, bothSlots),
            new(1, 2, "random", 0.5, true, 999, bothSlots),
        };
        foreach (var (media, slot, name) in new (uint, int, string)[]
                 {
                     (100, 0, "uitest_first_play"),  (100, 0, "uitest_first_again_play"),
                     (200, 1, "uitest_second_play"), (200, 1, "uitest_second_again_play"),
                 })
            twice.Rows.Add(new SoundRow
            {
                Kind = "VO", Bank = "bnk_vo_1030001", MediaId = media, EventName = name,
                Codec = "VORBIS", Subtitle = "Two events, one sound.",
                Applied = new WwiseProps.Applied { FamilyId = 777, Alternative = variants[slot] },
                Family = "F1",
                // What SoundIndex.cs:479 fills in: one entry per ROW in the family, which is
                // the list whose count was being printed as a number of sounds.
                FamilyMembers = ["> uitest_first_play", "  uitest_first_again_play",
                                 "  uitest_second_play", "  uitest_second_again_play"],
            });

        window.PretendRows(twice);
        window.PaneShow(details: true);
        Dispatcher.UIThread.RunJobs();
        var picks = window.SelectSync(1, 100);
        Dispatcher.UIThread.RunJobs();
        drawn = Panel();
        W($"    {drawn}");
        Check("a row of the hand-built set is what the panel is describing",
              picks == 1 && drawn.Contains("uitest_first_play"), $"{picks} selected");
        Check("a mixer holding one sound under two events is one sound, not two rows",
              drawn.Contains("One of 2 sounds on it"), Around(drawn, "sounds on it"));
        // The selection is rows and the sentence above it is sounds, so the two numbers
        // differ on purpose. What the line must not do is offer "all 4 of them" under a
        // sentence that has just said there are 2.
        Check("and the line that selects them says it is counting rows",
              drawn.Contains("Select the 4 rows in that group") &&
              !drawn.Contains("Select all 4 of them"), Around(drawn, "Select "));

        var slots = Lines().Where(t => t.StartsWith("1. media") || t.StartsWith("2. media")).ToList();
        Check("a container of two alternatives lists two, one line per slot",
              slots.Count == 2 && slots[0].Contains("media 100") && slots[1].Contains("media 200"),
              string.Join(" | ", slots));
        Check("and marks this one exactly once",
              slots.Count(t => t.Contains("this one")) == 1, string.Join(" | ", slots));
        // The sentence that used to close the list and contradict it: four lines above it,
        // and then "the bank states 2 of them and 4 are in the .bnk files you have open".
        Check("and says nothing about members the list does not hold",
              !drawn.Contains("are in the .bnk files you have open"),
              Around(drawn, "The bank states"));

        W("");
        W("a box per medium, which can select it and can play it");

        // The same hand-built set, read as CONTROLS rather than as text. One box per slot is
        // the claim the section is for, and it is the count that has already been wrong once
        // here in the other direction: a container two events reach printed four lines for
        // two alternatives.
        var set = Boxes().Where(b => b.Label.StartsWith("1. media") ||
                                     b.Label.StartsWith("2. media")).ToList();
        W($"    {string.Join(" | ", set.Select(b => $"[{b.Play.Content}] {b.Label}"))}");
        Check("a set of two alternatives is two boxes, one per distinct medium",
              set.Count == 2 && set[0].Label.Contains("media 100") &&
              set[1].Label.Contains("media 200"),
              string.Join(" | ", set.Select(b => b.Label)));
        Check("and every box has a play button beside its label",
              set.Count == 2 && set.All(b => b.Play.Content as string == "▶" && b.Play.IsEnabled));

        // The current sound's box, marked in words and DRAWN as one of the selected ones,
        // because its row is the one the list is on. Asked of the control and not of the text:
        // the words are the same words the line carried before and would go on reading right
        // while every box looked identical, which is what they did until the boxes became
        // something a reader selects with.
        var mine = set.Where(b => b.Label.Contains("this one")).ToList();
        Check("the current sound's box is marked, exactly once, and drawn as selected",
              mine.Count == 1 && mine[0].Label.Contains("media 100") &&
              mine[0].Face.Classes.Contains("picked"),
              $"{mine.Count} marked, classes {(mine.Count == 1 ? string.Join(".", mine[0].Face.Classes) : "-")}");
        Check("and the box for a row the list is not holding is not drawn as selected",
              set.Count == 2 && !set[1].Face.Classes.Contains("picked"),
              set.Count == 2 ? string.Join(".", set[1].Face.Classes) : "-");
        // That the class paints something, and not only that it is on the control. A selected
        // state nobody can see is the defect this replaced, so the two boxes are asked for the
        // brush they will actually be drawn in.
        Check("and selected is a different surface from not selected, not just a class",
              set.Count == 2 && mine.Count == 1 &&
              mine[0].Face is Button { Background: { } lit } &&
              set[1].Face is Button { Background: { } unlit } &&
              !ReferenceEquals(lit, unlit),
              set.Count == 2 && mine.Count == 1
                  ? $"{(mine[0].Face as Button)?.Background} against {(set[1].Face as Button)?.Background}"
                  : "-");

        W("");
        W("a set is what its container holds, not what rows happen to carry its id");

        // The shape that made the list above draw a fraction of a set and say nothing about
        // it. The index keeps one row per bank, event and medium, and this game's banks build
        // one set of media into container after container through Sound objects of their own
        // each time, so the rows scatter across whichever container each medium was met in
        // first. In bnk_sfx_1022001 container 183834482 holds media 673786398, 53978063 and
        // 634398524 and only the first comes back with a row naming it, the other two naming
        // container 479715779, which holds one medium of its own that no row reaches at all.
        // 21 sets of that skin, 7 of 1034001 and 4 of 1030001 are shaped this way.
        //
        // Built by hand for the reason PretendRows gives, and to the same proportions: one
        // set whose three media are two other rows' business, a twin sharing two of the
        // three, and a member with no row anywhere.
        var third = new WwiseProps.Variation(0, 3, "random", 1 / 3.0, true, 555, []).Share;
        WwiseProps.Alternate[] held =
            [new(0, third, [600u]), new(1, third, [700u]), new(2, third, [800u])];
        WwiseProps.Alternate[] twin =
            [new(0, third, [900u]), new(1, third, [700u]), new(2, third, [800u])];
        var spread = new SkinSounds();
        foreach (var (media, slot, group, holds) in new (uint, int, uint, WwiseProps.Alternate[])[]
                 {
                     (600, 0, 555u, held), (700, 1, 666u, twin), (800, 2, 666u, twin),
                 })
            spread.Rows.Add(new SoundRow
            {
                Kind = "SFX", Bank = "bnk_sfx_1022001", MediaId = media,
                EventName = $"uitest_spread_{media}_play", Codec = "VORBIS",
                Applied = new WwiseProps.Applied
                {
                    Alternative = new WwiseProps.Variation(slot, 3, "random", 1 / 3.0, true,
                                                           group, holds),
                },
            });

        window.PretendRows(spread);
        Dispatcher.UIThread.RunJobs();
        window.SelectSync(1, 600);
        Dispatcher.UIThread.RunJobs();
        drawn = Panel();
        var spreadSet = Boxes().Where(b => b.Label.Contains(". media ")).ToList();
        W($"    {string.Join(" | ", spreadSet.Select(b => b.Label))}");
        // The one that used to draw a single box for a set of three, because two of the three
        // carry another container's id and gathering rows on this one never saw them.
        Check("a set of three whose other members are filed under a twin still lists three",
              spreadSet.Count == 3 && spreadSet[0].Label.Contains("media 600") &&
              spreadSet[1].Label.Contains("media 700") && spreadSet[2].Label.Contains("media 800"),
              string.Join(" | ", spreadSet.Select(b => b.Label)));
        Check("and every one of them is a box a reader can play and select, one of them lit",
              spreadSet.Count == 3 && spreadSet.All(b => b.Play.IsEnabled) &&
              spreadSet.All(b => b.Face is Button) &&
              spreadSet.Count(b => b.Face.Classes.Contains("picked")) == 1,
              string.Join(" | ", spreadSet.Select(
                  b => $"{b.Label} {string.Join(".", b.Face.Classes)}")));
        // A box is one per medium and the Ingame preview is per ROW, and in bnk_sfx_1030001
        // 154 of 623 distinct media are reached by more than one row, 136 of those by rows
        // whose banks state a different level: media 378759846 by three at -14, -7 and -4 dB.
        // Which row the box plays is the pane's pick and a defensible one, but with Ingame on
        // it decides the loudness, so it has to be named rather than left to be assumed.
        Check("and the play button names the row whose chain it is about to apply",
              spreadSet.Count == 3 &&
              ToolTip.GetTip(spreadSet[1].Play) is string applies &&
              applies.Contains("uitest_spread_700_play") && applies.Contains("With Ingame on"),
              spreadSet.Count == 3 ? ToolTip.GetTip(spreadSet[1].Play) as string
                                   : $"{spreadSet.Count} boxes");
        // The sentence that used to close the list. It counted the boxes, so it agreed with a
        // list that was short and told the reader the rest were not open when they were on
        // screen at the time.
        Check("and nothing says the members it did not draw are missing from the banks",
              !drawn.Contains("are in the .bnk files you have open") &&
              !drawn.Contains("one is in the .bnk"), Around(drawn, "The bank states"));

        W("");
        W("a box moves the selection and not what the panel is describing");

        // The panel has a subject of its own now (MainWindow.Details.cs, _detailsAnchor), and
        // this fixture is the shape that made the old behaviour wrong twice over.
        //
        // A box stands for a SLOT of the container the panel is describing, while the row
        // behind it carries whichever container its own medium was met in first: media 700's
        // row names the twin, whose slots are 900, 700 and 800. A panel that read its subject
        // off the list therefore followed a plain click onto a DIFFERENT set. Measured against
        // the game's own banks before this changed, that happened on 350 of 1,085 containers
        // across six skins, 32 per cent of them.
        //
        // And ctrl+click, which is the gesture these boxes exist for and the one their tooltip
        // teaches, took the panel from a sound's answers to a selection's and threw away
        // everything the reader was reading.
        List<(Button Play, Control Face, string Label)> Set() =>
            Boxes().Where(b => b.Label.Contains(". media ")).ToList();

        if (Set().Count == 3)
        {
            Click(Set()[1].Face, KeyModifiers.None);
            Check("a plain click on a box leaves the panel on the set it was drawn from",
                  Set().Count == 3 && Set()[0].Label.Contains("media 600") &&
                  Set()[1].Label.Contains("media 700") && Set()[2].Label.Contains("media 800") &&
                  !Panel().Contains("media 900"),
                  string.Join(" | ", Set().Select(b => b.Label)));
            Check("and the panel still names the sound it was describing, sections and all",
                  Panel().Contains("uitest_spread_600_play") &&
                  Panel().Contains("What the game does to it") &&
                  Panel().Contains("Where the audio is"),
                  Around(Panel(), "uitest_spread"));
            Check("while the list holds exactly the row that was clicked",
                  Held() == "uitest_spread_700_play", Held());
            // Which box is lit is the selection's business and follows the click, because a box
            // drawn selected is a claim about the LIST and has to stay true.
            Check("and the clicked box is the lit one, because that is what is selected",
                  Set().Count(b => b.Face.Classes.Contains("picked")) == 1 &&
                  Set()[1].Face.Classes.Contains("picked"),
                  string.Join(" | ", Set().Select(
                      b => $"{b.Label} {string.Join(".", b.Face.Classes)}")));

            // The gesture in the user's own words: ctrl+click the others to select them all.
            // This is the click that used to wipe the panel.
            Click(Set()[2].Face, KeyModifiers.Control);
            Click(Set()[0].Face, KeyModifiers.Control);
            Check("ctrl+clicking down the set keeps the sound's own answers in front of you",
                  Panel().Contains("uitest_spread_600_play") &&
                  Panel().Contains("What the game does to it") &&
                  Panel().Contains("Where the audio is") && Set().Count == 3,
                  Around(Panel(), "What the game does"));
            Check("and says what the selection has come to, in a line rather than a page",
                  Panel().Contains("3 rows selected over 3 distinct media") &&
                  Panel().Contains("the whole of one set of 3 alternatives"),
                  Around(Panel(), "rows selected"));
            Check("and the list holds exactly the three rows those clicks named",
                  window.SelectedRows.Count == 3 && Set().All(b => b.Face.Classes.Contains("picked")),
                  Held());

            // Taking the described sound back OUT of the selection. The panel goes on
            // describing it and says in words that it is not one of the selected rows, which is
            // the other half of the box beside it being drawn unlit.
            Click(Set()[0].Face, KeyModifiers.Control);
            Check("deselecting the described sound leaves the panel on it, and says so",
                  Panel().Contains("uitest_spread_600_play") &&
                  Panel().Contains("this sound is not one of them") &&
                  Set().Count == 3 && !Set()[0].Face.Classes.Contains("picked"),
                  Around(Panel(), "not one of them"));

            // The one gesture that does move it, which is why it is named in the box's tooltip
            // beside the three that do not.
            Check("and a box says so, so the four gestures are all findable in one place",
                  ToolTip.GetTip(Set()[1].Face) is string four &&
                  four.Contains("None of those change what the panel is describing") &&
                  four.Contains("Double-click"),
                  ToolTip.GetTip(Set()[1].Face) as string);
            // Through the method the double-click handler calls, for the reason PlayMediaSync
            // gives about going through the list's own play path: the headless mouse puts in
            // presses and releases and no click count, so a double-click cannot be raised here,
            // and a check that rebuilt what the handler does would be a check of itself.
            Check("going to a box's sound describes that sound, which is the twin's set here",
                  window.GoToMediaSync(700) && Panel().Contains("uitest_spread_700_play") &&
                  Panel().Contains("media 900"), Around(Panel(), "uitest_spread"));
        }
        else Check("the spread set has drawn the three boxes these clicks are measured on",
                   false, $"{Set().Count} boxes");

        // The twin's own set, which is where a member with no row at all lives. Neither half
        // of a box could act on it, so it is a line, and the point is that the line is there.
        window.SelectSync(1, 700);
        Dispatcher.UIThread.RunJobs();
        drawn = Panel();
        var twinSet = Boxes().Where(b => b.Label.Contains(". media ")).ToList();
        Check("a member the index has no row for is still listed, as a line rather than a box",
              twinSet.Count == 2 && drawn.Contains("1. media 900") &&
              drawn.Contains("no row in this list"), Around(drawn, "media 900"));
        Check("and the members that do have rows are still boxes",
              twinSet.Count == 2 && twinSet[0].Label.Contains("media 700") &&
              twinSet[1].Label.Contains("media 800"),
              string.Join(" | ", twinSet.Select(b => b.Label)));

        // Back to the two-slot set, because the checks below read the family it carries.
        window.PretendRows(twice);
        Dispatcher.UIThread.RunJobs();
        window.SelectSync(1, 100);
        Dispatcher.UIThread.RunJobs();

        // The mixer family's boxes, which for a family this size are now drawn where a reader
        // can see them. They used to be behind a shut expander whatever the family held, which
        // is why nobody had ever seen one: the expander was put there for the 224-media case
        // and then applied to a mixer of two as well, beside a set of two whose boxes were
        // inline. The rule is one threshold for every list of boxes in the pane
        // (MainWindow.Details.cs, InlineBoxes), and the large case is checked below.
        var family = details.GetVisualDescendants().OfType<Expander>()
                            .FirstOrDefault(e => (e.Header as string)?.Contains("one box each") == true);
        var members = Boxes().Where(b => b.Label.StartsWith("media ")).ToList();
        Check("a mixer small enough to draw is drawn, not hidden behind an expander",
              family is null && members.Count == 2,
              family is null ? $"{members.Count} boxes" : $"still behind \"{family.Header}\"");
        Check("and it draws exactly as many boxes as the sentence above it counted",
              members.Count == 2 && members.Select(b => b.Label).Distinct().Count() == 2,
              string.Join(" | ", members.Select(b => b.Label)));
        Check("and marks the current sound there too, drawn as selected like a set's box is",
              members.Count(b => b.Face.Classes.Contains("picked")) == 1 &&
              members.Single(b => b.Face.Classes.Contains("picked")).Label.Contains("this one"),
              string.Join(" | ", members.Select(
                  b => $"{b.Label} {string.Join(".", b.Face.Classes)}")));

        W("");
        W("a long box list is behind an expander, and a short one is not, by one threshold");

        // The other side of the rule, and the reason the threshold exists at all: a mixer can
        // hold 224 distinct media on skin 1030001 and 244 on 1022001, which at two controls a
        // box is 448 controls on a panel rebuilt once per dispatcher pass. So the same call
        // that drew the two above has to put a big one away, and say how big before it is
        // opened. Sized from the pane's own constant by building one family either side of it,
        // so this cannot pass by agreeing with a number that has drifted.
        SkinSounds Mixer(int media)
        {
            var built = new SkinSounds();
            for (var i = 0; i < media; i++)
                built.Rows.Add(new SoundRow
                {
                    Kind = "SFX", Bank = "bnk_sfx_1030001", MediaId = (uint)(9000 + i),
                    EventName = $"uitest_mixer_{i}_play", Codec = "VORBIS",
                    Applied = new WwiseProps.Applied { FamilyId = 1234 },
                    Family = "F9",
                    FamilyMembers = Enumerable.Range(0, media)
                                              .Select(n => $"  uitest_mixer_{n}_play").ToList(),
                });
            return built;
        }

        // How many boxes the pane draws before it stops, read back from the pane rather than
        // typed here: what is being checked is that one threshold governs both lists, not that
        // it happens to be sixteen today.
        var inlineAt = 0;
        for (var n = 2; n <= 64 && inlineAt == 0; n++)
        {
            window.PretendRows(Mixer(n + 1));
            Dispatcher.UIThread.RunJobs();
            window.SelectSync(1, 9000);
            Dispatcher.UIThread.RunJobs();
            if (details.GetVisualDescendants().OfType<Expander>()
                       .Any(e => (e.Header as string)?.Contains("one box each") == true))
                inlineAt = n;
        }
        W($"    the pane draws up to {inlineAt} boxes and puts more away");
        Check("a family one over the threshold is behind an expander that states its size",
              inlineAt > 0 && details.GetVisualDescendants().OfType<Expander>()
                     .FirstOrDefault(e => (e.Header as string)?.Contains("one box each") == true)
                     is { IsExpanded: false } shut &&
              shut.Header as string == $"All {inlineAt + 1} of them, one box each",
              details.GetVisualDescendants().OfType<Expander>()
                     .Select(e => e.Header as string).FirstOrDefault(h => h?.Contains("box") == true)
              ?? "no expander says how many");
        Check("and draws nothing at all until it is opened, because a family runs to hundreds",
              Boxes().Count(b => b.Label.StartsWith("media ")) == 0,
              $"{Boxes().Count(b => b.Label.StartsWith("media "))} boxes drawn while shut");

        window.ExpandDetailsSync();
        Dispatcher.UIThread.RunJobs();
        Check("opened, it draws exactly as many boxes as its header counted",
              Boxes().Count(b => b.Label.StartsWith("media ")) == inlineAt + 1,
              $"{Boxes().Count(b => b.Label.StartsWith("media "))} boxes for {inlineAt + 1}");

        // Opened, it has to STAY open under the reader. A box inside it is a selection change and
        // so a redraw, and every redraw builds the family's expander again, which used to be built
        // shut: the list closed after every click and nobody could ctrl+click down it. Asked of
        // the expander drawn after each click, and of a different control from the one opened,
        // so this cannot pass by the panel simply not having been redrawn.
        Expander Family() => details.GetVisualDescendants().OfType<Expander>()
                                    .FirstOrDefault(e => (e.Header as string)?.Contains("one box each") == true);
        List<(Button Play, Control Face, string Label)> InFamily() =>
            Boxes().Where(b => b.Label.StartsWith("media ")).ToList();
        var openedFamily = Family();
        if (InFamily().Count == inlineAt + 1)
        {
            Click(InFamily()[2].Face, KeyModifiers.None);
            var redrawn = Family();
            Check("a box clicked inside the opened family leaves it open, boxes and all",
                  redrawn is { IsExpanded: true } && !ReferenceEquals(redrawn, openedFamily) &&
                  InFamily().Count == inlineAt + 1,
                  $"{(redrawn is null ? "no family drawn" : redrawn.IsExpanded ? "open" : "shut")}, " +
                  $"{(ReferenceEquals(redrawn, openedFamily) ? "not redrawn" : "redrawn")}, " +
                  $"{InFamily().Count} boxes");
            Click(InFamily()[4].Face, KeyModifiers.Control);
            var litInFamily = InFamily().Count(b => b.Face.Classes.Contains("picked"));
            Check("and a ctrl+click further down keeps it open, with both boxes lit",
                  Family() is { IsExpanded: true } && InFamily().Count == inlineAt + 1 &&
                  litInFamily == 2,
                  $"{(Family()?.IsExpanded == true ? "open" : "shut")}, {litInFamily} lit");
        }
        else Check("the opened family has drawn the boxes these clicks are measured on", false,
                   $"{InFamily().Count} boxes");

        // Shut again the way a reader shuts it, because the panel remembers an opened list
        // across redraws and across sounds, and the timings below are of a family behind a SHUT
        // expander, which is the figure InlineBoxes quotes.
        foreach (var e in details.GetVisualDescendants().OfType<Expander>().ToList())
            e.IsExpanded = false;
        Dispatcher.UIThread.RunJobs();

        // What the threshold is bought with. Printed rather than asserted to a figure, because
        // a millisecond on this machine is not a millisecond on another one; what IS asserted
        // is that a panel drawing the threshold's worth of boxes still lands inside one frame
        // at 60 Hz, which is the point at which a redraw per selection change starts to show.
        foreach (var n in new[] { 4, inlineAt, inlineAt + 1, 224 })
        {
            window.PretendRows(Mixer(n));
            Dispatcher.UIThread.RunJobs();
            window.SelectSync(1, 9000);
            Dispatcher.UIThread.RunJobs();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 20; i++) { window.ShowDetails(force: true); Dispatcher.UIThread.RunJobs(); }
            W($"    a family of {n,3}: {clock.Elapsed.TotalMilliseconds / 20:0.00} ms a draw" +
              (n > inlineAt ? "   (behind the expander)" : "   (drawn)"));
        }

        window.PretendRows(Mixer(inlineAt));
        Dispatcher.UIThread.RunJobs();
        window.SelectSync(1, 9000);
        Dispatcher.UIThread.RunJobs();
        var drawing = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) { window.ShowDetails(force: true); Dispatcher.UIThread.RunJobs(); }
        Check($"drawing the threshold's {inlineAt} boxes stays inside one frame at 60 Hz",
              drawing.Elapsed.TotalMilliseconds / 20 < 16.0,
              $"{drawing.Elapsed.TotalMilliseconds / 20:0.00} ms a draw");

        // Back to the two-slot set, and the controls re-read off the pane it has just drawn:
        // the ones above belong to a panel that has been rebuilt six times since, and pressing
        // a detached button would be a check of a closure rather than of what is on screen.
        window.PretendRows(twice);
        Dispatcher.UIThread.RunJobs();
        window.SelectSync(1, 100);
        Dispatcher.UIThread.RunJobs();
        set = Boxes().Where(b => b.Label.StartsWith("1. media") ||
                                 b.Label.StartsWith("2. media")).ToList();
        members = Boxes().Where(b => b.Label.StartsWith("media ")).ToList();

        // Pressing the drawn glyph, which is the only way to prove where it goes. The rows
        // are built by hand and carry no audio, so the list's play path answers "no bytes
        // found" -- a sentence written in MainWindow.Browse.cs and nowhere else, which is
        // what makes it evidence that the box did not grow a second play path of its own.
        if (set.Count == 2)
        {
            Press(set[1].Play);
            Check("pressing a box's play button goes through the list's own play path",
                  status.Text == "media 200: no bytes found.", status.Text);
            Check("and the panel does not redraw under it, so a reader can walk down the set",
                  Boxes().Count == set.Count + members.Count,
                  $"{Boxes().Count} boxes now, {set.Count + members.Count} before");
            // The same medium through the hook the render harness and any later check reach
            // it by, which has to be the same route or it is a check of the harness.
            status.Text = "";
            Check("and the harness's own hook reaches it by that same path",
                  window.PlayMediaSync(200) && status.Text == "media 200: no bytes found.",
                  status.Text);
        }

        // A medium with no row on screen. The filter box is what puts it there here, because
        // it settles the same state without depending on which row kinds this machine has
        // switched off, and it is the state about a quarter of the boxes in a real family are
        // in before anybody types anything.
        filter.Text = "uitest_first";
        Dispatcher.UIThread.RunJobs();
        window.SelectSync(1, 100);
        Dispatcher.UIThread.RunJobs();
        var apart = Boxes().Where(b => b.Label.StartsWith("2. media")).ToList();
        Check("a box for a medium the list is not showing is drawn differently",
              apart.Count == 1 && apart[0].Label.EndsWith(" *") &&
              apart[0].Face is Button { IsEnabled: true },
              apart.Count == 1 ? apart[0].Label : $"{apart.Count} such boxes");
        // And never as one of the selected ones. A row the filter is holding back cannot be in
        // the list's selection, so a box that drew itself selected there would be claiming
        // something the list would contradict the moment anybody looked at it.
        Check("and never as one of the selected ones, which its row cannot be",
              apart.Count == 1 && !apart[0].Face.Classes.Contains("picked"),
              apart.Count == 1 ? string.Join(".", apart[0].Face.Classes) : "-");
        Check("and the boxes are still one per medium, because the set is the bank's and " +
              "not the list's",
              Boxes().Count(b => b.Label.StartsWith("1. media") ||
                                 b.Label.StartsWith("2. media")) == 2);
        if (apart.Count == 1)
        {
            Check("and says which half of it still works",
                  ToolTip.GetTip(apart[0].Face) is string why &&
                  why.Contains("cannot select it") && why.Contains("The play button still works"),
                  ToolTip.GetTip(apart[0].Face) as string);

            // Both halves pressed in that state, because this is the one place the two do
            // not agree and the box is built around them not agreeing.
            Press(apart[0].Play);
            Check("its play button still reaches the bank, which never asks what is on screen",
                  status.Text == "media 200: no bytes found.", status.Text);
            Press(apart[0].Face);
            Check("and its label says so rather than doing nothing",
                  status.Text == "none of those are in the list as it is filtered now.",
                  status.Text);
        }

        filter.Text = "";
        Dispatcher.UIThread.RunJobs();
        window.SelectSync(1, 100);
        Dispatcher.UIThread.RunJobs();
        drawn = Panel();

        W("");
        W("a box is a selection surface, and by the list's own rules");

        // What the LIST does with the three gestures, driven as real clicks on the rows it has
        // drawn. Measured here rather than assumed, because the boxes are built to copy it:
        // the gesture a reader has for a selection in this window is the one the grid gave
        // them, and a box obeying a rule of its own would be a second language for one act.
        //
        // With the grid in the Extended mode it defaults to (MainWindow.axaml sets no
        // SelectionMode on Grid_) it does this, and the third one is the one nobody would
        // guess: a plain click leaves the clicked row alone selected, a ctrl+click adds it or
        // takes it away again, and a shift+click ADDS the run from the last plainly or ctrl
        // clicked row while keeping whatever else was selected, so row 0, then ctrl row 2,
        // then shift row 3 leaves rows 0, 2 and 3 and not rows 2 and 3.
        var listed = grid.GetVisualDescendants().OfType<DataGridRow>()
                         .Where(r => r.DataContext is SoundRow)
                         .OrderBy(r => r.Index).ToList();
        Check("the list has drawn the four rows these gestures are measured on",
              listed.Count == 4, $"{listed.Count} rows realised");
        if (listed.Count == 4)
        {
            Click(listed[0], KeyModifiers.None);
            Check("a plain click on a row selects that row alone",
                  Held() == "uitest_first_play", Held());
            Click(listed[2], KeyModifiers.Shift);
            Check("shift+click takes the run from the last one clicked to this one",
                  Held() == "uitest_first_play,uitest_first_again_play,uitest_second_play",
                  Held());
            Click(listed[3], KeyModifiers.Control);
            Check("ctrl+click adds one more to it", grid.SelectedItems.Count == 4, Held());
            Click(listed[3], KeyModifiers.Control);
            Check("and ctrl+click on a selected row takes that one back out",
                  grid.SelectedItems.Count == 3, Held());
            Click(listed[0], KeyModifiers.None);
            Click(listed[2], KeyModifiers.Control);
            Click(listed[3], KeyModifiers.Shift);
            Check("a run is ADDED to what is selected rather than replacing it",
                  Held() == "uitest_first_play,uitest_second_play,uitest_second_again_play",
                  Held());
            // And with nothing selected there is nothing to measure a run from, so the grid
            // takes it as a plain click. A box with no anchor in the group it is drawn in does
            // the same, and this is where that rule comes from.
            grid.SelectedItems.Clear();
            Dispatcher.UIThread.RunJobs();
            Click(listed[2], KeyModifiers.Shift);
            Check("and a shift+click with nothing selected is a plain one",
                  Held() == "uitest_second_play", Held());
        }

        // The same three on the BOXES. One row per medium here, because the claims below are
        // about how many rows a click leaves selected and the fixture above gives every medium
        // two events, so one box there is legitimately two rows. Every row names the one
        // container, so the set the panel draws does not change as the clicks move down it:
        // the twin that makes it change is its own fixture further up.
        var runs = new SkinSounds();
        var aThird = new WwiseProps.Variation(0, 3, "random", 1 / 3.0, true, 444, []).Share;
        WwiseProps.Alternate[] threeSlots =
            [new(0, aThird, [810u]), new(1, aThird, [820u]), new(2, aThird, [830u])];
        foreach (var (media, slot) in new (uint, int)[] { (810, 0), (820, 1), (830, 2) })
            runs.Rows.Add(new SoundRow
            {
                Kind = "SFX", Bank = "bnk_sfx_1022001", MediaId = media,
                EventName = $"uitest_run_{media}_play", Codec = "VORBIS",
                Applied = new WwiseProps.Applied
                {
                    Alternative = new WwiseProps.Variation(slot, 3, "random", 1 / 3.0, true,
                                                           444, threeSlots),
                },
            });
        window.PretendRows(runs);
        Dispatcher.UIThread.RunJobs();
        window.SelectSync(1, 810);
        Dispatcher.UIThread.RunJobs();

        // Read as drawn controls throughout, for the reason at the top of this file: which of
        // its states a box is in is decided while it is being built, so asking the model would
        // be asking the wrong thing twice, and checks in this pane have twice passed while the
        // pane drew something else.
        List<(Button Play, Control Face, string Label)> Slots() =>
            Boxes().Where(b => b.Label.Contains(". media ")).ToList();
        string Lit() => string.Join(",", Slots().Where(b => b.Face.Classes.Contains("picked"))
                                                .Select(b => b.Label.Split("   ")[0]));

        Check("the set the bank states is three boxes to click", Slots().Count == 3,
              string.Join(" | ", Slots().Select(b => b.Label)));
        // Two of the three gestures leave no mark on screen, so the box has to say they are
        // there: a reader who has never ctrl+clicked a row of the list will not guess that a
        // box takes it, and this is the only place they are told.
        Check("and a box says in words which gesture does what to the selection",
              Slots().Count == 3 && ToolTip.GetTip(Slots()[0].Face) is string how &&
              how.Contains("Click selects") && how.Contains("Ctrl+click adds it") &&
              how.Contains("shift+click adds"),
              Slots().Count == 3 ? ToolTip.GetTip(Slots()[0].Face) as string : "-");
        if (Slots().Count == 3)
        {
            Click(Slots()[1].Face, KeyModifiers.None);
            Check("a plain click on a box leaves exactly that row selected and that box lit",
                  Held() == "uitest_run_820_play" && Lit() == "2. media 820",
                  $"{Held()} / lit {Lit()}");

            // The gesture in the user's own words: ctrl+click the others to select them all.
            // The panel is in its several-selected state from the second click onwards, which
            // is the state the boxes used not to be drawn in at all, so this is also the check
            // that they survive it.
            Click(Slots()[2].Face, KeyModifiers.Control);
            Check("two ctrl+clicks leave two boxes lit and the list holding exactly those rows",
                  Held() == "uitest_run_820_play,uitest_run_830_play" &&
                  Lit() == "2. media 820,3. media 830" && Slots().Count == 3,
                  $"{Held()} / lit {Lit()} / {Slots().Count} boxes");
            Click(Slots()[0].Face, KeyModifiers.Control);
            Check("and a third leaves the whole set selected, still three boxes",
                  Slots().Count == 3 && Slots().All(b => b.Face.Classes.Contains("picked")) &&
                  window.SelectedRows.Count == 3,
                  $"{Held()} / lit {Lit()} / {Slots().Count} boxes");
            Check("and the panel says so of the set rather than of one of its rows",
                  Panel().Contains("the whole of one set of 3 alternatives"),
                  Around(Panel(), "alternatives"));

            // Ctrl+click on a lit box takes it back out, the way it does on a row of the list.
            Click(Slots()[0].Face, KeyModifiers.Control);
            Check("ctrl+click on a lit box takes that one back out",
                  Held() == "uitest_run_820_play,uitest_run_830_play" &&
                  Lit() == "2. media 820,3. media 830",
                  $"{Held()} / lit {Lit()}");

            // A run, measured from the box the last ctrl+click landed on and added to what is
            // already selected, which is what the grid was just measured doing.
            Click(Slots()[2].Face, KeyModifiers.None);
            Click(Slots()[0].Face, KeyModifiers.Shift);
            Check("shift+click adds the run between the two boxes",
                  window.SelectedRows.Count == 3 &&
                  Lit() == "1. media 810,2. media 820,3. media 830",
                  $"{Held()} / lit {Lit()}");

            // The other direction of the same wire. A reader who uses the list rather than the
            // boxes must not be left looking at a pane that disagrees with it.
            var inList = grid.GetVisualDescendants().OfType<DataGridRow>()
                             .Where(r => r.DataContext is SoundRow)
                             .OrderBy(r => r.Index).ToList();
            if (inList.Count == 3)
            {
                Click(inList[0], KeyModifiers.None);
                Click(inList[2], KeyModifiers.Control);
                Check("selecting rows in the LIST lights the boxes for exactly those rows",
                      Held() == "uitest_run_810_play,uitest_run_830_play" &&
                      Lit() == "1. media 810,3. media 830",
                      $"{Held()} / lit {Lit()}");
            }
            else Check("the list has drawn the three rows to select from", false,
                       $"{inList.Count} rows realised");

            // Play is the other half of the box and has to stay its own affordance: a reader
            // hearing an alternative before deciding to stage it must not find the selection
            // moved under them. Pressed with ctrl held as well, because that is the modifier
            // the label half reads and the play half must not.
            var before = Held();
            Click(Slots()[1].Play, KeyModifiers.None);
            Check("pressing play on an unlit box plays it and does not move the selection",
                  Held() == before && Lit() == "1. media 810,3. media 830" &&
                  status.Text == "media 820: no bytes found.",
                  $"{Held()} / lit {Lit()} / {status.Text}");
            Click(Slots()[1].Play, KeyModifiers.Control);
            Check("and ctrl+pressing play does not add it either",
                  Held() == before && Lit() == "1. media 810,3. media 830",
                  $"{Held()} / lit {Lit()}");
        }

        W("");
        W("a click in the list moves the details and a double-click there plays; on a box, " +
          "a double-click is what moves them");

        // The two surfaces answer the same gestures differently on purpose. In the list a click
        // is how a reader picks the sound to read about and a double-click is how they hear it;
        // on a box a click only selects, because the box is drawn on the page it would otherwise
        // rewrite (MainWindow.Details.cs, _detailsAnchor), and a double-click is the one gesture
        // that moves the panel. Read off the panel's first line, which is the name of the sound
        // being described and is drawn first whatever else the panel holds.
        string Subject() => Lines().FirstOrDefault() ?? "";
        window.SelectSync(1, 810);
        Dispatcher.UIThread.RunJobs();
        var runRows = grid.GetVisualDescendants().OfType<DataGridRow>()
                          .Where(r => r.DataContext is SoundRow)
                          .OrderBy(r => r.Index).ToList();
        if (runRows.Count == 3 && Slots().Count == 3 && Subject() == "uitest_run_810_play")
        {
            Click(runRows[1], KeyModifiers.None);
            Check("a single click on a row in the list moves the details to that row",
                  Held() == "uitest_run_820_play" && Subject() == "uitest_run_820_play",
                  $"{Held()} selected, describing {Subject()}");
            status.Text = "";
            DoubleClick(runRows[2]);
            Check("a double-click on a row in the list plays it",
                  status.Text == "media 830: no bytes found." && Held() == "uitest_run_830_play",
                  $"{status.Text} / {Held()} selected");

            // The same two gestures on the boxes, with a real double-click rather than the
            // harness's GoToMediaSync, which the check further up has to use on its own. On a
            // box that is not selected yet, which is the case that matters: its first click
            // changes the selection and so redraws the panel before the second press arrives.
            window.SelectSync(1, 810);
            Dispatcher.UIThread.RunJobs();
            Click(Slots()[1].Face, KeyModifiers.None);
            var afterClick = Subject();
            DoubleClick(Slots()[2].Face);
            Check("a single click on a box leaves the details where they were, and a " +
                  "double-click moves them to that box's sound",
                  afterClick == "uitest_run_810_play" && Subject() == "uitest_run_830_play",
                  $"describing {afterClick} after the click, {Subject()} after the double-click");
        }
        else Check("the list and the panel have drawn the three rows and three boxes these " +
                   "gestures are measured on, anchored on the first", false,
                   $"{runRows.Count} rows, {Slots().Count} boxes, describing {Subject()}");

        // The end of the same count: a mixer whose rows are all one medium. Skin 1030001 has
        // 7 of these and they were printed as "one of 2 sounds", which is a group of one
        // described as a pair. Their rows carry no event name, so they are behind the row
        // filters and no render can reach them, which is why the claim is asserted here.
        var alone = new SkinSounds();
        foreach (var name in new[] { "uitest_alone_play", "uitest_alone_again_play" })
            alone.Rows.Add(new SoundRow
            {
                Kind = "VO", Bank = "bnk_vo_1030001", MediaId = 300, EventName = name,
                Codec = "VORBIS", Subtitle = "One sound, two events.",
                Applied = new WwiseProps.Applied { FamilyId = 888 },
                Family = "F2",
                FamilyMembers = ["> uitest_alone_play", "  uitest_alone_again_play"],
            });

        window.PretendRows(alone);
        Dispatcher.UIThread.RunJobs();
        window.SelectSync(1, 300);
        Dispatcher.UIThread.RunJobs();
        drawn = Panel();
        Check("a mixer holding one medium says so rather than counting it twice",
              drawn.Contains("The only sound on it") &&
              !drawn.Contains("One of 2 sounds"), Around(drawn, "sound on it"));

        W("");
        W("what the panel says about a set, a mixer and a silence it cannot see the whole of");

        // The multi-row answers, on the same hand-built set. Selecting every row of a
        // two-alternative container that two events reach used to read "the whole of one set
        // of 4 alternatives", and selecting one row per slot, which covers both alternatives,
        // used to warn in red that 2 members were left alone. 6 sets of skin 1030001 and 19 of
        // 1029001 carry more rows than the bank has alternatives.
        window.PretendRows(twice);
        Dispatcher.UIThread.RunJobs();
        var all4 = window.SelectSync(4);
        Dispatcher.UIThread.RunJobs();
        drawn = Panel();
        Check("every row of a set of two is the whole of a set of two, not of four",
              all4 == 4 && drawn.Contains("the whole of one set of 2 alternatives") &&
              !drawn.Contains("set of 4 alternatives"), Around(drawn, "alternatives"));

        // One row per slot: fewer rows, the same alternatives, and nothing left alone. Written
        // through the grid's own SelectedItems because the two rows wanted are not a prefix of
        // the list, and that collection is the one a click writes and the panel reads.
        var onePerSlot = (grid.ItemsSource as IEnumerable<SoundRow>)
                         .GroupBy(r => r.Applied.Alternative.Index)
                         .Select(g => g.First()).ToList();
        grid.SelectedItems.Clear();
        foreach (var r in onePerSlot) grid.SelectedItems.Add(r);
        Dispatcher.UIThread.RunJobs();
        window.ShowDetails(force: true);
        Dispatcher.UIThread.RunJobs();
        drawn = Panel();
        Check("a selection covering both alternatives is not warned that members are missing",
              onePerSlot.Count == 2 && drawn.Contains("the whole of one set of 2 alternatives") &&
              !drawn.Contains("left out"), Around(drawn, "alternatives"));

        // A mixer the bank gives more children than the list has rows for. The count the panel
        // prints is distinct media among the rows, and the index keeps one row per medium, so
        // a medium sitting under two mixers is attributed to one of them: mixer 34047687 of
        // skin 1030001 lists 147 Sound children, 10 reach a row, and the panel read "one of 10
        // sounds that share a mixer" and offered "Select all 10 of them".
        var deeper = new SkinSounds();
        foreach (var (media, name) in new (uint, string)[]
                 { (400, "uitest_deep_play"), (500, "uitest_deep_other_play") })
            deeper.Rows.Add(new SoundRow
            {
                Kind = "VO", Bank = "bnk_vo_1030001", MediaId = media, EventName = name,
                Codec = "VORBIS", Subtitle = "Two of a mixer's twenty.",
                Applied = new WwiseProps.Applied { FamilyId = 666, FamilySize = 20 },
                Family = "F3",
                FamilyMembers = ["> uitest_deep_play", "  uitest_deep_other_play"],
            });
        window.PretendRows(deeper);
        Dispatcher.UIThread.RunJobs();
        window.SelectSync(1, 400);
        Dispatcher.UIThread.RunJobs();
        drawn = Panel();
        Check("a mixer the bank gives more children than the list has rows for says so",
              drawn.Contains("2 of its 20 children are rows here") &&
              !drawn.Contains("One of 2 sounds on it"), Around(drawn, "children"));
        Check("and does not offer to select all of something it has not counted",
              !drawn.Contains("Select all "), Around(drawn, "Select "));

        // A silence a curve can lift. DrivenProps can only mark a property the object STORES,
        // so a curve on a level the object leaves at its default marks nothing and the row was
        // told that nothing raises it and that replacing the audio changes nothing anybody
        // will hear: 6 rows of skin 1029001 and 1 of 1034001 were given that instruction with
        // a Volume curve on a mixer above them.
        var muted = new SkinSounds();
        foreach (var (media, name) in new (uint, string)[]
                 { (600, "uitest_quiet_play"), (700, "uitest_quiet_other_play") })
        {
            var chain = new WwiseProps.Applied { LevelDriven = true, Rtpc = 1 };
            chain.Props.Add(new WwiseProps.Prop(0x00, "Volume", WwiseProps.Unit.Db, -96f, 0, "sound"));
            chain.Live.Add("Volume (on the 4th actor-mixer above it)");
            muted.Rows.Add(new SoundRow
            {
                Kind = "SFX", Bank = "bnk_sfx_1029001", MediaId = media, EventName = name,
                Codec = "VORBIS", Applied = chain,
            });
        }
        window.PretendRows(muted);
        Dispatcher.UIThread.RunJobs();
        window.SelectSync(1, 600);
        Dispatcher.UIThread.RunJobs();
        drawn = Panel();
        Check("a silence a curve on the level can lift is not written off",
              drawn.Contains("This sound is silent.") &&
              drawn.Contains("game parameter drives its level") &&
              !drawn.Contains("Nothing raises it again"), Around(drawn, "raise"));

        // And with a second row selected as well. The panel answers the SOUND now rather than
        // the selection (MainWindow.Details.cs, _detailsAnchor), so what has to survive is
        // both: this sound's own verdict, and that the selection carries silent rows.
        window.SelectSync(2);
        Dispatcher.UIThread.RunJobs();
        drawn = Panel();
        Check("and is not written off once a second row is selected either",
              drawn.Contains("uitest_quiet_play") &&
              drawn.Contains("2 rows selected over 2 distinct media, 2 of them silent") &&
              drawn.Contains("What the game does to it") &&
              !drawn.Contains("Nothing raises it again"), Around(drawn, "silent"));

        foreach (var f in new[] { one, again, earlier, nowhere, whileStaging, whileDetails })
            try { File.Delete(f); } catch { /* a leftover temp file must not fail the run */ }

        // The sentence a check is about, rather than the whole panel: these read one line out
        // of a page of prose, and printing the page for each of them buries the one that
        // failed in four copies of everything that did not.
        static string Around(string all, string what)
        {
            var at = all.IndexOf(what, StringComparison.Ordinal);
            return at < 0 ? $"nothing says \"{what}\""
                 : all.Substring(Math.Max(0, at - 60), Math.Min(160, all.Length - Math.Max(0, at - 60)));
        }

        // What the details sub-panel has DRAWN, for the same reason the staged rows are read
        // off their realised cells: the panel is built from the row, so reading the row back
        // would be comparing the row with itself.
        string Panel() => string.Join(" / ", Lines());

        // The same reading kept as lines, for the claims that are about how MANY lines the
        // panel drew: a list that prints every member twice reads correctly when it is
        // joined into one string, and is wrong on screen.
        List<string> Lines() => details.GetVisualDescendants().OfType<TextBlock>()
                                       .Select(t => t.Text ?? "")
                                       .Where(t => t.Length > 0).ToList();

        // Press a control the panel drew, by raising the event its own handler is on. The
        // window has no pointer here, and a check that called what the handler calls would
        // be a check that the handler was written the way the check assumes.
        void Press(Control what)
        {
            what.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        // A real click on a drawn control, with the modifiers a reader would be holding down.
        // Put in at the window's own input root rather than raised on the control, because the
        // modifiers are the thing being checked and they arrive on the pointer event: raising
        // Click alone is the gesture with no modifier, which Press above already covers.
        //
        // Scrolled into view first and taken 40 pixels in from the control's left edge, both
        // for the same reason: a click only lands where the control is actually drawn. This
        // window is 1100 by 600 when it is headless, a list row is wider than the column it is
        // drawn in, and the middle of one is over the details panel beside it, where the press
        // went to the panel and the selection never moved.
        void Click(Control what, KeyModifiers mods)
        {
            what.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            var on = what.TranslatePoint(new Point(Math.Min(40, what.Bounds.Width / 2),
                                                   what.Bounds.Height / 2), window) ?? default;
            var raw = (mods.HasFlag(KeyModifiers.Control)
                          ? RawInputModifiers.Control : RawInputModifiers.None) |
                      (mods.HasFlag(KeyModifiers.Shift)
                          ? RawInputModifiers.Shift : RawInputModifiers.None);
            window.MouseDown(on, MouseButton.Left, raw);
            window.MouseUp(on, MouseButton.Left, raw);
            Dispatcher.UIThread.RunJobs();
        }

        // Two real clicks on the same spot, inside the platform's double-click time, which is
        // how the window's own mouse turns them into a double-click. No modifiers, because the
        // double-clicks being checked are the plain ones on a row of the list and on a box.
        void DoubleClick(Control what)
        {
            what.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            var on = what.TranslatePoint(new Point(Math.Min(40, what.Bounds.Width / 2),
                                                   what.Bounds.Height / 2), window) ?? default;
            for (var i = 0; i < 2; i++)
            {
                window.MouseDown(on, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(on, MouseButton.Left, RawInputModifiers.None);
            }
            Dispatcher.UIThread.RunJobs();
        }

        // Which rows the list is holding, named and in the order it hands them back, because a
        // claim about a gesture is a claim about WHICH rows it left selected and not how many.
        string Held() => string.Join(",", grid.SelectedItems.OfType<SoundRow>()
                                              .Select(r => r.EventName));

        // Every box the panel has DRAWN, as the pair of controls each one is made of. Found
        // by the class the box carries rather than by the shape of two controls that happen
        // to sit beside each other, and read out of the visual tree for the reason at the top
        // of this file: which of the three states a box is in is decided while it is being
        // built, so asking the row about it would be asking the wrong thing twice.
        //
        // Face is the label half and the play half is the glyph; both are Buttons, and what
        // distinguishes a box's three states is what it carries rather than what it is: the
        // picked class where the list is holding its rows, a trailing star and muted text where
        // the list is not showing them at all.
        List<(Button Play, Control Face, string Label)> Boxes() =>
            details.GetVisualDescendants().OfType<Grid>()
                   .Where(g => g.Classes.Contains("mediabox") && g.Children.Count == 2)
                   .Select(g => (g.Children[0] as Button, g.Children[1],
                                 g.Children[1].GetVisualDescendants().OfType<TextBlock>()
                                              .FirstOrDefault()?.Text ?? ""))
                   .Where(b => b.Item1 is not null)
                   .ToList();

        // The pane's rows as the pane has actually DRAWN them: header to the text in the
        // realised cell. Reading ItemsSource instead was the hole in this file -- the pane
        // is handed the staged collection ITSELF, so a check that read it back compared the
        // list with the list and passed all the way through a period when the pane was
        // visibly drawing blank cells. A cell nobody drew, or drew with a stale value,
        // cannot get past this.
        List<Dictionary<string, string>> Drawn()
        {
            var headers = pending.Columns.Where(c => c.IsVisible)
                                 .OrderBy(c => c.DisplayIndex)
                                 .Select(c => c.Header?.ToString() ?? "").ToList();
            var drawn = new List<Dictionary<string, string>>();
            foreach (var row in pending.GetVisualDescendants().OfType<DataGridRow>()
                                       .Where(r => r.DataContext is PendingWem)
                                       .OrderBy(r => r.Index))
            {
                var cells = row.GetVisualDescendants().OfType<DataGridCell>()
                               .Select(c => c.GetVisualDescendants().OfType<TextBlock>()
                                             .FirstOrDefault()?.Text ?? "")
                               .ToList();
                var byHeader = new Dictionary<string, string>();
                for (var i = 0; i < headers.Count && i < cells.Count; i++)
                    byHeader[headers[i]] = cells[i];
                drawn.Add(byHeader);
            }
            return drawn;
        }

        static string Describe(List<Dictionary<string, string>> drawn) =>
            drawn.Count == 0 ? "nothing"
            : string.Join(" / ", drawn.Select(r => string.Join(" ", r.Select(c => $"[{c.Value}]"))));

        // ColumnFits was written for exactly this and called by nobody, which is part of
        // why the pane shipped with two columns clipped to a single letter. Both grids, so
        // one check covers the list and the pane.
        void Fits(string at)
        {
            W($"    at {at}:");
            var all = window.ColumnFits().Concat(window.PendingColumnFits()).ToList();
            foreach (var f in all) W($"      {(f.Fits ? "fits " : "CLIPS")}  {f}");
            Check($"nothing clips at {at}", all.Count > 0 && all.All(f => f.Fits),
                  all.Count == 0 ? "nothing could be measured"
                  : string.Join("; ", all.Where(f => !f.Fits)));
        }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// A quarter of a second of 16-bit mono at 48 kHz, so the check above stages through
    /// the real conversion rather than around it. Written here rather than borrowed: the
    /// engine's own wav writer is private to its self-test, and a UI check reaching into
    /// one would tie the two together for twenty lines.
    /// </summary>
    private static byte[] Wav(int samples)
    {
        const int rate = 48000;
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(i * 2, 2), (short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 8000));

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(4 + 8 + 16 + 8 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(rate);
        w.Write(rate * 2);          // bytes per second
        w.Write((short)2);          // block align
        w.Write((short)16);         // bits
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
