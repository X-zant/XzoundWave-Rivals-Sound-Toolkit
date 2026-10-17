using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace MRAudioKit.UI;

/// <summary>What a toolbar button is: a label, what it does, and whether it is the big one.</summary>
/// <param name="Menu">Items of a dropdown: the button opens them instead of running anything. "-" is a separator.</param>
/// <param name="Checked">For a dropdown item that is a setting: shown ticked or not.</param>
public sealed record ToolButton(string Text, Action Run, bool Key = false, string Hint = null,
                                IReadOnlyList<ToolButton> Menu = null, bool? Checked = null)
{
    public static ToolButton Separator => new("-", null);
}

/// <summary>
/// The parts every tab needs and none of them should reimplement.
///
/// The old window had twenty-six file dialogs constructed inline, fifty MessageBox
/// calls and a hundred status writes, each spelled slightly differently. Here the
/// shell owns the ways of talking to someone and each tab owns only its own work,
/// which is also what lets the tabs live in separate files.
/// </summary>
public partial class MainWindow
{
    /// <summary>The rows the list has selected, which is what most actions act on.</summary>
    public List<SoundRow> SelectedRows =>
        Grid_.SelectedItems?.OfType<SoundRow>().ToList() ?? [];

    /// <summary>The one row, for actions that only make sense on one.</summary>
    public SoundRow CurrentRow => Grid_.SelectedItem as SoundRow;

    public SkinSounds Sounds => _sounds;
    public GameSession Session => _session;
    public Settings Config => _settings;
    public ModSet Mods => _mods;

    /// <summary>The .bnk files the tree has picked, by game path.</summary>
    public List<string> PickedBankPaths()
    {
        var want = _picked.Where(n => n.Bank is not null)
                          .Select(n => n.Bank)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var paths = new List<string>();
        foreach (var skin in _picked.Where(n => n.Skin is not null)
                                    .Select(n => n.Skin).DistinctBy(s => s.SkinId))
            foreach (var b in skin.Banks)
                if (want.Count == 0 || want.Contains(Path.GetFileName(b.Path)))
                    paths.Add(b.Path);

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The skin the selection belongs to, or null when it spans several.</summary>
    public SkinEntry PickedSkin =>
        _picked.Where(n => n.Skin is not null).Select(n => n.Skin)
               .DistinctBy(s => s.SkinId).ToList() is { Count: 1 } one ? one[0] : null;

    // ---- saying things ------------------------------------------------------

    /// <summary>The status line. Every outcome goes here; only a question gets a dialog.</summary>
    public void Say(string message) => Status.Text = message;

    /// <summary>
    /// A message someone has to acknowledge. Rare on purpose: the old window answered
    /// with both a status line and a dialog for the same outcome, forty-six times.
    /// </summary>
    public async Task Tell(string title, string body)
    {
        // The render harness: a dialog there would wait for a click that never comes.
        if (ToldForShot is not null) { ToldForShot.Add($"{title}: {body}"); return; }
        var ok = new Button { Content = "OK", Width = 90, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var panel = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(new TextBlock { Text = body, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 520 });
        panel.Children.Add(ok);

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    /// <summary>The render harness: what <see cref="Tell"/> would have shown, instead of showing it.</summary>
    internal List<string> ToldForShot;

    /// <summary>The render harness's answer to a yes-or-no, by its title: nobody is there to click one.</summary>
    internal Func<string, bool> AnswerForShot;

    /// <summary>A yes or no. Returns true only on an explicit yes.</summary>
    public async Task<bool> Ask(string title, string body, string yes = "Yes", string no = "Cancel")
    {
        if (AnswerForShot is not null) return AnswerForShot(title);
        var answer = false;
        var yesButton = new Button { Content = yes, Width = 100 };
        var noButton = new Button { Content = no, Width = 100 };

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        buttons.Children.Add(yesButton);
        buttons.Children.Add(noButton);

        var panel = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(new TextBlock { Text = body, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 520 });
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        yesButton.Click += (_, _) => { answer = true; dialog.Close(); };
        noButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return answer;
    }

    // ---- picking files ------------------------------------------------------

    /// <summary>A folder, or null if they cancelled.</summary>
    public async Task<string> PickFolder(string title)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.Count > 0 ? picked[0].Path.LocalPath : null;
    }

    /// <summary>A file to read, or null. Patterns are like "*.bnk".</summary>
    public async Task<string> PickFile(string title, string kind, params string[] patterns)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = Filters(kind, patterns),
        });
        return picked.Count > 0 ? picked[0].Path.LocalPath : null;
    }

    /// <summary>Several files to read, empty if they cancelled.</summary>
    public async Task<List<string>> PickFiles(string title, string kind, params string[] patterns)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = Filters(kind, patterns),
        });
        return picked.Select(f => f.Path.LocalPath).ToList();
    }

    /// <summary>A file to write, or null.</summary>
    public async Task<string> SaveFile(string title, string suggested, string kind, params string[] patterns)
    {
        var picked = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggested,
            FileTypeChoices = Filters(kind, patterns),
        });
        return picked?.Path.LocalPath;
    }

    private static List<FilePickerFileType> Filters(string kind, string[] patterns) =>
    [
        new(kind) { Patterns = patterns },
        new("All files") { Patterns = ["*"] },
    ];

    // ---- long work ----------------------------------------------------------

    /// <summary>
    /// Run something slow off the drawing thread, with the status line saying so.
    /// Every build and every bank read goes through here, which is the only reason
    /// the window stays answerable while a hundred .bnk files are rewritten.
    /// </summary>
    public async Task<T> Working<T>(string what, Func<T> work)
    {
        Say(what + "…");
        try
        {
            var result = await Task.Run(work);
            return result;
        }
        catch (Exception ex)
        {
            Say($"{what} failed: {ex.Message}");
            return default;
        }
    }

    /// <summary>Redraw everything that reads from the rows. One entry point, on purpose.</summary>
    public void Refresh()
    {
        var rows = Grid_.ItemsSource;
        Grid_.ItemsSource = null;
        Grid_.ItemsSource = rows;
        Counts();
    }
}
