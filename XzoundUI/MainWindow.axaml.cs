using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.VisualTree;

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

    /// <summary>
    /// Load the game as soon as the window opens, once it has loaded here before. Only the real
    /// window asks for it: a render or a self-test makes its own window and loads (or not) itself.
    /// </summary>
    public bool AutoLoadOnOpen { get; init; }

    /// <summary>
    /// Put the columns at the widths last dragged to, and keep them on the way out. The real
    /// window only: a render or a self-test measures the columns at the widths the layout gives.
    /// </summary>
    public bool RemembersLayout { get; init; }

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            RefreshSetupNeeded();
            if (RemembersLayout) RestoreColumnWidths();
            if (AutoLoadOnOpen && Config.LoadedOnce && !string.IsNullOrWhiteSpace(Config.AesKey) &&
                !string.IsNullOrWhiteSpace(Config.PaksDir) && Directory.Exists(Config.PaksDir))
                _ = SetupLoadAsync();
        };
        BuildTreeTemplate();
        BuildColumns();
        // A node that says it is open is drawn open. Without this the render harness
        // can expand nothing and every shot shows a collapsed tree.
        Tree.ContainerPrepared += (_, e) =>
        {
            if (e.Container is TreeViewItem item && item.DataContext is Node n)
                item.IsExpanded = n.Expanded;
        };
        // Ahead of the arrow's own button, so a click on it toggles once, not twice.
        Tree.AddHandler(PointerPressedEvent, ToggleFromGutter, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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

        // Double-click is how people expect to hear a row. The context menu carries
        // the comparison: a staged file wins the ordinary play, so hearing what the
        // game shipped needs its own way in.
        Grid_.DoubleTapped += (_, _) =>
        {
            // A double-click in the Notes cell is someone opening it to type, not asking to
            // hear the sound again.
            if (Grid_.CurrentColumn?.Header?.ToString() == NotesColumn) return;
            if (CurrentRow is not null) PlayRow(CurrentRow, original: false);
        };
        Grid_.CellEditEnded += (_, e) =>
        {
            if (e.EditAction != DataGridEditAction.Commit || e.Row?.DataContext is not SoundRow row) return;
            if (e.Column?.Header?.ToString() != NotesColumn) return;
            // Opening the cell and leaving it is a commit too; only a different note is a change.
            if ((_project.FindNote(row.MediaId)?.Notes ?? "") == (row.Notes ?? "")) return;
            RowStateChanged([row]);
            Say($"note saved on {row.MediaId}, in the project \"{_project.Name}\". Save the project to keep it.");
        };
        Grid_.ContextFlyout = RowMenu();
        BtnColumns.Click += (_, _) => ShowColumnMenu();
        BtnSettings.Click += (_, _) => _ = ShowSettings();
        HookProject();
        HookPendingPanel();
        ApplyColumnVisibility();
        // A column's floor can only be measured off a cell the theme has built, so it
        // waits for a layout pass rather than being settled here. See FloorColumns.
        Grid_.LayoutUpdated += (_, _) => FloorColumns();
        // Before the toolbar is built: one of its buttons carries this state in its label.
        StartCategoryLanguage();
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

    /// <summary>
    /// The whole gutter left of a node's label opens and shuts it, not only the arrow. The
    /// theme's arrow is a 12 px button centred in a 36 px gutter on a 30 px row, so a click a
    /// few pixels off its middle landed on the row and selected it instead: people reported
    /// having to hit the arrow dead centre. The label still selects, and a double-click on it
    /// still opens, as before.
    /// </summary>
    private void ToggleFromGutter(object sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed) return;
        if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true) is not { ItemCount: > 0 } item) return;
        // The item's own header and label: the first of each below it, ahead of its children's.
        var parts = item.GetVisualDescendants().OfType<Control>();
        if (parts.FirstOrDefault(c => c.Name == "PART_Header") is not { } header ||
            parts.FirstOrDefault(c => c.Name == "PART_HeaderPresenter") is not { } label) return;
        var at = e.GetPosition(header);
        if (at.X < 0 || at.X >= label.Bounds.X || at.Y < 0 || at.Y >= header.Bounds.Height) return;
        item.IsExpanded = !item.IsExpanded;
        e.Handled = true;
    }

    /// <summary>The render harness: a node of its own in the tree, and out again.</summary>
    internal void AddRootForShot(Node node) => _roots.Add(node);
    internal void RemoveRootForShot(Node node) => _roots.Remove(node);

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
    /// <summary>The widths last dragged to, on every fixed-width column; the star column keeps filling the rest.</summary>
    private void RestoreColumnWidths()
    {
        if (_settings.ColumnWidths is not { Count: > 0 } saved) return;
        foreach (var column in Grid_.Columns)
            if (column.Header?.ToString() is { } header && !column.Width.IsStar &&
                saved.TryGetValue(header, out var width) && width >= 30 && width <= 2000)
                column.Width = new DataGridLength(width);
    }

    private void SaveColumnWidths()
    {
        var widths = new Dictionary<string, double>(_settings.ColumnWidths ?? []);
        foreach (var column in Grid_.Columns)
            if (column.Header?.ToString() is { } header && !column.Width.IsStar && column.ActualWidth > 0)
                widths[header] = Math.Round(column.ActualWidth);
        _settings.ColumnWidths = widths;
        _settings.Save();
    }

    private void BuildColumns()
    {
        // Dragged wider or narrower by the header's edge; remembered by the real window.
        Closed += (_, _) => { if (RemembersLayout) SaveColumnWidths(); };
        // Every column a fixed width the header's edge drags, with a floor. A star column (the
        // subtitle filled what was left) made the grid fit every column into the view instead of
        // scrolling: with the side panel open the others were squeezed -- Category to 20 px,
        // nothing at all -- and no column could be dragged wider, there being nowhere to take
        // the room from. Fixed widths scroll sideways instead, which is what the window this was
        // ported from did: its list carried 1,781px of columns in 592px and had always scrolled.
        // Only Notes is editable: the grid is not read-only so that it can be, and every other
        // column says so itself.
        // A column showing a label for a number sorts by the number (sortBy), or "#10" comes
        // before "#2" and "10.0x" before "2.0x" (SortingSelfTest).
        void Add(string header, string path, int width, int floor, bool editable = false, string sortBy = null)
            => Grid_.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path) { Mode = editable ? BindingMode.TwoWay : BindingMode.OneWay },
                Width = new DataGridLength(width),
                MinWidth = floor,
                IsReadOnly = !editable,
                SortMemberPath = sortBy,
            });

        Add("Mod", nameof(SoundRow.Mod), 80, 50);
        Add(".bnk", nameof(SoundRow.Bank), 170, 80);
        Add("Event", nameof(SoundRow.Display), 300, 100);
        // Wide enough for a ten-digit id whole: a clipped id reads as another, valid one.
        Add("Media ID", nameof(SoundRow.MediaId), 110, 100);
        Add("Category", nameof(SoundRow.CategoryShown), 150, 70);
        Add("Subtitle / line", nameof(SoundRow.Subtitle), 420, 120);

        // What a test build numbered this sound, so hearing "42" in game leads back to its row;
        // and a level set on it, starred when set by hand. Both are kept in the project.
        // Floored at its header, which is wider than any number it shows: about 76 px in
        // Segoe UI and 80 in Inter, the Linux window's font.
        Add(TestColumn, nameof(SoundRow.TestLabel), 90, 90, sortBy: nameof(SoundRow.TestNumber));
        Add(VolColumn, nameof(SoundRow.VolumeLabel), 60, 45, sortBy: nameof(SoundRow.Volume));

        // Your own words about the sound, typed straight into the cell and kept in the project
        // (Grid_CellEditEnded). Tags were the other half of this once; they are withdrawn
        // until the community database they are meant for.
        Add(NotesColumn, nameof(SoundRow.Notes), 220, 80, editable: true);

        // Hidden on a first run, like .bnk: an unmeasured length is an empty column,
        // and the mockup's seven are what the default view should be. But Measure
        // durations has to have somewhere to put its answer, or the button reports
        // into the status line and nowhere else.
        Add("Len", nameof(SoundRow.Length), 60, 45, sortBy: nameof(SoundRow.Seconds));

        // A cell clips its text against its own bounds, and DataGridTextColumn leaves
        // TextTrimming at None, so a value too wide for its column comes out SHORTER
        // rather than marked: the media id 1057163202 renders as 105716320, which is
        // itself a perfectly plausible media id, and 123.45s renders as 123.4 with the
        // one character that would have warned you taken off first. DataGridTextColumn
        // exposes the font properties and not this one, so it is set on the cells
        // themselves through a style, which also leaves the headers alone -- a header
        // is a DataGridColumnHeader and not a DataGridCell.
        Grid_.Styles.Add(new Style(x => x.Is<DataGridCell>().Descendant().OfType<TextBlock>())
        {
            Setters =
            {
                new Setter(TextBlock.TextTrimmingProperty,
                           Avalonia.Media.TextTrimming.CharacterEllipsis),
            },
        });
    }

    /// <summary>
    /// A column, the widest string it claims to show whole, the width that would take,
    /// and the width it ended up with -- all in pixels of the font this host resolved.
    ///
    /// Public because "every column fits what it claims to show" is a layout claim about
    /// a particular host's font, and a layout claim belongs in the headless test rather
    /// than in someone's memory of what the window looked like.
    ///
    /// Needs carries the theme's own margin round the text, which is why it is comparable
    /// with Room: a column is as wide as its cells, not as wide as the glyphs in them.
    /// </summary>
    public sealed record ColumnFit(string Header, string Widest, double Needs, double Room)
    {
        public bool Fits => Room >= Needs;
        public override string ToString() =>
            $"{Header} \"{Widest}\" needs {Needs:0.#} of {Room:0.#}";
    }

    /// <summary>
    /// One column measured against one string, in the shape both grids report in.
    /// Falls back to the floor when the column has not been arranged yet, because a
    /// width of zero is "not laid out" rather than "nothing fits".
    /// </summary>
    private static ColumnFit Fit(DataGrid grid, string header, string widest, double needs)
    {
        var column = grid.Columns.FirstOrDefault(c => c.Header?.ToString() == header);
        var room = column is null ? 0 : Math.Max(column.ActualWidth, column.MinWidth);
        return new ColumnFit(header, widest, needs, room);
    }

    private double _cellChrome;
    private List<(string Header, string Widest, double Needs)> _bounded;
    private List<SoundRow> _floorProbe;

    /// <summary>
    /// The font a grid cell was found to be drawn in. Kept because the staged pane is a
    /// second DataGrid with fixed columns of its own and the same problem to solve, and
    /// it has no cell of its own to measure while it is empty -- which is exactly when
    /// its columns are first laid out. Nothing here sets a FontFamily and both grids use
    /// the same theme, so what was resolved for one cell is what the other will get.
    /// </summary>
    private Avalonia.Media.Typeface _cellFace;
    private double _cellEm;

    /// <summary>
    /// The same figures for a column HEADER, which is a DataGridColumnHeader: it carries
    /// its own padding, the separator between columns and 32 px of room for a sort arrow
    /// it keeps whether or not the grid sorts, and it is drawn in a lighter font than a
    /// cell. Measuring a column against the cell's margin alone leaves its header a
    /// character short, which is how "Codec" comes out as "Code" over an empty column;
    /// measuring it in the cell's FONT overstates it by about a fifth, which on the
    /// staged pane is the difference between four columns fitting and two of them
    /// collapsing to one letter each.
    /// </summary>
    private double _headerChrome;
    private Avalonia.Media.Typeface _headFace;
    private double _headEm;

    /// <summary>
    /// How wide a column has to be to show a string whole: the text in this host's font,
    /// plus whatever the theme puts round it. Zero until <see cref="FloorColumns"/> has
    /// had a cell to measure, which callers read as "do not floor anything yet".
    /// </summary>
    /// <param name="header">
    /// Whether the string is the column's header rather than one of its values.
    /// </param>
    private double CellWidth(string text, bool header = false) =>
        _bounded is null || string.IsNullOrEmpty(text) ? 0
        : new Avalonia.Media.FormattedText(
              text, System.Globalization.CultureInfo.CurrentCulture,
              Avalonia.Media.FlowDirection.LeftToRight,
              header ? _headFace : _cellFace, header ? _headEm : _cellEm, null).Width
          + (header ? _headerChrome : _cellChrome);

    /// <summary>
    /// Every column of the list that is on screen, against what it has to show whole:
    /// its own header, and for a bounded column the widest value its type can yield.
    /// Empty until <see cref="FloorColumns"/> has had a cell to measure, which is the
    /// answer "this could not be measured" rather than "everything fits".
    ///
    /// A column switched off is left out rather than reported as fitting nothing: it
    /// shows no value and no header, so there is no claim to make about it, and reporting
    /// on it would make the answer depend on which columns whoever ran this had hidden.
    /// </summary>
    public IReadOnlyList<ColumnFit> ColumnFits()
    {
        if (_bounded is null) return [];

        var fits = new List<ColumnFit>();
        foreach (var column in Grid_.Columns.Where(c => c.IsVisible))
        {
            var header = column.Header?.ToString();
            if (string.IsNullOrEmpty(header)) continue;

            var widest = header;
            var needs = CellWidth(header, header: true);
            foreach (var b in _bounded.Where(b => b.Header == header))
            {
                if (b.Needs + _cellChrome <= needs) continue;
                needs = b.Needs + _cellChrome;
                widest = b.Widest;
            }
            fits.Add(Fit(Grid_, header, widest, needs));
        }
        return fits;
    }

    /// <summary>
    /// Give the fixed-width columns a floor measured in the font this host resolved.
    ///
    /// The widths in BuildColumns are pixel constants and nothing in this application
    /// sets a FontFamily, so the host picks the font -- and a digit is eighteen per cent
    /// wider in DejaVu Sans than in Segoe UI. Media ID's 110 px holds a ten-digit id on
    /// Windows and cuts a digit off it on Linux, which with the trimming above is now
    /// visible and was previously silent. Auto width is not the fix: it follows whatever
    /// rows are on screen, measuring 94 px while only short ids are realised and jumping
    /// to 120 as a long one scrolls in, so the column would resize under the reader. So
    /// the declared width stays and gains a floor, which keeps the design on the host it
    /// was designed for and gives more room only to the font that needs more.
    ///
    /// Both halves of the sum can only be read off a cell the theme has built: the
    /// typeface the text shaper resolved, and the twenty-four pixels of margin
    /// DataGridTextColumn.GenerateElement puts round the text, which Avalonia sets in
    /// code and exposes as no resource. Hence the wait for a layout pass, and the one
    /// empty row put in when nothing is on screen yet, because a grid with no rows has
    /// no cell to measure.
    /// </summary>
    private void FloorColumns()
    {
        if (_bounded is not null) return;

        var probe = Grid_.GetVisualDescendants().OfType<DataGridCell>()
            .Select(c => (Cell: c, Text: c.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()))
            .FirstOrDefault(p => p.Text is not null && p.Cell.Bounds.Width > 0);

        if (probe.Text is null)
        {
            // Nothing realised. One row is enough for the theme to build a cell, and it
            // is handed back the moment the measurement is taken -- guarded so a grid
            // that stays unrealised for another reason does not add a row every pass.
            if (_floorProbe is not null || Grid_.ItemsSource is not null) return;
            Grid_.ItemsSource = _floorProbe = [new SoundRow()];
            return;
        }

        _cellChrome = probe.Cell.Bounds.Width - probe.Text.Bounds.Width;

        // Both grids are the same theme, so the header's own padding is read off this
        // grid's headers, which are realised as soon as it has columns. Never below the
        // cell's margin: the fallback when none is realised has to be the figure that
        // was measured rather than nothing.
        var head = Grid_.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Select(h => (Head: h, Text: h.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()))
            .FirstOrDefault(p => p.Text is not null && p.Head.Bounds.Width > 0);
        _headerChrome = head.Text is null ? _cellChrome
            : Math.Max(_cellChrome, head.Head.Bounds.Width - head.Text.Bounds.Width);

        var face = _cellFace = new Avalonia.Media.Typeface(
            probe.Text.FontFamily, probe.Text.FontStyle,
            probe.Text.FontWeight, probe.Text.FontStretch);
        var em = _cellEm = probe.Text.FontSize;

        // And the font a header is drawn in, which is not the cell's. Measured on this
        // host: a header renders about a fifth narrower than the same string in a cell,
        // so measuring "Your note" in the cell's font asks 109 px for a header that needs
        // 96, and two columns of that is most of the slack the staged pane has to give.
        _headFace = head.Text is null ? _cellFace : new Avalonia.Media.Typeface(
            head.Text.FontFamily, head.Text.FontStyle, head.Text.FontWeight, head.Text.FontStretch);
        _headEm = head.Text is null ? _cellEm : head.Text.FontSize;

        double Wide(string s) => new Avalonia.Media.FormattedText(
            s, System.Globalization.CultureInfo.CurrentCulture,
            Avalonia.Media.FlowDirection.LeftToRight, face, em, null).Width;

        // Which digit is the widest one is a property of the font and not of the number,
        // so every candidate is measured with its digits swapped for that one: the floor
        // has to hold the widest rendering of a value, not the rendering of one example.
        var digit = "0123456789".OrderByDescending(d => Wide(new string(d, 4))).First();
        string Widen(string s) => string.Concat(s.Select(c => char.IsDigit(c) ? digit : c));

        _bounded = [];
        foreach (var (header, candidates) in BoundedColumns())
        {
            var column = Grid_.Columns.FirstOrDefault(c => c.Header?.ToString() == header);
            if (column is null) continue;

            var widest = candidates.Select(Widen).OrderByDescending(Wide).First();
            var needs = Wide(widest);
            column.MinWidth = Math.Max(column.MinWidth, Math.Ceiling(needs + _cellChrome));
            _bounded.Add((header, widest, needs));
        }

        // The pane's own fixed columns have the same problem and no cell of their own to
        // measure. Floored here as well as after every staging, so the first frame the
        // pane is drawn in is already right rather than right one redraw later.
        FloorPending();

        if (ReferenceEquals(Grid_.ItemsSource, _floorProbe)) Grid_.ItemsSource = null;
    }

    /// <summary>
    /// What each bounded column has to be able to show whole.
    ///
    /// Every candidate is asked of the code that fills the column rather than quoted from
    /// it, so a change to what a cell can say moves the floor with it. Media ID binds a
    /// uint (SoundIndex.cs:19), so ten digits is the type's ceiling and the ceiling is
    /// the honest floor. Mod's widest state is the one its own property yields for a row
    /// with a file staged (SoundIndex.cs:216). Test # and Vol ask the row for its label at
    /// the widest value a person sets: a four-digit test number, a level of 10x by hand.
    ///
    /// Len is a floor paired with the ellipsis rather than a guarantee, because seconds is a
    /// double with no upper bound, so past the floor it is the ellipsis that says a value
    /// was shortened. Three integer digits is over sixteen minutes of audio, and Len is
    /// where this matters most -- 123.45s clipped to 123.4 loses the unit that would have
    /// told you something was missing, and still reads as a duration.
    /// </summary>
    private static IEnumerable<(string Header, IEnumerable<string> Candidates)> BoundedColumns()
    {
        yield return ("Mod", [new SoundRow { Muted = true }.Mod,
                              new SoundRow { ReplacementPath = "" }.Mod]);
        yield return ("Media ID", [new string('0', uint.MaxValue.ToString().Length)]);
        yield return (TestColumn, [new SoundRow { TestNumber = 9999 }.TestLabel]);
        yield return (VolColumn, [new SoundRow { Volume = 10.25, VolumeByHand = true }.VolumeLabel]);
        yield return ("Len", [new SoundRow { Seconds = 999.99 }.Length]);
    }

    private const string TestColumn = "Test #";
    private const string VolColumn = "Vol";
    private const string NotesColumn = "Notes";

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
            ShowRows();
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
            ShowRows();
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
    /// What applies to the rows you have picked. Short on purpose: the old one ran to
    /// twenty items in six groups, most of which duplicated a toolbar button under a
    /// different name. What is here is either about THIS row, or has no other home.
    /// </summary>
    private MenuFlyout RowMenu()
    {
        var menu = new MenuFlyout();

        MenuItem Item(string header, Action run, string hint = null)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => run();
            if (hint is not null) ToolTip.SetTip(item, hint);
            menu.Items.Add(item);
            return item;
        }

        Item("Play", () => { if (CurrentRow is not null) PlayRow(CurrentRow, original: false); });
        Item("Play the game's original",
             () => { if (CurrentRow is not null) PlayRow(CurrentRow, original: true); },
             "Ignores a staged replacement and a loaded mod, so you can hear what the " +
             "game shipped and compare.");
        // The audio out, named so it stages straight back in: what the old window called Export.
        Item("Extract wem(s)…", () => _ = ExtractRowsAsync(asWav: false),
             "Writes the selected sounds' audio as {media id}-{event}.wem, the audio the row plays " +
             "(an open mod's for its rows). Drop one back in and it stages onto the same sound.");
        Item("Extract as WAV…", () => _ = ExtractRowsAsync(asWav: true),
             "The same, decoded to WAV to listen to or edit.");
        menu.Items.Add(new Separator());
        // A mute is a build instruction for the rows themselves, not a test-only one, so it
        // lives with them rather than on the Test tab. Named for what a click will do.
        var mute = Item("Mute", ToggleMute,
             "Silences the selection in every build from now on, not only a test " +
             "one. Mute the sound you have already identified to hear what is under it.");
        menu.Opening += (_, _) =>
            mute.Header = SelectedRows is { Count: > 0 } picked && picked.All(r => r.Muted) ? "Unmute" : "Mute";
        // Beside the event rather than instead of anything in it: the one way to ADD audio to
        // what the game plays, so it has no toolbar home.
        var addLayer = Item("Add a sound to this event…", () => _ = AddLayerAsync(),
             "Plays a file of yours every time this sound's event plays, beside what it already " +
             "plays (music under an ult, say). Nothing of the game's is replaced. Saved in the " +
             "project, listed under \"Added sounds\", and put in by Build mod.");
        var removeLayer = Item("Remove added sound", RemoveLayers);
        menu.Opening += (_, _) =>
        {
            var picked = SelectedRows;
            var added = picked.Count > 0 && picked.All(IsAddedRow);
            removeLayer.IsVisible = added;
            addLayer.IsVisible = !added;
            addLayer.IsEnabled = picked.Count == 1 && !IsProjectRow(picked[0]);
        };
        menu.Items.Add(new Separator());
        Item("Copy media id(s)", () => _ = CopyIds());
        return menu;
    }

    /// <summary>The render harness: right-click › Mute on the selection.</summary>
    internal void MuteForShot() => ToggleMute();

    /// <summary>
    /// Mute or unmute the selection. When several sounds fire at once only the loudest
    /// is intelligible, so silencing the one you have already identified is how the
    /// ones underneath it become audible on the next run.
    /// </summary>
    private void ToggleMute()
    {
        // An added sound is the project's own; it goes by being removed, not by a mute that would
        // write a silent file under an id the game has never had.
        var rows = SelectedRows.Where(r => !IsAddedRow(r)).ToList();
        if (rows.Count == 0 && SelectedRows.Count > 0) { Say("an added sound is not muted: right-click › Remove added sound."); return; }
        if (rows.Count == 0) { Say("select one or more sounds in the list first."); return; }

        // A mixed selection becomes all-muted rather than flipping each one, which is
        // what "mute these" means when some of them already are.
        var turnOn = rows.Any(r => !r.Muted);
        foreach (var r in rows) r.Muted = turnOn;
        RowStateChanged(rows);
        Refresh();

        var muted = Sounds?.Rows.Count(r => r.Muted) ?? 0;
        Say($"{rows.Count} sound(s) {(turnOn ? "muted" : "unmuted")} — {muted} muted in total. " +
            "A mute is silent in every build, not only a test one; rebuild to hear the difference.");
    }

    /// <summary>The ids, for pasting into a filename or a message to someone.</summary>
    private async Task CopyIds()
    {
        var ids = SelectedRows.Select(r => r.MediaId.ToString()).ToList();
        if (ids.Count == 0) { Say("select one or more sounds first."); return; }

        if (Clipboard is null) { Say("no clipboard on this host."); return; }
        await Clipboard.SetTextAsync(string.Join(Environment.NewLine, ids));
        Say($"copied {ids.Count} media id(s).");
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
            ApplyPendingHelp();
            ApplyFilter();
        }).ShowDialog(this);

        ApplyColumnVisibility();
        ApplyPendingHelp();
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
    /// The row kinds Settings › Rows has switched off, as the list acts on them: the stored
    /// setting less the kinds about tags, which the window does not offer for now
    /// (RowClasses.Withheld). One property for every place that hides or explains a row.
    /// </summary>
    private List<string> HiddenRowKinds =>
        RowClasses.InForce(_settings.HiddenRowClasses ??= [.. RowClasses.HiddenByDefault]);

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
        rows = RowClasses.Apply(rows, HiddenRowKinds);

        if (q.Length > 0)
            rows = rows.Where(r =>
                r.Display.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.MediaId.ToString().Contains(q) ||
                r.Bank.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (r.Notes ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                // Clips are spoken zero-padded, so "042" should find #42 as well.
                (int.TryParse(q.TrimStart('#'), out var n) && r.TestNumber == n));

        // Before the rows are handed over, so a set that arrived translated is drawn
        // translated the first time rather than a frame later.
        CategoryLanguageForNewRows();

        Grid_.ItemsSource = rows.ToList();
        Counts();
    }

    /// <summary>
    /// Put the merged rows on screen.
    ///
    /// Not called Show: a private Show() on a Window hides Window.Show(), so any call
    /// inside this class meaning "open the window" would silently have got this
    /// instead. It only stayed harmless because nothing inside called it.
    /// </summary>
    private void ShowRows()
    {
        _sounds = Merge();
        // Rows are built fresh here, so what is staged is marked on them here: a new line's or an
        // added sound's row has nothing to play until it is, and a skin read after staging would
        // show nothing green.
        MarkStagedRows();
        var relinked = ApplyProjectToRows(_sounds.Rows);
        ApplyFilter();
        Say($"{_picked.Count} selected, {_sounds.Rows.Count:N0} sounds." + RelinkedNote(relinked));
    }

    // Game rows a loaded loose file is the audio of, last time the rows were merged.
    private readonly HashSet<SoundRow> _overlaid = [];

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
            if (node.What == Node.Kind.NewLines)
            {
                foreach (var row in NewLineRows())
                    if (done.Add("newline:" + row.MediaId)) merged.Rows.Add(row);
                continue;
            }
            if (node.What == Node.Kind.AddedSounds)
            {
                foreach (var row in AddedRows())
                    if (done.Add("added:" + row.MediaId)) merged.Rows.Add(row);
                continue;
            }
            var stems = node.Bank is not null
                ? [Path.GetFileNameWithoutExtension(node.Bank)]
                : node.Skin is null ? new List<string>()
                : node.Skin.Banks.Select(b => Path.GetFileNameWithoutExtension(b.Path)).ToList();

            foreach (var stem in stems)
            {
                if (!done.Add(stem)) continue;
                var entry = _mods.Get(stem);
                var vanilla = node.Skin is not null && _skinCache.TryGetValue(node.Skin.SkinId, out var v) ? v : null;
                var from = entry?.Sounds ?? vanilla;
                if (from is null) continue;

                var rows = from.Rows.Where(r => r.Bank.Equals(stem, StringComparison.OrdinalIgnoreCase))
                                    .Select(r => (Row: r, Game: entry is null)).ToList();
                // A loaded bank lists what it embeds. The sounds the game's copy streams from loose
                // files are not in it, and still play, through the same Sounds -- so they are the
                // game's rows, and the ones a loaded loose file replaces have to be among them.
                if (entry is not null && vanilla is not null)
                {
                    var have = entry.Sounds.Rows.Select(r => r.MediaId).ToHashSet();
                    rows.AddRange(vanilla.Rows.Where(r => r.Bank.Equals(stem, StringComparison.OrdinalIgnoreCase)
                                                          && !have.Contains(r.MediaId)
                                                          && !vanilla.Embedded.ContainsKey(r.MediaId)
                                                          && !vanilla.BankOfMedia.ContainsKey(r.MediaId))
                                              .Select(r => (Row: r, Game: true)));
                }

                var overlaid = false;
                foreach (var (row, game) in rows)
                {
                    merged.Rows.Add(row);
                    var src = game ? vanilla : from;
                    if (src.Raw.TryGetValue(row.MediaId, out var raw)) merged.Raw[row.MediaId] = raw;
                    if (src.Embedded.TryGetValue(row.MediaId, out var e)) merged.Embedded[row.MediaId] = e;
                    if (src.BankOfMedia.TryGetValue(row.MediaId, out var b)) merged.BankOfMedia[row.MediaId] = b;

                    // A loose file a loaded mod ships at the path this row's medium streams from is
                    // what the row plays now. A game row carries no tag of its own, so its tag is
                    // set from the loaded files every time and goes when they do.
                    var lo = _mods.LooseFor(row.MediaId, _session?.Language);
                    // Under a loaded bank a game row is one that bank left alone, "" -- what "Show
                    // only changed" hides -- unless a loose file changes it.
                    if (game) row.ModTag = lo?.Tag ?? (entry is not null ? "" : null);
                    else if (lo is { Changed: true } && string.IsNullOrEmpty(row.ModTag)) row.ModTag = "MODDED";
                    // A game row is shared by every view of its bank and outlives the mod, so a
                    // length measured from the mod's file goes when the file does.
                    if (game && lo is { Changed: true }) _overlaid.Add(row);
                    else if (game && _overlaid.Remove(row)) row.Seconds = null;
                    if (lo is null) continue;
                    overlaid = true;
                    if (!lo.Changed) continue;
                    merged.Raw[row.MediaId] = lo.Bytes;
                    merged.RawFrom[row.MediaId] = $"{lo.MountedAt} in {lo.SourceName}";
                }
                // Among a bank a mod touches, an untouched row is "" rather than no tag at all,
                // which is what "Show only changed" hides.
                if (overlaid)
                    foreach (var (row, game) in rows)
                        if (game && row.ModTag is null) row.ModTag = "";
            }
        }
        return merged;
    }

    /// <summary>
    /// The counts, each able to say that some of its kind are NOT on screen.
    ///
    /// A filter that silently removes the sounds you came to look at reads as a broken
    /// tool: hiding unnamed events is one switch away, and a mod that replaced sounds the
    /// game never named has every one of its changes dropped by it. So a count carries
    /// a star when rows of its kind are being withheld, and says so on hover. It shows
    /// at zero too, because "0 modded *" is the sentence that stops someone concluding
    /// their mod failed to load.
    ///
    /// The staged count is the odd one out and is worked out separately: see the local
    /// method for why counting rows was the wrong question for it.
    ///
    /// The text box is deliberately not counted. Typing a filter is an act you can see
    /// and undo; a setting from three sessions ago is not.
    /// </summary>
    private void Counts()
    {
        // The pane on the right is the long form of the staged count beside it, and this
        // is the one method both routes to the list reach: Refresh for a redraw of the
        // same rows, ApplyFilter for a different set of them. Drawing it from here is
        // what makes "what is staged" and "what the window says is staged" the same
        // thing, whichever of the five places that stage something did the staging.
        ShowPending();

        var shown = (Grid_.ItemsSource as IEnumerable<SoundRow>)?.ToList() ?? [];
        var all = _sounds?.Rows ?? [];
        var hiddenKeys = HiddenRowKinds;
        var withheld = all.Where(r => RowClasses.IsHidden(r, hiddenKeys)).ToList();

        StatScope.Text = _mods.Any
            ? $"{_mods.Sources.Count()} mod loaded · {_mods.Count} .bnk" +
              (_mods.LooseFiles.Count > 0 ? $" · {_mods.LooseFiles.Count} loose wem" : "")
            : $"{shown.Count:N0} sounds";

        Set(StatModded, shown.Count(r => r.ModTag is "MODDED" or "NEW"), "modded",
            withheld, r => r.ModTag is "MODDED" or "NEW", "● ");
        Staged();
        Set(StatMuted, shown.Count(r => r.Muted), "muted", withheld, r => r.Muted);

        // Modded and muted are properties of a ROW, so counting the rows on screen is the
        // only count there is. Staged is not: the staged list exists whether or not a skin
        // is open, and it is the same list the pane draws, so this number is asked of the
        // list itself. Counting the rows instead read "5 staged" beside a pane showing six,
        // because the sixth id belonged to a .bnk of a skin nobody had opened, and read as
        // nothing at all with a word typed in the filter box while six were staged. The
        // star is this window's existing way of saying "and some you cannot see", so the
        // same mark says so here rather than a second convention being invented.
        void Staged()
        {
            if (_pending.Count == 0) { StatStaged.Text = ""; ToolTip.SetTip(StatStaged, null); return; }

            var onScreen = shown.Select(r => r.MediaId).ToHashSet();
            var off = _pending.Where(p => !onScreen.Contains(p.MediaId)).ToList();

            StatStaged.Text = $"{_pending.Count:N0} staged" + (off.Count > 0 ? " *" : "");
            if (off.Count == 0) { ToolTip.SetTip(StatStaged, null); return; }

            // Which of the three reasons applies is worth saying, because two of them are
            // something the reader can undo and the third is not: a filter is a setting,
            // and an id belonging to another skin's bank is simply a fact about the file.
            var open = new Dictionary<uint, SoundRow>();
            foreach (var r in all) open.TryAdd(r.MediaId, r);

            var byRowFilter = 0; var byTextBox = 0; var noRow = 0;
            foreach (var p in off)
                if (!open.TryGetValue(p.MediaId, out var row)) noRow++;
                else if (RowClasses.IsHidden(row, hiddenKeys)) byRowFilter++;
                else byTextBox++;

            var why = new List<string>();
            if (byRowFilter > 0)
                why.Add($"{byRowFilter:N0} by the row filter, which Settings › Rows turns back on");
            if (byTextBox > 0) why.Add($"{byTextBox:N0} by the filter box");
            if (noRow > 0)
                why.Add($"{noRow:N0} staged on a media id no .bnk you have open holds");

            ToolTip.SetTip(StatStaged,
                $"All {_pending.Count:N0} are staged and all {_pending.Count:N0} will be built. " +
                $"{off.Count:N0} of them have no row in the list, so nothing on screen is " +
                "tinted for them:" + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, why.Select(w => "  " + w)) +
                Environment.NewLine + Environment.NewLine +
                "The pane on the right lists every staged file whatever the list is showing.");
        }

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
                Content = tool.Menu is null ? tool.Text : tool.Text + " ▾",
                Margin = new Avalonia.Thickness(0, 0, 6, 0),
            };
            b.Classes.Add("tool");
            if (tool.Key) b.Classes.Add("key");
            if (tool.Hint is not null) ToolTip.SetTip(b, tool.Hint);
            // A dropdown groups the actions that belong together, so the toolbar stays one row
            // of what is used every time; each item keeps its own explanation on hover.
            if (tool.Menu is not null)
            {
                var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
                foreach (var item in tool.Menu)
                {
                    if (item.Text == "-") { flyout.Items.Add(new Separator()); continue; }
                    var mi = new MenuItem
                    {
                        Header = item.Text,
                        ToggleType = item.Checked is null ? MenuItemToggleType.None : MenuItemToggleType.CheckBox,
                        IsChecked = item.Checked == true,
                    };
                    if (item.Hint is not null) ToolTip.SetTip(mi, item.Hint);
                    mi.Click += (_, _) => item.Run();
                    flyout.Items.Add(mi);
                }
                b.Flyout = flyout;
            }
            else b.Click += (_, _) => tool.Run();
            Toolbar.Children.Add(b);
        }
    }

    // ---- used by the window and by the render harness ------------------------

    /// <summary>Mount the game and fill the tree, off the UI thread.</summary>
    public async Task LoadGame()
    {
        try
        {
            // A project opened while this runs parks its mods rather than reading them against
            // a game half mounted (OpenProject); they are taken up just below.
            _mounting = true;
            _settings.AutoDetect();
            await Task.Run(() => _session.Mount(_settings, _ => { }));
            AfterMount();
            await ReopenLastProject();
            _mounting = false;
            // A project opened before the game was, or the mods a reload closed.
            await ReopenModsAfterLoad();
        }
        catch (Exception ex) { Say("could not load the game: " + ex.Message); }
        finally { _mounting = false; RefreshSetupNeeded(); }
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
            _nothingAwaited = true;
            _settings.AutoDetect();
            _session.Mount(_settings, _ => { });
            AfterMount();
        }
        catch (Exception ex) { Say("could not load the game: " + ex.Message); }
        finally { RefreshSetupNeeded(); }
    }

    /// <summary>Setup pulses while no game is loaded (see the needsSetup style), and stops once one is.</summary>
    internal void RefreshSetupNeeded() =>
        SetupTab.Classes.Set("needsSetup", _session.Provider is not { Files.Count: > 0 } || _roots.Count == 0);

    private void AfterMount()
    {
        _voiceSets = null;
        _extractor = null;   // its containers are the previous mount's
        PopulateTree();
        RefreshNewLinesNode();
        RefreshAddedNode();
        Say($"{_session.Characters.Count} characters, " +
            $"{_session.OtherBanks.Sum(g => g.Skins.Count)} other .bnk files.");
    }

    /// <summary>Select one of a skin's banks, so a render has real rows in it.</summary>
    public void ShowSkinSync(string skinId, string bankFragment = null)
    {
        // Walked down rather than looked one level into, because the two halves of the
        // tree are not the same depth: a character holds skins which hold banks, while the
        // banks belonging to no character sit under one root and a group per kind. Without
        // this the music, ambience, UI and map banks could not be rendered at all, which
        // is where the rows nobody had seen before are.
        var trail = Trail(_roots, n => n.Skin?.SkinId == skinId);
        if (trail is null) { Say($"no skin {skinId} in this game"); return; }
        var found = trail[^1];

        // A skin node holds its banks; a bank belonging to no character is already one.
        var bank = found.Children.Count == 0 ? found
                 : found.Children.FirstOrDefault(b =>
                       bankFragment is null ||
                       b.Label.Contains(bankFragment, StringComparison.OrdinalIgnoreCase))
                   ?? found.Children.FirstOrDefault();
        if (bank is null) return;

        foreach (var node in trail) node.Expanded = true;

        // Containers are prepared once, and the flag is read then -- so a node opened
        // after the tree was built stays shut unless the items are handed over again.
        Tree.ItemsSource = null;
        Tree.ItemsSource = _roots;

        _picked.Clear();
        _picked.Add(bank);
        Rebuild();
        Tree.SelectedItems.Add(bank);
    }

    /// <summary>
    /// The nodes from a root down to the first one that matches, or null when none does.
    /// The path and not just the node, because every ancestor has to be opened for the
    /// match to be visible in a render.
    /// </summary>
    private static List<Node> Trail(IEnumerable<Node> nodes, Func<Node, bool> match)
    {
        foreach (var node in nodes ?? [])
        {
            if (match(node)) return [node];
            if (Trail(node.Children, match) is not { } below) continue;
            below.Insert(0, node);
            return below;
        }
        return null;
    }

    /// <summary>
    /// Put rows in the list with no game behind them.
    ///
    /// The staged pane's Replaces column is read off the open rows, so a check that a
    /// replacement still fills that cell in needs rows for the staged ids to be found in.
    /// Mounting a game for it would make the check pass or fail on whether the machine
    /// running it has the paks, an AES key and a matching usmap, which is a test of the
    /// machine. Rows are a plain list of plain objects, so handing some over directly is
    /// the honest way to ask the question.
    /// </summary>
    public void PretendRows(SkinSounds rows)
    {
        _sounds = rows;
        ApplyFilter();
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
