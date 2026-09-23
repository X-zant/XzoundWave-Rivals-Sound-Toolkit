using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace MRAudioKit.UI;

public partial class MainWindow : Window
{
    // InitializeComponent is generated from the .axaml, and writing one by hand
    // shadows it: the XAML loads, every x:Name field stays null, and the first line
    // that touches a control throws.

    private readonly Settings _settings = Settings.Load();
    private readonly GameSession _session = new();
    private readonly ModSet _mods = new();
    private readonly Dictionary<string, SkinSounds> _skinCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<Node> _roots = [];
    private readonly List<Node> _picked = [];

    private SkinSounds _sounds;

    public MainWindow()
    {
        InitializeComponent();
        BuildTreeTemplate();
        BuildColumns();
        // A node that says it is open is drawn open. Without this the render harness
        // can expand nothing and every shot shows a collapsed tree.
        Tree.ContainerPrepared += (_, e) =>
        {
            if (e.Container is TreeViewItem item && item.DataContext is Node n)
                item.IsExpanded = n.Expanded;
        };
        // State at a glance, in the same colours the tree uses. Avalonia has no
        // DataTrigger, and a row's fill depends on four properties of its item, so it
        // is set as the row is realised rather than expressed as a style. Order
        // matters: a muted row that also has a replacement staged reads as muted,
        // because that is the one that will surprise you in game.
        Grid_.LoadingRow += (_, e) =>
        {
            e.Row.Background = e.Row.DataContext is not SoundRow r ? null
                : r.Muted                              ? Palette.RowMuted
                : r.ReplacementPath is not null        ? Palette.RowStaged
                : r.ModTag == "NEW"                    ? Palette.RowNew
                : r.ModTag == "MODDED"                 ? Palette.RowModded
                : null;
        };
        TbFilter.TextChanged += (_, _) => ApplyFilter();
        BtnColumns.Click += (_, _) => ShowColumnMenu();
        BtnSettings.Click += (_, _) => _ = ShowSettings();
        ApplyColumnVisibility();
        Tabs.SelectionChanged += (_, _) => ShowToolbar();
        Tree.SelectionChanged += async (_, _) => await PickedChanged();
        ShowToolbar();
        Tree.ItemsSource = _roots;
    }

    // ---- the tree -----------------------------------------------------------

    /// <summary>
    /// One template for every level. The marking is option B: a node a mod touched is
    /// filled with the same colour the grid uses for a changed row, with the count on
    /// the right, so a collapsed hero still says how much is in there.
    /// </summary>
    private void BuildTreeTemplate()
    {
        Tree.ItemTemplate = new FuncTreeDataTemplate<Node>(
            (node, _) => new Border
            {
                // Values, not bindings: the template is handed the node, the node does
                // not change after it is built, and anything that does change the tree
                // rebinds it wholesale.
                Padding = new Avalonia.Thickness(4, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = node.Fill,
                Child = new TextBlock
                {
                    // The count rides in the text rather than in a right-docked
                    // element, which measured to nothing inside the item's header and
                    // rendered blank however it was aligned. The number is the point;
                    // where it sits on the row is not worth another layout fight.
                    Text = node.Changed > 0 ? $"{node.Label}   ● {node.CountText}" : node.Label,
                    Foreground = node.Ink,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                },
            },
            n => n.Children);
    }

    /// <summary>Characters, their skins, and the .bnk files under each.</summary>
    private void PopulateTree()
    {
        _roots.Clear();
        foreach (var c in _session.Characters)
        {
            var skins = new List<Node>();
            foreach (var s in c.Skins)
            {
                var banks = s.Banks
                    .OrderBy(b => Path.GetFileName(b.Path), StringComparer.OrdinalIgnoreCase)
                    .Select(b => new Node
                    {
                        What = Node.Kind.Bank,
                        Label = Path.GetFileName(b.Path),
                        CharId = c.CharId,
                        Skin = s,
                        Bank = Path.GetFileName(b.Path),
                    }).ToList();

                skins.Add(new Node
                {
                    What = Node.Kind.Skin,
                    Label = $"{s.Label}   ({s.Banks.Count} .bnk{(s.Banks.Count == 1 ? "" : " files")})",
                    CharId = c.CharId,
                    Skin = s,
                    Children = banks,
                });
            }
            _roots.Add(new Node
            {
                What = Node.Kind.Character, Label = c.Name, CharId = c.CharId, Children = skins,
            });
        }

        if (_session.OtherBanks.Count == 0) return;
        var groups = _session.OtherBanks.Select(g => new Node
        {
            What = Node.Kind.Group,
            Label = g.Name,
            Children = g.Skins.Select(b => new Node
            {
                What = Node.Kind.Bank, Label = b.Label, Skin = b, Bank = b.Label,
            }).ToList(),
        }).ToList();

        _roots.Add(new Node
        {
            What = Node.Kind.Group,
            Label = $"Other .bnk files   ({_session.OtherBanks.Sum(g => g.Skins.Count)})",
            Children = groups,
        });
    }

    // ---- the list -----------------------------------------------------------

    /// <summary>
    /// Seven columns, which is the set the mockup settled on. Everything else a row
    /// knows is still reachable; it is not all worth a column.
    /// </summary>
    private void BuildColumns()
    {
        void Add(string header, string path, int width)
            => Grid_.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = new DataGridLength(width),
            });

        Add("Mod", nameof(SoundRow.Mod), 80);
        Add(".bnk", nameof(SoundRow.Bank), 170);
        Add("Event", nameof(SoundRow.Display), 300);
        Add("Media ID", nameof(SoundRow.MediaId), 110);
        Add("Category", nameof(SoundRow.CategoryShown), 150);
        Grid_.Columns.Add(new DataGridTextColumn
        {
            Header = "Subtitle / line",
            Binding = new Binding(nameof(SoundRow.Subtitle)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        });
        Add("Tags", nameof(SoundRow.TagLabel), 120);
    }

    // ---- selection ----------------------------------------------------------

    /// <summary>
    /// Ctrl+click, which the TreeView does itself once it is told selection can be
    /// multiple. The WPF version intercepted the click and kept its own set of
    /// TreeViewItems, because WPF's TreeView is single-select by design.
    /// </summary>
    private async Task PickedChanged()
    {
        _picked.Clear();
        foreach (var item in Tree.SelectedItems) if (item is Node n) _picked.Add(n);
        if (_picked.Count == 0) { Grid_.ItemsSource = null; return; }

        // Reading a skin is seconds of work, so it goes off the thread that draws.
        try
        {
            foreach (var skin in NeedReading())
            {
                var charId = _picked.First(n => n.Skin?.SkinId == skin.SkinId).CharId ?? "";
                _skinCache[skin.SkinId] =
                    await Task.Run(() => SoundIndex.Build(_session, skin, charId, _ => { }));
            }
            Show();
        }
        catch (Exception ex) { Say("could not read that: " + ex.Message); }
    }

    /// <summary>The same, with nothing awaited. See LoadGameSync.</summary>
    private void Rebuild()
    {
        try
        {
            foreach (var skin in NeedReading())
            {
                var charId = _picked.First(n => n.Skin?.SkinId == skin.SkinId).CharId ?? "";
                _skinCache[skin.SkinId] = SoundIndex.Build(_session, skin, charId, _ => { });
            }
            Show();
        }
        catch (Exception ex) { Say("could not read that: " + ex.Message); }
    }

    private List<SkinEntry> NeedReading() =>
        _picked.Where(n => n.Skin is not null).Select(n => n.Skin)
               .DistinctBy(s => s.SkinId)
               .Where(s => !_skinCache.ContainsKey(s.SkinId))
               .ToList();

    // ---- which columns are on screen ---------------------------------------

    /// <summary>
    /// Hidden on a first run. The .bnk column repeats what the tree already says when
    /// one bank is picked, and a length nobody has measured yet is an empty column.
    /// </summary>
    private static readonly string[] HiddenColumnsByDefault = [".bnk"];

    /// <summary>
    /// A column header is the key its hidden-or-not preference is stored under, so
    /// renaming one silently un-hides it for everybody who had already turned it off.
    /// Rewrite the old name on load rather than stranding the setting.
    ///
    /// Both of these are real: Bank became .bnk in the WPF app, and the port renamed
    /// "Subtitle / line" from the old "Subtitle / line" unchanged but dropped several
    /// columns entirely, whose entries are left alone because an absent column cannot
    /// be shown or hidden.
    /// </summary>
    private static readonly (string Old, string New)[] RenamedColumns =
        [("Bank", ".bnk"), ("Game does", "Ingame")];

    private void ApplyColumnVisibility()
    {
        _settings.HiddenColumns ??= [.. HiddenColumnsByDefault];
        foreach (var (was, now) in RenamedColumns)
            for (var i = 0; i < _settings.HiddenColumns.Count; i++)
                if (_settings.HiddenColumns[i].Equals(was, StringComparison.OrdinalIgnoreCase))
                    _settings.HiddenColumns[i] = now;

        foreach (var c in Grid_.Columns)
        {
            var header = c.Header?.ToString();
            if (string.IsNullOrEmpty(header)) continue;
            c.IsVisible = !_settings.HiddenColumns
                .Contains(header, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Settings owns everything that is a preference rather than an action. It is
    /// handed the grid so its Columns page can enumerate the real columns, and a
    /// callback so a change takes effect here without it knowing how.
    /// </summary>
    private async Task ShowSettings()
    {
        await new SettingsWindow(_settings, Grid_, () =>
        {
            ApplyColumnVisibility();
            ApplyFilter();
        }).ShowDialog(this);

        ApplyColumnVisibility();
        ApplyFilter();
    }

    /// <summary>
    /// A tick per column, next to the button. The same preference lives in Settings;
    /// this is the quick way to it, because deciding which columns you want is
    /// something you do while looking at them.
    /// </summary>
    private void ShowColumnMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var column in Grid_.Columns)
        {
            var header = column.Header?.ToString();
            if (string.IsNullOrEmpty(header)) continue;

            var item = new MenuItem
            {
                Header = header,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible,
            };
            item.Click += (_, _) =>
            {
                _settings.HiddenColumns.RemoveAll(h =>
                    h.Equals(header, StringComparison.OrdinalIgnoreCase));
                if (item.IsChecked != true) _settings.HiddenColumns.Add(header);
                _settings.Save();
                ApplyColumnVisibility();
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(BtnColumns);
    }

    /// <summary>
    /// What the list shows, out of what the selection holds. The filter is deliberately
    /// only the text box: the row-kind switches are a persisted setting and belong in
    /// Settings, where they can be seen rather than remembered.
    /// </summary>
    private void ApplyFilter()
    {
        if (_sounds is null) return;
        var q = TbFilter.Text?.Trim() ?? "";
        IEnumerable<SoundRow> rows = _sounds.Rows;

        // Whole kinds of row can be switched off in Settings, and that setting applies
        // everywhere the list is read from.
        rows = RowClasses.Apply(rows, _settings.HiddenRowClasses ??= [.. RowClasses.HiddenByDefault]);

        if (q.Length > 0)
            rows = rows.Where(r =>
                r.Display.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.MediaId.ToString().Contains(q) ||
                r.Bank.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                SoundTags.Matches(r.Tags, q) ||
                // Clips are spoken zero-padded, so "042" should find #42 as well.
                (int.TryParse(q.TrimStart('#'), out var n) && r.TestNumber == n));

        Grid_.ItemsSource = rows.ToList();
        Counts();
    }

    private void Show()
    {
        _sounds = Merge();
        ApplyFilter();
        Say($"{_picked.Count} selected — {_sounds.Rows.Count:N0} sounds.");
    }

    /// <summary>
    /// One view over everything picked. A .bnk a mod has loaded comes from the MOD,
    /// because that is the point of having opened it.
    /// </summary>
    private SkinSounds Merge()
    {
        var merged = new SkinSounds();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in _picked)
        {
            var stems = node.Bank is not null
                ? [Path.GetFileNameWithoutExtension(node.Bank)]
                : node.Skin is null ? new List<string>()
                : node.Skin.Banks.Select(b => Path.GetFileNameWithoutExtension(b.Path)).ToList();

            foreach (var stem in stems)
            {
                if (!done.Add(stem)) continue;
                var from = _mods.Get(stem)?.Sounds
                           ?? (node.Skin is not null &&
                               _skinCache.TryGetValue(node.Skin.SkinId, out var v) ? v : null);
                if (from is null) continue;

                foreach (var row in from.Rows)
                {
                    if (!row.Bank.Equals(stem, StringComparison.OrdinalIgnoreCase)) continue;
                    merged.Rows.Add(row);
                    if (from.Raw.TryGetValue(row.MediaId, out var raw)) merged.Raw[row.MediaId] = raw;
                    if (from.Embedded.TryGetValue(row.MediaId, out var e)) merged.Embedded[row.MediaId] = e;
                    if (from.BankOfMedia.TryGetValue(row.MediaId, out var b)) merged.BankOfMedia[row.MediaId] = b;
                }
            }
        }
        return merged;
    }

    /// <summary>
    /// The counts, each able to say that some of its kind are NOT on screen.
    ///
    /// A filter that silently removes the sounds you came to look at reads as a broken
    /// tool: hiding unnamed events is on by default, and a mod that replaced sounds the
    /// game never named has every one of its changes dropped by it. So a count carries
    /// a star when rows of its kind are being withheld, and says so on hover. It shows
    /// at zero too, because "0 modded *" is the sentence that stops someone concluding
    /// their mod failed to load.
    ///
    /// The text box is deliberately not counted. Typing a filter is an act you can see
    /// and undo; a setting from three sessions ago is not.
    /// </summary>
    private void Counts()
    {
        var shown = (Grid_.ItemsSource as IEnumerable<SoundRow>)?.ToList() ?? [];
        var all = _sounds?.Rows ?? [];
        var hiddenKeys = _settings.HiddenRowClasses ??= [.. RowClasses.HiddenByDefault];
        var withheld = all.Where(r => RowClasses.IsHidden(r, hiddenKeys)).ToList();

        StatScope.Text = _mods.Any
            ? $"{_mods.Sources.Count()} mod loaded · {_mods.Count} .bnk"
            : $"{shown.Count:N0} sounds";

        Set(StatModded, shown.Count(r => r.ModTag is "MODDED" or "NEW"), "modded",
            withheld, r => r.ModTag is "MODDED" or "NEW", "● ");
        Set(StatStaged, shown.Count(r => r.ReplacementPath is not null), "staged",
            withheld, r => r.ReplacementPath is not null);
        Set(StatMuted, shown.Count(r => r.Muted), "muted", withheld, r => r.Muted);

        void Set(TextBlock box, int count, string label, List<SoundRow> off,
                 Func<SoundRow, bool> belongs, string mark = "")
        {
            var hidden = off.Where(belongs).ToList();
            if (count == 0 && hidden.Count == 0) { box.Text = ""; return; }

            box.Text = $"{mark}{count:N0} {label}" + (hidden.Count > 0 ? " *" : "");
            if (hidden.Count == 0) { ToolTip.SetTip(box, null); return; }

            var by = hidden.SelectMany(r => RowClasses.HidingKinds(r, hiddenKeys))
                           .Select(c => c.Label).Distinct()
                           .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            ToolTip.SetTip(box,
                $"{hidden.Count:N0} more are hidden by the row filter, so they are not " +
                "counted here." + Environment.NewLine + Environment.NewLine +
                "Hidden by: " + string.Join(", ", by) + Environment.NewLine +
                "Settings › Rows turns them back on.");
        }
    }

    // ---- the toolbar, which is the only thing a tab changes -----------------

    /// <summary>
    /// The toolbar is the only thing a tab changes. Each tab's buttons come from its
    /// own file, so the tabs do not have to share one method and can be worked on
    /// without colliding.
    /// </summary>
    private void ShowToolbar()
    {
        Toolbar.Children.Clear();
        var tools = Tabs.SelectedIndex switch
        {
            1 => TestTools(),
            2 => BuildTools(),
            3 => ModTools(),
            4 => SetupTools(),
            _ => BrowseTools(),
        };

        foreach (var tool in tools)
        {
            var b = new Button
            {
                Content = tool.Text,
                Margin = new Avalonia.Thickness(0, 0, 6, 0),
            };
            b.Classes.Add("tool");
            if (tool.Key) b.Classes.Add("key");
            if (tool.Hint is not null) ToolTip.SetTip(b, tool.Hint);
            b.Click += (_, _) => tool.Run();
            Toolbar.Children.Add(b);
        }
    }

    // ---- used by the window and by the render harness ------------------------

    /// <summary>Mount the game and fill the tree, off the UI thread.</summary>
    public async Task LoadGame()
    {
        try
        {
            _settings.AutoDetect();
            await Task.Run(() => _session.Mount(_settings, _ => { }));
            AfterMount();
        }
        catch (Exception ex) { Say("could not load the game: " + ex.Message); }
    }

    /// <summary>
    /// The same work with nothing awaited.
    ///
    /// The render harness has one thread and blocks it waiting for the window to be
    /// ready. Await anything here and the continuation queues on the thread that is
    /// doing the waiting, which is a deadlock and was: the shot hung until it was
    /// killed. Off the UI thread there is nothing to be responsive to, so the honest
    /// answer is not to go async at all.
    /// </summary>
    public void LoadGameSync()
    {
        try
        {
            _settings.AutoDetect();
            _session.Mount(_settings, _ => { });
            AfterMount();
        }
        catch (Exception ex) { Say("could not load the game: " + ex.Message); }
    }

    private void AfterMount()
    {
        PopulateTree();
        Say($"{_session.Characters.Count} characters, " +
            $"{_session.OtherBanks.Sum(g => g.Skins.Count)} other .bnk files.");
    }

    /// <summary>Select one of a skin's banks, so a render has real rows in it.</summary>
    public void ShowSkinSync(string skinId, string bankFragment = null)
    {
        var character = _roots.FirstOrDefault(c => c.Children.Any(s => s.Skin?.SkinId == skinId));
        var skin = character?.Children.FirstOrDefault(s => s.Skin?.SkinId == skinId);
        if (skin is null) { Say($"no skin {skinId} in this game"); return; }

        var bank = skin.Children.FirstOrDefault(b =>
                       bankFragment is null ||
                       b.Label.Contains(bankFragment, StringComparison.OrdinalIgnoreCase))
                   ?? skin.Children.FirstOrDefault();
        if (bank is null) return;

        character.Expanded = true;
        skin.Expanded = true;

        // Containers are prepared once, and the flag is read then -- so a node opened
        // after the tree was built stays shut unless the items are handed over again.
        Tree.ItemsSource = null;
        Tree.ItemsSource = _roots;

        _picked.Clear();
        _picked.Add(bank);
        Rebuild();
        Tree.SelectedItems.Add(bank);
    }

    /// <summary>Mark nodes the way a loaded mod would, for a render of the mod view.</summary>
    public void PretendModded(IReadOnlyDictionary<string, int> changedByBank,
                              bool expandModded = false)
    {
        foreach (var character in _roots)
        {
            var forChar = 0;
            foreach (var skin in character.Children)
            {
                var forSkin = 0;
                foreach (var bank in skin.Children)
                {
                    if (bank.Bank is null) continue;
                    var stem = Path.GetFileNameWithoutExtension(bank.Bank);
                    if (!changedByBank.TryGetValue(stem, out var n)) continue;
                    bank.Changed = n;
                    forSkin += n;
                }
                skin.Changed = forSkin;
                forChar += forSkin;
            }
            character.Changed = forChar;
            if (expandModded && forChar > 0)
            {
                character.Expanded = true;
                foreach (var skin in character.Children.Where(s => s.Changed > 0))
                    skin.Expanded = true;
            }

        }

        Tree.ItemsSource = null;
        Tree.ItemsSource = _roots;

        Console.WriteLine("marked: " + string.Join(", ", _roots
            .Where(c => c.Changed > 0)
            .Select(c => $"{c.Label}={c.Changed}")));
    }
}
