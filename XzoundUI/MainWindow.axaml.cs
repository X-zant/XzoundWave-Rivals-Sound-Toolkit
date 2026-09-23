using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
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

    private void Show()
    {
        _sounds = Merge();
        Grid_.ItemsSource = _sounds.Rows;
        Counts();
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

    private void Counts()
    {
        var rows = _sounds?.Rows ?? [];
        StatScope.Text = _mods.Any
            ? $"{_mods.Sources.Count()} mod loaded · {_mods.Count} .bnk"
            : $"{rows.Count:N0} sounds";
        var modded = rows.Count(r => r.ModTag is "MODDED" or "NEW");
        var staged = rows.Count(r => r.ReplacementPath is not null);
        var muted = rows.Count(r => r.Muted);
        StatModded.Text = modded > 0 ? $"● {modded:N0} modded" : "";
        StatStaged.Text = staged > 0 ? $"{staged:N0} staged" : "";
        StatMuted.Text = muted > 0 ? $"{muted:N0} muted" : "";
    }

    // ---- the toolbar, which is the only thing a tab changes -----------------

    private void ShowToolbar()
    {
        Toolbar.Children.Clear();
        var buttons = Tabs.SelectedIndex switch
        {
            1 => new (string, bool)[] {("Numbered test .bnk…", false), ("Test the selection…", false),
                 ("Test only the selection…", false), ("Mute / unmute", false), ("Silent .bnk…", false)},
            2 => [("Build modded .bnk", true), ("Set replacement…", false), ("Clear", false),
                  ("Set volume…", false), ("Change all volumes…", false),
                  ("Build at these volumes…", false)],
            3 => [("Open .bnk / .pak…", false), ("Close mods", false), ("Merge mods…", false),
                  ("Recompress .bnk…", false)],
            4 => [("Paks folder…", false), ("AES key…", false), ("usmap…", false),
                  ("Fetch latest", false), ("Load game", true)],
            _ => [("▶  Play", false), ("▶▶  Autoplay", false), ("■  Stop", false),
                  ("Measure durations", false), ("Ingame", false)],
        };

        foreach (var (text, key) in buttons)
        {
            var b = new Button { Content = text, Margin = new Avalonia.Thickness(0, 0, 6, 0) };
            b.Classes.Add("tool");
            if (key) b.Classes.Add("key");
            Toolbar.Children.Add(b);
        }
    }

    private void Say(string s) => Status.Text = s;

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
