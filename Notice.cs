using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MRAudioKit;

/// <summary>
/// A warning the author can switch off.
///
/// Some things are worth saying once and then never again: that a second mod has taken
/// over a .bnk the first one owned, that a build is layering onto a loaded mod rather
/// than the game's original. Said every time they become noise, and noise is what
/// teaches people to click through warnings without reading them. WPF's MessageBox
/// cannot carry a checkbox, so this is the smallest dialog that can.
/// </summary>
public static class Notice
{
    /// <summary>
    /// Show a warning unless the author has silenced this particular one.
    /// Returns false only when they actively cancel.
    /// </summary>
    /// <param name="key">
    /// Stable id for this warning, stored in settings. Never reuse one for a different
    /// message: someone who silenced the old meaning did not agree to the new one.
    /// </param>
    public static bool Show(Window owner, Settings settings, string key,
                            string title, string message, bool cancellable = true)
    {
        if (Notices.IsSuppressed(settings, key)) return true;

        Brush B(string k) => (Brush)Application.Current.Resources[k];

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("Fg"),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var again = new CheckBox
        {
            Content = "Don't show this again",
            Foreground = B("Muted"),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var ok = new Button { Content = "OK", Width = 92, IsDefault = true };
        var cancel = new Button
        {
            Content = "Cancel", Width = 92, Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true, Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(text);
        body.Children.Add(again);
        body.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Content = body,
            SizeToContent = SizeToContent.Height,
            Width = 470,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = B("Bg"),
        };
        ok.Click += (_, _) => { win.DialogResult = true; };

        var accepted = win.ShowDialog() == true;

        // Only remember the choice when they accepted it. Silencing a warning someone
        // cancelled out of would suppress it for a decision they had refused.
        if (accepted && again.IsChecked == true) Notices.Suppress(settings, key);
        return accepted;
    }

    /// <summary>
    /// The keys and their wording live in Notices, in the engine, because the settings
    /// page that offers to bring a warning back is in a different window from the code
    /// that raises it and both have to mean the same thing.
    /// </summary>
    public const string ModReplaced = Notices.ModReplaced;
    public const string BuildOnMod = Notices.BuildOnMod;

    public static string Describe(string key) => Notices.Describe(key);
}
