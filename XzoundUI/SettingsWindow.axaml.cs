using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
// SetTextAsync is an extension on IClipboard rather than a method of it, so the
// clipboard cannot be written without this one.
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace MRAudioKit.UI;

/// <summary>
/// One place for preferences about the tool, as opposed to the Setup tab, which is
/// about where the game lives.
///
/// Changes apply as they are ticked and are saved immediately. There is no OK button
/// to forget to press, and nothing here is destructive enough to need one. The single
/// destructive choice, replacing an imported file with the .wem it converted to, asks
/// before it is switched on, which is where the decision is actually made and the last
/// point at which it can still be taken back.
/// </summary>
public partial class SettingsWindow : Window
{
    // InitializeComponent is generated from the .axaml. Writing one by hand shadows
    // it: the XAML loads, every x:Name field stays null, and the first line that
    // touches a control throws.

    private readonly Settings _settings;
    private readonly DataGrid _grid;
    private readonly Action _changed;

    /// <summary>
    /// The pages, in the order they are offered. Rows and Columns first because they
    /// are what someone comes here for; Diagnostics last because it is what someone is
    /// sent here for.
    /// </summary>
    private static readonly string[] Pages =
        ["Rows", "Columns", "Importing", "Warnings", "List help", "Tools", "Diagnostics"];

    /// <param name="settings">Written and saved as each control is used.</param>
    /// <param name="grid">The sound list, so the column page reflects what is really there.</param>
    /// <param name="changed">Re-runs the filter and redraws the counts.</param>
    public SettingsWindow(Settings settings, DataGrid grid, Action changed)
    {
        InitializeComponent();
        _settings = settings;
        _grid = grid;
        _changed = changed;

        Categories.ItemsSource = Pages;
        Categories.SelectionChanged += (_, _) => Build();
        BtnReset.Click += (_, _) => Reset();
        BtnClose.Click += (_, _) => Close();
        // Selecting raises the event, which builds the first page.
        Categories.SelectedIndex = 0;
    }

    private void Build()
    {
        PaneBody.Children.Clear();
        switch (Categories.SelectedItem as string)
        {
            case "Rows": BuildRows(); break;
            case "Columns": BuildColumns(); break;
            case "Importing": BuildImporting(); break;
            case "Warnings": BuildWarnings(); break;
            case "Tools": BuildTools(); break;
            case "Diagnostics": BuildDiagnostics(); break;
            default: BuildHelp(); break;
        }
        RefreshSummary();
    }

    private void Add(Control e) => PaneBody.Children.Add(e);

    /// <summary>An indented paragraph under the control it belongs to.</summary>
    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = Palette.Muted,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(22, 2, 0, 0),
    };

    // ---- rows ------------------------------------------------------------------

    /// <summary>
    /// The row kinds are a persisted preference rather than part of the filter box, so
    /// this page is the only place they can be seen rather than remembered. Turning one
    /// off removes those rows from the list everywhere, which is why the counts by the
    /// filter box have to be redrawn as well: a list that is withholding half of itself
    /// must not look like a list that is empty.
    /// </summary>
    private void BuildRows()
    {
        PaneTitle.Text = "Which rows the list shows";
        PaneHint.Text =
            "Turn a kind off and those rows disappear from the list everywhere — the counts, " +
            "Export to CSV and Autoplay all follow what is on screen. Nothing is deleted, and " +
            "a build still writes every sound in the .bnk.";

        // Null means "never chosen", which is not the same as "nothing hidden": it is
        // what lets a first run start with the unnamed events already out of the way.
        var hidden = _settings.HiddenRowClasses ??= [.. RowClasses.HiddenByDefault];

        // Not the kinds about tags, which the window cannot set for now (RowClasses.Withheld).
        foreach (var cls in RowClasses.Offered)
        {
            var box = new CheckBox
            {
                Content = "Show " + Lower(cls.Label),
                IsChecked = !hidden.Contains(cls.Key, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 6, 0, 0),
            };
            var key = cls.Key;
            // Click, not IsCheckedChanged: only a click means somebody decided, and a
            // handler that also fires when the code sets the box can recurse.
            box.Click += (_, _) =>
            {
                hidden.RemoveAll(h => h.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (box.IsChecked != true) hidden.Add(key);
                _settings.Save();
                _changed();
                RefreshSummary();
            };
            Add(box);
            Add(new TextBlock
            {
                Text = cls.Meaning,
                Foreground = Palette.Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(22, 1, 0, 0),
            });
        }
    }

    // ---- columns ---------------------------------------------------------------

    /// <summary>
    /// Hidden on a first run, and the set this page's Reset returns to. It mirrors the
    /// window's own first run set; it is restated here because that one is private to a
    /// file this window does not own.
    /// </summary>
    private static readonly string[] HiddenColumnsByDefault = [".bnk"];

    /// <summary>
    /// The grid's real columns, read live rather than listed, so this page cannot offer
    /// a column the list does not have.
    ///
    /// A column is stored by its HEADER TEXT, which makes the words on screen a settings
    /// key: rename one and everybody who had turned that column off silently gets it
    /// back, because the name they stored no longer matches anything. The window carries
    /// a renames table that rewrites the old name when settings are loaded, and the rule
    /// is that renaming a header here means adding a pair to it. The headers as they
    /// stand are Mod, .bnk, Event, Media ID, Category, Subtitle / line, Test #, Vol, Notes
    /// and Len. Tags was one until it was withdrawn for the community database; its entry is
    /// left alone like any other absent column's.
    ///
    /// An entry naming a column that no longer exists is left alone rather than tidied
    /// away: an absent column can be neither shown nor hidden, and dropping the entry
    /// would strand the preference if the column ever came back.
    /// </summary>
    private void BuildColumns()
    {
        PaneTitle.Text = "Which columns the list shows";
        PaneHint.Text = "The same list as the Columns button above the grid.";

        var hidden = _settings.HiddenColumns ??= [.. HiddenColumnsByDefault];
        foreach (var column in _grid.Columns)
        {
            var header = column.Header?.ToString();
            if (string.IsNullOrEmpty(header)) continue;

            var box = new CheckBox
            {
                Content = header,
                IsChecked = column.IsVisible,
                Margin = new Thickness(0, 6, 0, 0),
            };
            var col = column;
            box.Click += (_, _) =>
            {
                var on = box.IsChecked == true;
                col.IsVisible = on;
                hidden.RemoveAll(h => h.Equals(header, StringComparison.OrdinalIgnoreCase));
                if (!on) hidden.Add(header);
                _settings.Save();
                RefreshSummary();
            };
            Add(box);
        }
    }

    // ---- importing -------------------------------------------------------------

    /// <summary>
    /// What happens to audio on the way in. Encoding is the slow step and the only one
    /// worth avoiding twice, so this page is about not paying for it again.
    /// </summary>
    private void BuildImporting()
    {
        PaneTitle.Text = "Importing audio";
        PaneHint.Text =
            "Anything that is not already Wwise Vorbis is encoded when you stage it. " +
            "That is the slow part of a large drop, and a file that has not changed " +
            "always encodes to the same thing.";

        var keep = new CheckBox
        {
            Content = "Remember conversions, so the same file is never encoded twice",
            IsChecked = _settings.CacheConvertedAudio,
            Margin = new Thickness(0, 6, 0, 0),
        };
        keep.Click += (_, _) =>
        {
            _settings.CacheConvertedAudio = keep.IsChecked == true;
            _settings.Save();
            Build();
        };
        Add(keep);
        Add(Note("Matched on the contents of the file, not its name, so a copy or a " +
                 "rename still counts as the same audio and an edited file correctly " +
                 "does not."));

        var stats = ConvertedCache.Stats(_settings);
        Add(new TextBlock
        {
            Text = $"Holding {stats.Files:N0} conversion(s), {stats.Bytes / 1024.0 / 1024.0:0.0} MB" +
                   Environment.NewLine + ConvertedCache.Dir(_settings),
            Foreground = Palette.Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 8, 0, 8),
        });

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(22, 0, 0, 0),
        };

        var move = Tool("Keep them somewhere else…");
        move.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Folder for converted audio",
                AllowMultiple = false,
            });
            if (picked.Count == 0) return;
            _settings.ConvertedCacheDir = picked[0].Path.LocalPath;
            _settings.Save();
            Build();
        };
        row.Children.Add(move);

        var clear = Tool("Clear");
        clear.Click += (_, _) =>
        {
            // Nothing here cannot be recreated, so this needs no dire warning.
            var n = ConvertedCache.Clear(_settings);
            // After the rebuild, not before it: Build ends by rewriting the summary.
            Build();
            Summary.Text = $"cleared {n:N0} stored conversion(s).";
        };
        row.Children.Add(clear);
        Add(row);

        // ---- the destructive one -----------------------------------------------
        Add(new TextBlock
        {
            Text = "Replacing your files",
            FontWeight = FontWeight.Bold,
            Foreground = Palette.Fg,
            Margin = new Thickness(0, 20, 0, 0),
        });

        var replace = new CheckBox
        {
            Content = "Replace imported files with the converted .wem",
            IsChecked = _settings.ReplaceSourceWithWem,
            // The one red thing in the window. It deletes files, and it should not
            // look like the checkbox above it.
            Foreground = Palette.TagRed,
            Margin = new Thickness(0, 6, 0, 0),
        };
        replace.Click += async (_, _) =>
        {
            if (replace.IsChecked == true)
            {
                // Turning this on is the decision, not the first import that acts on
                // it, so the warning belongs here where it can still be undone.
                var yes = await Confirm(
                    "This deletes your source files",
                    "Every file you import will be converted to a .wem and the original " +
                    "DELETED." + Environment.NewLine + Environment.NewLine +
                    "An mp3 becomes a .wem beside it and the mp3 is removed. The new file " +
                    "is read back and checked before anything is deleted, but the audio " +
                    "you started from is gone." + Environment.NewLine + Environment.NewLine +
                    "Keep your masters elsewhere. Turn this on?",
                    "Turn it on", "Cancel", destructive: true);
                // Putting the box back does not raise Click again, which is the other
                // reason this handler is on Click rather than on IsCheckedChanged.
                if (!yes) { replace.IsChecked = false; return; }
            }
            _settings.ReplaceSourceWithWem = replace.IsChecked == true;
            _settings.Save();
            Build();
        };
        Add(replace);
        Add(Note("Off unless you ask for it. With conversions remembered above, the " +
                 "second import is already instant without deleting anything — this is " +
                 "only worth it if you want the folder itself to end up as wems."));
    }

    // ---- tools -----------------------------------------------------------------

    /// <summary>
    /// vgmstream has no field on the Setup tab any more: it ships inside the exe and
    /// unpacks itself, so asking for it would be asking for something already present.
    /// The override lives here because it is a real need for two small groups, anyone
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
        // A path inside the unpacked tools folder is the bundled copy under another
        // name, not somebody's own choice, so it does not count as an override.
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
            Foreground = Palette.Fg,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12),
        });

        var pick = Tool("Use my own vgmstream-cli.exe…");
        pick.HorizontalAlignment = HorizontalAlignment.Left;
        pick.Click += async (_, _) =>
        {
            var wanted = AppPaths.Exe("vgmstream-cli");
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = wanted,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("vgmstream-cli") { Patterns = [wanted] },
                    new FilePickerFileType("Programs")
                        { Patterns = [OperatingSystem.IsWindows() ? "*.exe" : "*"] },
                    new FilePickerFileType("All files") { Patterns = ["*"] },
                ],
            });
            if (picked.Count == 0) return;
            _settings.VgmstreamPath = picked[0].Path.LocalPath;
            _settings.Save();
            _changed();
            Build();
        };
        Add(pick);

        if (custom)
        {
            // Only offered when there is something to go back from, so the way back is
            // never a button that does nothing.
            var revert = Tool("Go back to the bundled copy");
            revert.HorizontalAlignment = HorizontalAlignment.Left;
            revert.Margin = new Thickness(0, 8, 0, 0);
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
            Foreground = Palette.Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        });
    }

    // ---- warnings --------------------------------------------------------------

    /// <summary>
    /// The way back from "Don't show this again". Without it a checkbox ticked once by
    /// accident is permanent, and the warning it silenced is one about a build quietly
    /// doing something other than what you would assume.
    ///
    /// Silenced warnings are stored by key rather than as one quiet flag, so they come
    /// back one at a time as well as all at once. A key is written to be stable, not to
    /// be read, which is why the text comes from Notices.Describe rather than from a
    /// second copy of the wording living here.
    /// </summary>
    private void BuildWarnings()
    {
        PaneTitle.Text = "Warnings you have switched off";
        PaneHint.Text =
            "Some warnings carry a “Don't show this again” box. Anything you have " +
            "silenced is listed here and can be brought back.";

        var off = _settings.SuppressedWarnings ?? [];
        if (off.Count == 0)
        {
            Add(new TextBlock
            {
                Text = "None — every warning is still shown.",
                Foreground = Palette.Muted,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        // A copy, because the buttons remove from the list this is walking.
        foreach (var key in off.ToList())
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 0, 0, 8),
            };

            var what = new TextBlock
            {
                Text = Notices.Describe(key),
                Foreground = Palette.Fg,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            Grid.SetColumn(what, 0);
            row.Children.Add(what);

            var back = Tool("Show again");
            back.Width = 100;
            var silenced = key;
            back.Click += (_, _) =>
            {
                _settings.SuppressedWarnings.Remove(silenced);
                _settings.Save();
                Build();
            };
            Grid.SetColumn(back, 1);
            row.Children.Add(back);

            Add(row);
        }

        var all = Tool("Show all of them again");
        all.HorizontalAlignment = HorizontalAlignment.Left;
        all.Margin = new Thickness(0, 10, 0, 0);
        all.Click += (_, _) =>
        {
            _settings.SuppressedWarnings.Clear();
            _settings.Save();
            Build();
        };
        Add(all);
    }

    // ---- diagnostics -----------------------------------------------------------

    /// <summary>
    /// The same report as the doctor verb, reachable without a command line.
    ///
    /// Someone whose audio will not play is exactly the person least able to run a
    /// console command and read its output, and this application is windowed, so there
    /// is no console to read anyway. The text is the same one EnvCheck.Run prints and
    /// saves, built here rather than run, so that nothing is written to disk and no
    /// second window appears through the Tell hook just because somebody looked.
    /// </summary>
    private void BuildDiagnostics()
    {
        PaneTitle.Text = "Diagnostics";
        PaneHint.Text =
            "What this machine provides, and anything that looks wrong. Copy this into " +
            "a bug report if something is not working.";

        var box = new TextBox
        {
            Text = "Collecting…",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = FontFamily.Parse("Consolas, Menlo, monospace"),
            Height = 260,
            Margin = new Thickness(0, 4, 0, 8),
        };
        // A TextBox scrolls through the ScrollViewer in its own template, which is told
        // what to do with attached properties rather than with properties of its own.
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        Add(box);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var copy = Tool("Copy to clipboard");
        copy.Click += async (_, _) =>
        {
            // Null when there is no windowing platform underneath, which is what the
            // headless render harness runs as.
            var board = Clipboard;
            if (board is null) { Summary.Text = "could not reach the clipboard."; return; }
            try
            {
                await board.SetTextAsync(box.Text);
                Summary.Text = "diagnostics copied.";
            }
            catch { Summary.Text = "could not reach the clipboard."; }
        };
        row.Children.Add(copy);

        var save = Tool("Save as a file…");
        save.Click += async (_, _) =>
        {
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the diagnostics",
                SuggestedFileName = "XzoundWave-diagnostics.txt",
                DefaultExtension = "txt",
                FileTypeChoices =
                [
                    new FilePickerFileType("Text") { Patterns = ["*.txt"] },
                    new FilePickerFileType("All files") { Patterns = ["*"] },
                ],
            });
            if (target is null) return;
            var path = target.Path.LocalPath;
            try
            {
                File.WriteAllText(path, box.Text);
                Summary.Text = "saved to " + path;
            }
            catch (Exception ex) { Summary.Text = ex.Message; }
        };
        row.Children.Add(save);

        var again = Tool("Run again");
        again.Click += (_, _) => Build();
        row.Children.Add(again);
        Add(row);

        Add(new TextBlock
        {
            Text = "Running XzoundWave with --doctor writes the same report to:"
                   + Environment.NewLine + EnvCheck.ReportPath,
            Foreground = Palette.Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });

        _ = Collect(box);
    }

    /// <summary>
    /// Off the drawing thread, because the report answers the question that matters by
    /// starting vgmstream to see whether it runs. On the thread that draws, that is a
    /// window which does not repaint while somebody waits to find out why it is broken.
    /// The await comes back to the UI thread on its own, so nothing here posts.
    /// </summary>
    private async Task Collect(TextBox box)
    {
        string text;
        try { text = await Task.Run(EnvCheck.Report); }
        catch (Exception ex) { text = "the report could not be built: " + ex.Message; }
        box.Text = text;
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
        Add(drop);

        Add(new TextBlock
        {
            Text = "Where the game, the AES key and the usmap live is on the Setup tab, " +
                   "because those are about your installation rather than about the tool.",
            Foreground = Palette.Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        });
    }

    // ---- chrome ----------------------------------------------------------------

    /// <summary>A footer button, flat and square like the toolbar's.</summary>
    private static Button Tool(string text)
    {
        var b = new Button { Content = text };
        b.Classes.Add("tool");
        return b;
    }

    private void RefreshSummary()
    {
        var rows = RowClasses.InForce(_settings.HiddenRowClasses ??= [.. RowClasses.HiddenByDefault])
            .Select(RowClasses.Find).Where(c => c is not null).ToList();
        var cols = _settings.HiddenColumns ?? [];
        Summary.Text = rows.Count == 0 && cols.Count == 0
            ? "showing everything"
            : $"hiding {rows.Count} kind(s) of row" +
              (rows.Count > 0 ? $" ({string.Join(", ", rows.Select(r => Lower(r.Label)))})" : "") +
              $" and {cols.Count} column(s)";
    }

    /// <summary>
    /// Back to how the page arrived, for this page only.
    ///
    /// The WPF original reset the drop pane help from any page that had nothing of its
    /// own to reset, which is not what a button called "Reset this page" says it does.
    /// A page with nothing to put back now says so instead.
    /// </summary>
    private void Reset()
    {
        var page = Categories.SelectedItem as string;
        string said = null;

        switch (page)
        {
            case "Rows":
                _settings.HiddenRowClasses = [.. RowClasses.HiddenByDefault];
                break;

            case "Columns":
                // Put the grid back in step with the defaults, not just the setting.
                _settings.HiddenColumns = [.. HiddenColumnsByDefault];
                foreach (var c in _grid.Columns)
                {
                    var header = c.Header?.ToString();
                    if (string.IsNullOrEmpty(header)) continue;
                    c.IsVisible = !_settings.HiddenColumns
                        .Contains(header, StringComparer.OrdinalIgnoreCase);
                }
                break;

            case "List help":
                _settings.ShowDropHelp = true;
                break;

            default:
                said = "nothing on this page to reset.";
                break;
        }

        if (said is null)
        {
            _settings.Save();
            _changed();
        }
        // Build rewrites the summary, so anything to say goes after it.
        Build();
        if (said is not null) Summary.Text = said;
    }

    /// <summary>
    /// A modal yes or no. Avalonia has no MessageBox, and the shell's own Ask belongs to
    /// the main window, which this one is deliberately not handed a reference to.
    /// </summary>
    private async Task<bool> Confirm(string title, string body, string yes, string no,
                                     bool destructive = false)
    {
        var answer = false;

        // No Classes here: a style declared on this window does not reach a window of
        // its own, and a dialog is one. Fluent's dark buttons are close enough.
        var yesButton = new Button { Content = yes, MinWidth = 110 };
        if (destructive) yesButton.Foreground = Palette.TagRed;

        var noButton = new Button { Content = no, MinWidth = 110 };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        // The way out is the one under the hand: cancelling is the safe answer, so it
        // sits where the eye stops.
        buttons.Children.Add(noButton);
        buttons.Children.Add(yesButton);

        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = body,
            Foreground = Palette.Fg,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        });
        panel.Children.Add(buttons);

        // The settings window's own fill, so the dialog belongs to the window that
        // raised it rather than to whatever the theme would have picked.
        var bg = Background;

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            Background = bg,
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

    /// <summary>"Unnamed events" reads badly after "Show ", and only the first word changes.</summary>
    private static string Lower(string label) =>
        label.Length > 0 && char.IsUpper(label[0]) && !label.StartsWith("PCM")
            ? char.ToLowerInvariant(label[0]) + label[1..]
            : label;
}
