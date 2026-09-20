using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MRAudioKit;

/// <summary>
/// One place for preferences about the tool, as opposed to the Setup tab, which is
/// about where the game lives. Changes apply as they are ticked and are saved
/// immediately -- there is no OK button to forget to press, and nothing here is
/// destructive enough to need one.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly DataGrid _grid;
    private readonly Action _changed;

    /// <param name="grid">The sound list, so the column page reflects what is really there.</param>
    /// <param name="changed">Re-runs the filter and redraws the counts.</param>
    public SettingsWindow(Settings settings, DataGrid grid, Action changed)
    {
        InitializeComponent();
        _settings = settings;
        _grid = grid;
        _changed = changed;

        foreach (var name in new[] { "Rows", "Columns", "List help", "Tools", "Diagnostics" })
            Categories.Items.Add(name);
        Categories.SelectedIndex = 0;
    }

    private void Categories_Changed(object sender, SelectionChangedEventArgs e) => Build();

    private void Build()
    {
        PaneBody.Children.Clear();
        switch (Categories.SelectedItem as string)
        {
            case "Rows": BuildRows(); break;
            case "Columns": BuildColumns(); break;
            case "Tools": BuildTools(); break;
            case "Diagnostics": BuildDiagnostics(); break;
            default: BuildHelp(); break;
        }
        RefreshSummary();
    }

    // ---- rows ------------------------------------------------------------------

    private void BuildRows()
    {
        PaneTitle.Text = "Which rows the list shows";
        PaneHint.Text =
            "Turn a kind off and those rows disappear from the list everywhere — the counts, " +
            "Export to CSV and Autoplay all follow what is on screen. Nothing is deleted, and " +
            "a build still writes every sound in the bank.";

        var hidden = _settings.HiddenRowClasses ??= [.. RowClasses.HiddenByDefault];
        foreach (var cls in RowClasses.All)
        {
            var box = new CheckBox
            {
                Content = "Show " + Lower(cls.Label),
                IsChecked = !hidden.Contains(cls.Key, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 6, 0, 0),
            };
            var key = cls.Key;
            box.Click += (_, _) =>
            {
                hidden.RemoveAll(h => h.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (box.IsChecked != true) hidden.Add(key);
                _settings.Save();
                _changed();
                RefreshSummary();
            };
            PaneBody.Children.Add(box);
            PaneBody.Children.Add(new TextBlock
            {
                Text = cls.Meaning,
                Foreground = (Brush)FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(22, 1, 0, 0),
            });
        }
    }

    // ---- columns ---------------------------------------------------------------

    private void BuildColumns()
    {
        PaneTitle.Text = "Which columns the list shows";
        PaneHint.Text = "The same list as the Columns button above the grid.";

        var hidden = _settings.HiddenColumns ??= [];
        foreach (var column in _grid.Columns)
        {
            if (column.Header is not string header || header.Length == 0) continue;
            var box = new CheckBox
            {
                Content = header,
                IsChecked = column.Visibility == Visibility.Visible,
                Margin = new Thickness(0, 6, 0, 0),
            };
            var col = column;
            box.Click += (_, _) =>
            {
                var on = box.IsChecked == true;
                col.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                hidden.RemoveAll(h => h.Equals(header, StringComparison.OrdinalIgnoreCase));
                if (!on) hidden.Add(header);
                _settings.Save();
                RefreshSummary();
            };
            PaneBody.Children.Add(box);
        }
    }

    // ---- tools -----------------------------------------------------------------

    /// <summary>
    /// vgmstream has no field on the Setup tab any more: it ships inside the exe and
    /// unpacks itself, so asking for it would be asking for something already present.
    /// The override lives here because it is a real need for two small groups -- anyone
    /// wanting a newer vgmstream than the one bundled, and anyone exercising the right
    /// the LGPL gives them to substitute their own build of its libraries.
    /// </summary>
    private void BuildTools()
    {
        PaneTitle.Text = "vgmstream";
        PaneHint.Text =
            "Used for playback, measuring durations, and decoding Vorbis before a volume " +
            "change. A copy ships inside XzoundWave and unpacks itself the first time it " +
            "is needed, so there is nothing to install.";

        var bundled = Bundled.HasVgmstream;
        var custom = !string.IsNullOrWhiteSpace(_settings.VgmstreamPath)
                     && !_settings.VgmstreamPath.StartsWith(Bundled.ToolsDir,
                            StringComparison.OrdinalIgnoreCase);

        Add(new TextBlock
        {
            Text = custom
                ? "In use: your own copy" + Environment.NewLine + _settings.VgmstreamPath
                : (bundled
                    ? "In use: the bundled copy" + Environment.NewLine + Bundled.VgmstreamExe
                    : "Not unpacked yet — it appears the first time it is needed."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12),
        });

        var pick = new Button { Content = "Use my own vgmstream-cli.exe…", HorizontalAlignment = HorizontalAlignment.Left };
        pick.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "vgmstream-cli.exe",
                Filter = "vgmstream-cli|vgmstream-cli.exe|Programs|*.exe|All files|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            _settings.VgmstreamPath = dlg.FileName;
            _settings.Save();
            _changed();
            Build();
        };
        Add(pick);

        if (custom)
        {
            var revert = new Button
            {
                Content = "Go back to the bundled copy",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0),
            };
            revert.Click += (_, _) =>
            {
                _settings.VgmstreamPath = Bundled.EnsureVgmstream(out _) ?? "";
                _settings.Save();
                _changed();
                Build();
            };
            Add(revert);
        }

        Add(new TextBlock
        {
            Text = "The unpacked files, including vgmstream's own licence, are in:"
                   + Environment.NewLine + Bundled.ToolsDir,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        });
    }

    private void Add(UIElement e) => PaneBody.Children.Add(e);

    // ---- diagnostics -----------------------------------------------------------

    /// <summary>
    /// The same report as the --doctor verb, reachable without a command line.
    /// Someone whose audio will not play is exactly the person least able to run a
    /// console command and read its output, and this application is windowed, so
    /// there is no console to read anyway.
    /// </summary>
    private void BuildDiagnostics()
    {
        PaneTitle.Text = "Diagnostics";
        PaneHint.Text =
            "What this machine provides, and anything that looks wrong. Copy this into " +
            "a bug report if something is not working.";

        var text = EnvCheck.Report();

        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Height = 260,
            Margin = new Thickness(0, 4, 0, 8),
        };
        Add(box);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var copy = new Button { Content = "Copy to clipboard" };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(box.Text); Summary.Text = "diagnostics copied."; }
            catch { Summary.Text = "could not reach the clipboard."; }
        };
        row.Children.Add(copy);

        var save = new Button { Content = "Save as a file…" };
        save.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "XzoundWave-diagnostics.txt",
                Filter = "Text|*.txt|All files|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, box.Text);
                Summary.Text = "saved to " + dlg.FileName;
            }
            catch (Exception ex) { Summary.Text = ex.Message; }
        };
        row.Children.Add(save);

        var refresh = new Button { Content = "Run again" };
        refresh.Click += (_, _) => Build();
        row.Children.Add(refresh);
        Add(row);
    }

    // ---- help ------------------------------------------------------------------

    private void BuildHelp()
    {
        PaneTitle.Text = "Explanations in the window";
        PaneHint.Text = "Things that are worth reading once and then only take up room.";

        var drop = new CheckBox
        {
            Content = "Show the custom audio naming rule in the drop pane",
            IsChecked = _settings.ShowDropHelp,
            Margin = new Thickness(0, 6, 0, 0),
        };
        drop.Click += (_, _) =>
        {
            _settings.ShowDropHelp = drop.IsChecked == true;
            _settings.Save();
            _changed();
        };
        PaneBody.Children.Add(drop);

        PaneBody.Children.Add(new TextBlock
        {
            Text = "Where the game, the AES key and the usmap live is on the Setup tab, " +
                   "because those are about your installation rather than about the tool.",
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        });
    }

    // ---- chrome ----------------------------------------------------------------

    private void RefreshSummary()
    {
        var rows = (_settings.HiddenRowClasses ??= [.. RowClasses.HiddenByDefault])
            .Select(RowClasses.Find).Where(c => c is not null).ToList();
        var cols = _settings.HiddenColumns ?? [];
        Summary.Text = rows.Count == 0 && cols.Count == 0
            ? "showing everything"
            : $"hiding {rows.Count} kind(s) of row" +
              (rows.Count > 0 ? $" ({string.Join(", ", rows.Select(r => Lower(r.Label)))})" : "") +
              $" and {cols.Count} column(s)";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        switch (Categories.SelectedItem as string)
        {
            case "Rows":
                _settings.HiddenRowClasses = [.. RowClasses.HiddenByDefault];
                break;
            case "Columns":
                // Put the grid back in step with the defaults, not just the setting.
                _settings.HiddenColumns = [.. MainWindow.HiddenColumnsByDefault];
                foreach (var c in _grid.Columns)
                    if (c.Header is string h && h.Length > 0)
                        c.Visibility = _settings.HiddenColumns.Contains(h, StringComparer.OrdinalIgnoreCase)
                            ? Visibility.Collapsed : Visibility.Visible;
                break;
            default:
                _settings.ShowDropHelp = true;
                break;
        }
        _settings.Save();
        _changed();
        Build();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>"Unnamed events" reads badly after "Show ", and only the first word changes.</summary>
    private static string Lower(string label) =>
        label.Length > 0 && char.IsUpper(label[0]) && !label.StartsWith("PCM")
            ? char.ToLowerInvariant(label[0]) + label[1..]
            : label;
}
