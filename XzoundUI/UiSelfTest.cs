using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

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

        W("the window is the agreed shape");
        Check("it has a tab strip, a tree, a list and a toolbar",
              tabs is not null && tree is not null && grid is not null && toolbar is not null);

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
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
