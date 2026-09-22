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
        if (settings?.SuppressedWarnings?.Contains(key, StringComparer.OrdinalIgnoreCase) == true)
            return true;

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
        if (accepted && again.IsChecked == true && settings is not null)
        {
            settings.SuppressedWarnings ??= [];
            if (!settings.SuppressedWarnings.Contains(key, StringComparer.OrdinalIgnoreCase))
                settings.SuppressedWarnings.Add(key);
            settings.Save();
        }
        return accepted;
    }

    /// <summary>Keys, in one place so a typo cannot silently create a second warning.</summary>
    public const string ModReplaced = "mod-replaced-an-already-loaded-bnk";
    public const string BuildOnMod = "build-uses-a-loaded-mod-as-its-base";

    /// <summary>
    /// What a key means, for the list in settings. A key is written to be stable, not
    /// to be read, and "mod-replaced-an-already-loaded-bnk" is not an explanation.
    /// </summary>
    public static string Describe(string key) => key switch
    {
        ModReplaced => "Loading a .bnk another mod had already claimed replaces it.",
        BuildOnMod  => "Building while a mod is loaded uses that mod as the base.",
        _ => key,
    };
}
