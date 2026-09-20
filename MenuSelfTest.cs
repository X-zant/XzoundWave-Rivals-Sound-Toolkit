using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MRAudioKit;

/// <summary>
/// Checks that the app's own menu template can actually draw a submenu.
///
/// This exists because it could not. The dark template was written when every menu
/// item in the app was a single row, so it rendered the header and nothing else: no
/// popup, no items host, no check mark. An item with children therefore had nowhere
/// to put them, its submenu never opened, and the SubmenuOpened handler that was
/// supposed to fill it in never ran. Nothing in the C# was wrong, which is exactly
/// why it took a second look to find.
/// </summary>
public static class MenuSelfTest
{
    /// <summary>XzoundWave.exe --menutest</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        W("menu template");
        var style = Application.Current?.TryFindResource(typeof(MenuItem)) as Style;
        Check("the app styles MenuItem at all", style is not null);
        if (style is null) { W(""); W("1 CHECK(S) FAILED"); return 1; }

        var template = style.Setters.OfType<Setter>()
            .FirstOrDefault(s => s.Property == Control.TemplateProperty)?.Value as ControlTemplate;
        Check("it replaces the control template", template is not null);
        if (template is null) { W(""); W("1 CHECK(S) FAILED"); return 1; }

        var root = template.LoadContent();
        var parts = Walk(root).ToList();
        W($"  template contains {parts.Count} element(s)");

        var popup = parts.OfType<Popup>().FirstOrDefault();
        Check("there is a popup for the submenu", popup is not null);
        Check("the popup is named PART_Popup", popup?.Name == "PART_Popup", popup?.Name);

        var host = parts.OfType<Panel>().FirstOrDefault(p => p.IsItemsHost);
        Check("child items have somewhere to go", host is not null);
        Check("the items host is inside the popup",
            host is not null && popup is not null && IsUnder(host, popup));

        Check("the header is still presented",
            parts.OfType<ContentPresenter>().Any(c => c.ContentSource == "Header"));
        Check("a check mark exists for tickable items",
            parts.OfType<FrameworkElement>().Any(e => e.Name == "Check"));
        Check("a submenu arrow exists",
            parts.OfType<FrameworkElement>().Any(e => e.Name == "Arrow"));

        // The triggers are what stop the tick and arrow from re-introducing the icon
        // gutter this template was written to remove.
        var triggered = template.Triggers.OfType<Trigger>().Select(t => t.Property.Name).ToList();
        W($"  triggers on: {string.Join(", ", triggered)}");
        Check("the arrow appears only when there are items", triggered.Contains("HasItems"));
        Check("the tick column is reserved only when checkable", triggered.Contains("IsCheckable"));
        Check("a ticked item shows its tick", triggered.Contains("IsChecked"));

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject node)
    {
        if (node is null) yield break;
        yield return node;

        // Both trees, because a Popup's content hangs off the logical tree only until
        // the popup is opened -- which is never going to happen without a window.
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var d in Walk(child)) yield return d;

        var n = node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < n; i++)
            foreach (var d in Walk(VisualTreeHelper.GetChild(node, i))) yield return d;
    }

    private static bool IsUnder(DependencyObject node, DependencyObject ancestor)
    {
        for (var p = node; p is not null; p = LogicalTreeHelper.GetParent(p))
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }
}
