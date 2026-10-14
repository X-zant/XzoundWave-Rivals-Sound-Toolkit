using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

namespace MRAudioKit.UI;

/// <summary>
/// New voice lines: which trigger a line answers, under which condition, what its subtitle
/// says and who hears it. That is all this window holds. A line's audio is a staged file like
/// any other, on the line's own slot under "New lines" in the main window, and it is previewed,
/// levelled and built there -- this window only says whether it has one.
///
/// Every edit goes straight into the project (<see cref="Project.VoiceLines"/>) and is
/// reported to the main window, which keeps its "New lines" node and its dirty flag current.
/// </summary>
public partial class VoiceLinesWindow : Window
{
    private readonly MainWindow _owner;
    private readonly List<VoiceSet> _sets;
    private readonly Dictionary<string, VoiceSet> _setByKey;
    // Every hero a line can be keyed by (a victim, a teammate): the heroes with a default set.
    private readonly List<VoiceSet> _heroes;

    // Every VoiceID the game ships: a new line's id must be none of them. Read once, in the
    // background, because it opens every voice table in the game.
    private readonly Task<HashSet<int>> _gameIds;

    // Per VoiceItem: each trigger's entries, and whether it hands heals to the "Healed" triggers.
    // Per set: what each line of its table says, and whether "relevant" players hear it.
    private readonly Dictionary<string, Dictionary<string, List<VoiceTriggers.Entry>>> _entries = [];
    private readonly Dictionary<string, bool> _healsByHealth = [];
    private readonly Dictionary<string, Dictionary<int, VoiceTriggers.Row>> _rows = [];

    private VoiceSet _set;                        // the top bar's set; null: all of them
    private string _item;                         // the top bar's form of that set (its VoiceItem)
    private bool _adding;                         // a line is being added: its id is being read
    private CheckBox _relevantBox;                // the composer's fourth audience
    private VoiceTriggers.Trigger _trigger;       // the side list's trigger; null: all your lines
    private VoiceLine _line;                      // the line in the composer
    private bool _quiet;                          // set while lists are refilled from code
    private readonly Dictionary<VoiceLine, TextBlock> _subtitleCells = [];
    private readonly DispatcherTimer _typing;

    private Project Project => _owner.CurrentProject;
    private GameSession Session => _owner.CurrentSession;

    public VoiceLinesWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        _sets = owner.VoiceSetsOrNull() ?? [];
        _setByKey = _sets.ToDictionary(s => s.Key);
        _heroes = _sets.Where(s => s.IsDefault).GroupBy(s => s.HeroId).Select(g => g.First())
                       .OrderBy(s => s.HeroName, StringComparer.OrdinalIgnoreCase).ToList();
        var session = Session;
        _gameIds = Task.Run(() => VoiceLineBuilder.AllVoiceIds(session));

        // A subtitle is applied as it is typed; the main window hears about it once typing pauses.
        _typing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _typing.Tick += (_, _) => { _typing.Stop(); _owner.VoiceLinesChanged(); };
        Closing += (_, _) => { if (_typing.IsEnabled) { _typing.Stop(); _owner.VoiceLinesChanged(); } };

        SetFilter.SelectionChanged += (_, _) =>
        {
            if (_quiet) return;
            _set = SetFilter.SelectedIndex <= 0 ? null : _sets[SetFilter.SelectedIndex - 1];
            _item = _set?.ItemPackages[0];
            RefreshItemFilter();
            RefreshTriggers();
            RefreshLines();
            RefreshNewButton();
        };
        ItemFilter.SelectionChanged += (_, _) =>
        {
            if (_quiet || _set is null || ItemFilter.SelectedIndex < 0) return;
            _item = _set.ItemPackages[ItemFilter.SelectedIndex];
            RefreshTriggers();
            RefreshNewButton();
        };
        Triggers.SelectionChanged += (_, _) =>
        {
            if (_quiet) return;
            _trigger = (Triggers.SelectedItem as ListBoxItem)?.Tag as VoiceTriggers.Trigger;
            RefreshLines();
            RefreshNewButton();
        };
        Lines.SelectionChanged += (_, _) =>
        {
            if (_quiet) return;
            _line = (Lines.SelectedItem as ListBoxItem)?.Tag as VoiceLine;
            Compose();
        };
        BtnNewLine.Click += async (_, _) => await NewLine();

        RefreshSetFilter();
        RefreshItemFilter();
        RefreshTriggers();
        RefreshLines();
        RefreshNewButton();
        Compose();
    }

    // ---- called by the main window ----------------------------------------------------------

    /// <summary>The project was replaced (New, Open): everything shown came from the old one.</summary>
    internal void ProjectReplaced()
    {
        _typing.Stop();
        _line = null;
        RefreshSetFilter();
        RefreshTriggers();
        RefreshLines();
        Compose();
    }

    /// <summary>Audio was staged or unstaged in the main window.</summary>
    internal void AudioChanged()
    {
        RefreshLines();
        if (_line is not null) Compose();
    }

    // ---- reading a set ----------------------------------------------------------------------

    /// <summary>One VoiceItem's entries, every trigger: a hero with several forms keeps one per form, and they differ.</summary>
    private Dictionary<string, List<VoiceTriggers.Entry>> EntriesOf(VoiceSet set, string item)
    {
        if (!_entries.TryGetValue(item, out var e))
        {
            try { e = VoiceTriggers.AllEntries(Session, set, item); }
            catch (Exception ex) { Status.Text = $"could not read {item}: {ex.Message}"; e = []; }
            _entries[item] = e;
        }
        return e;
    }

    private List<VoiceTriggers.Entry> EntriesOf(VoiceSet set, string item, VoiceTriggers.Trigger t) =>
        EntriesOf(set, item).GetValueOrDefault(t.Field) ?? [];

    /// <summary>The entries a line's own VoiceItem (its form) has for its trigger.</summary>
    private List<VoiceTriggers.Entry> EntriesOf(VoiceLine l, VoiceSet set, VoiceTriggers.Trigger t) =>
        EntriesOf(set, l.ItemIn(set), t);

    private VoiceTriggers.Row RowOf(VoiceSet set, int voiceId)
    {
        if (!_rows.TryGetValue(set.Key, out var map))
        {
            try { map = VoiceTriggers.Rows(Session, set); }
            catch { map = []; }
            _rows[set.Key] = map;
        }
        return map.GetValueOrDefault(voiceId);
    }

    private string SubtitleOf(VoiceSet set, int voiceId) => RowOf(set, voiceId)?.Subtitle ?? "";

    /// <summary>Whether a VoiceItem hands every heal to the two "Healed" triggers instead of its buff entries.</summary>
    private bool HealsByHealth(string item)
    {
        if (!_healsByHealth.TryGetValue(item, out var b))
        {
            try { b = VoiceTriggers.ItemFlag(Session, item, VoiceTriggers.HealsByHealthFlag); }
            catch { b = false; }
            _healsByHealth[item] = b;
        }
        return b;
    }

    /// <summary>An entry keyed by the heal tag in a VoiceItem that never picks one: the heal goes to "Healed".</summary>
    private bool HealDeferred(VoiceSet set, string item, VoiceTriggers.Trigger t, IReadOnlyDictionary<string, string> keys) =>
        t.HealTagDefers && VoiceItemPath.SameKey(keys.GetValueOrDefault("Tag.TagName"), VoiceTriggers.HealTag) && HealsByHealth(item);

    /// <summary>Entries a line can join and be heard from: they have lines to copy, and the game picks them.</summary>
    private List<VoiceTriggers.Entry> Usable(VoiceSet set, string item, VoiceTriggers.Trigger t) =>
        EntriesOf(set, item, t).Where(e => e.Voices.Count > 0 && !HealDeferred(set, item, t, e.Keys)).ToList();

    /// <summary>A form of a set with several, as "form 2 of 3".</summary>
    private static string FormName(VoiceSet set, string item) =>
        $"form {set.ItemPackages.ToList().IndexOf(item) + 1} of {set.ItemPackages.Count}";

    /// <summary>
    /// Why one of a set's VoiceItems cannot take a line for a trigger, or null when it can. A
    /// new line copies its routing (row, event, mixer) from a line already there, so a trigger
    /// with none has nothing to copy from.
    /// </summary>
    private string Unavailable(VoiceSet set, string item, VoiceTriggers.Trigger t)
    {
        var entries = EntriesOf(set, item, t);
        var where = set.ItemPackages.Count > 1 ? $"{set.Label} ({FormName(set, item)})" : set.Label;
        if (entries.Count == 0) return $"{where}'s voice item has no {t.Field}";
        if (!entries.Any(e => e.Voices.Count > 0)) return $"{where} has no line here yet to copy routing from";
        if (Usable(set, item, t).Count == 0) return $"{where} voices these through \"Healed\" instead";
        return null;
    }

    /// <summary>The entry a line's target names now, among its VoiceItem's entries; null for a new one.</summary>
    private static VoiceTriggers.Entry CurrentEntry(VoiceLine l, VoiceTriggers.Trigger t, List<VoiceTriggers.Entry> entries)
    {
        var (keys, index) = VoiceTriggers.Condition(l);
        return t.Shape == VoiceTriggers.Shape.Single ? entries.FirstOrDefault()
             : keys.Count > 0 ? VoiceTriggers.Find(entries, keys)
             : entries.ElementAtOrDefault(index);
    }

    /// <summary>
    /// The fourth audience as the build will copy it when the line leaves it alone: from the
    /// first of the game's own lines where it goes (for a new entry, the list's first line),
    /// the same template the build takes its routing from.
    /// </summary>
    private bool TemplateRelevant(VoiceLine l, VoiceSet set, VoiceTriggers.Trigger t)
    {
        var entries = EntriesOf(l, set, t);
        var ours = Project.VoiceLines.Select(x => x.VoiceId).ToHashSet();
        var voice = CurrentEntry(l, t, entries)?.Voices.FirstOrDefault(v => !ours.Contains(v)) ?? 0;
        if (voice == 0) voice = entries.FirstOrDefault()?.Voices.FirstOrDefault(v => !ours.Contains(v)) ?? 0;
        return RowOf(set, voice)?.Relevant ?? false;
    }

    // ---- top bar and side list --------------------------------------------------------------

    private void RefreshSetFilter()
    {
        _quiet = true;
        var items = new List<string> { "All voice sets" };
        foreach (var s in _sets)
        {
            var n = Project.VoiceLines.Count(l => l.Set == s.Key);
            items.Add(n > 0 ? $"{s.Label}   ({n})" : s.Label);
        }
        SetFilter.ItemsSource = items;
        SetFilter.SelectedIndex = _set is null ? 0 : _sets.IndexOf(_set) + 1;
        _quiet = false;
    }

    /// <summary>The form picker: shown only for a set with one VoiceItem per form.</summary>
    private void RefreshItemFilter()
    {
        _quiet = true;
        var many = _set is { ItemPackages.Count: > 1 };
        ItemFilter.IsVisible = many;
        if (many)
        {
            ItemFilter.ItemsSource = _set.ItemPackages.Select(p => $"{FormName(_set, p)}  ({p[(p.LastIndexOf('/') + 1)..]})").ToList();
            ItemFilter.SelectedIndex = Math.Max(0, _set.ItemPackages.ToList().IndexOf(_item));
        }
        _quiet = false;
    }

    private void RefreshTriggers()
    {
        _quiet = true;
        var items = new List<ListBoxItem>();
        var mine = Project.VoiceLines.Where(l => _set is null || l.Set == _set.Key).ToList();
        items.Add(TriggerItem("All your lines", mine.Count, null, null));
        foreach (var group in VoiceTriggers.All.GroupBy(t => t.Group))
        {
            items.Add(new ListBoxItem
            {
                IsEnabled = false,
                Focusable = false,
                Padding = new Avalonia.Thickness(8, 10, 8, 2),
                Content = new TextBlock { Text = group.Key.ToUpperInvariant(), FontSize = 11, Foreground = Palette.Muted },
            });
            foreach (var t in group)
                items.Add(TriggerItem(t.Name, mine.Count(l => VoiceTriggers.Of(l) == t), t,
                                      _set is null ? null : Unavailable(_set, _item, t)));
        }
        Triggers.Items.Clear();
        foreach (var i in items) Triggers.Items.Add(i);
        Triggers.SelectedItem = _trigger is null ? items[0] : items.FirstOrDefault(i => i.Tag == _trigger) ?? items[0];
        _quiet = false;
    }

    private static ListBoxItem TriggerItem(string name, int count, VoiceTriggers.Trigger t, string unavailable)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = unavailable is null ? Palette.Fg : Palette.Muted,
            FontWeight = t is null ? FontWeight.Bold : FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (count > 0)
        {
            var c = new TextBlock { Text = count.ToString(), Foreground = Palette.Accent, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
            Grid.SetColumn(c, 1);
            grid.Children.Add(c);
        }
        var item = new ListBoxItem { Tag = t, Content = grid };
        ToolTip.SetTip(item, unavailable is null ? t?.Description : $"{t!.Description}\n\nNot for this set: {unavailable}.");
        return item;
    }

    /// <param name="status">
    /// Whether to put the hint in the status line: not after an add, whose own message (the
    /// line added, or why not) is what the status line has to show.
    /// </param>
    private void RefreshNewButton(bool status = true)
    {
        string why = null;
        if (_adding) why = "Adding a line…";
        else if (_set is null) why = "Pick a voice set at the top first.";
        else if (_trigger is null) why = "Pick a trigger on the left first.";
        else if (Unavailable(_set, _item, _trigger) is { } u) why = u + ".";
        BtnNewLine.IsEnabled = why is null;
        ToolTip.SetTip(BtnNewLine, why ?? $"A new {_trigger!.Name.ToLowerInvariant()} line for {_set!.Label}.");
        if (status && _line is null) Status.Text = why ?? $"+ New line adds one to {_set!.Label}: {_trigger!.Name}.";
    }

    // ---- the table of your lines ------------------------------------------------------------

    private void RefreshLines()
    {
        _quiet = true;
        _subtitleCells.Clear();
        Lines.Items.Clear();
        var shown = Project.VoiceLines
            .Where(l => (_set is null || l.Set == _set.Key) && (_trigger is null || VoiceTriggers.Of(l) == _trigger))
            .ToList();
        ListBoxItem selected = null;
        foreach (var l in shown)
        {
            var item = LineRow(l);
            Lines.Items.Add(item);
            if (l == _line) selected = item;
        }
        if (shown.Count == 0)
            Lines.Items.Add(new ListBoxItem
            {
                IsEnabled = false,
                Content = new TextBlock
                {
                    Text = Project.VoiceLines.Count == 0 ? "No new lines yet." : "None of your lines match this.",
                    Foreground = Palette.Muted,
                },
            });
        Lines.SelectedItem = selected;
        if (selected is null && _line is not null && !shown.Contains(_line)) { _line = null; Compose(); }
        _quiet = false;
    }

    private ListBoxItem LineRow(VoiceLine l)
    {
        var set = _setByKey.GetValueOrDefault(l.Set);
        var trigger = VoiceTriggers.Of(l);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,220,*,90") };
        void Cell(int col, string text, IBrush ink)
        {
            var tb = new TextBlock { Text = text, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Avalonia.Thickness(0, 0, 10, 0) };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
            if (col == 2) _subtitleCells[l] = tb;
        }
        Cell(0, set is null ? "(set not in this game)"
                : set.ItemPackages.Count > 1 ? $"{set.Label}, {FormName(set, l.ItemIn(set))}" : set.Label,
             set is null ? Palette.TagRed : Palette.Fg);
        var condition = ConditionText(l, set, trigger);
        Cell(1, (trigger?.Name ?? l.Target) + (condition.Length > 0 ? ": " + condition : ""), Palette.Fg);
        Cell(2, SubtitleCell(l), string.IsNullOrWhiteSpace(l.Subtitle) ? Palette.Muted : Palette.Fg);
        var staged = HasAudio(l);
        Cell(3, staged ? "staged" : "no audio", staged ? Palette.TagGreen : Palette.TagRed);
        return new ListBoxItem { Tag = l, Content = grid };
    }

    private static string SubtitleCell(VoiceLine l) => string.IsNullOrWhiteSpace(l.Subtitle) ? "(no subtitle)" : l.Subtitle;

    private bool HasAudio(VoiceLine l) => _owner.StagedFiles.Any(p => p.MediaId == l.MediaId);

    /// <summary>A line's condition in words: the hero it is keyed by, the entry's keys, or which entry.</summary>
    private string ConditionText(VoiceLine l, VoiceSet set, VoiceTriggers.Trigger t)
    {
        if (t is null || t.Shape == VoiceTriggers.Shape.Single) return "";
        var (keys, index) = VoiceTriggers.Condition(l);
        if (t.HeroKey && keys.TryGetValue("HeroID", out var hero))
            return HeroText(hero, keys.GetValueOrDefault("SculptID") ?? "0", set is null ? [] : EntriesOf(l, set, t));
        if (keys.Count > 0) return string.Join(", ", keys.Values.Select(Short));
        if (set is not null && index >= 0 && EntriesOf(l, set, t).ElementAtOrDefault(index) is { } e)
            return EntryText(set, e);
        return index >= 0 ? $"entry {index}" : "";
    }

    private string HeroText(string heroId, string sculpt, List<VoiceTriggers.Entry> entries)
    {
        if (heroId == "0") return "anyone";
        var name = _heroes.FirstOrDefault(h => h.HeroId == heroId)?.HeroName ?? heroId;
        return Forms(heroId, entries).Count > 1 ? $"{name} (form {sculpt})" : name;
    }

    /// <summary>The last part of a dotted tag or enum: "Voice.Gameplay.Building.ParkerNest" reads as "ParkerNest".</summary>
    private static string Short(string value)
    {
        var cut = Math.Max(value.LastIndexOf('.'), value.LastIndexOf(':'));
        return cut >= 0 && cut < value.Length - 1 ? value[(cut + 1)..] : value;
    }

    /// <summary>An existing entry, as what its first line says.</summary>
    private string EntryText(VoiceSet set, VoiceTriggers.Entry e)
    {
        var keys = e.Keys.Count > 0 ? string.Join(", ", e.Keys.Values.Select(Short)) + "   " : "";
        var first = e.Voices.Count > 0 ? $"\"{SubtitleOf(set, e.Voices[0])}\"" : "(no lines)";
        var more = e.Voices.Count > 1 ? $"  +{e.Voices.Count - 1}" : "";
        return keys + first + more;
    }

    /// <summary>
    /// A hero's forms: the sculpts the skin table states for it, and any other the set's own
    /// entries already key it by (Loki's second form appears only there).
    /// </summary>
    private List<int> Forms(string heroId, List<VoiceTriggers.Entry> entries)
    {
        var forms = new SortedSet<int>(_heroes.FirstOrDefault(h => h.HeroId == heroId)?.Sculpts ?? [0]);
        foreach (var e in entries)
            if (e.Keys.GetValueOrDefault("HeroID") == heroId && int.TryParse(e.Keys.GetValueOrDefault("SculptID"), out var s))
                forms.Add(s);
        return forms.ToList();
    }

    // ---- making a line ----------------------------------------------------------------------

    private async Task NewLine()
    {
        if (_adding || _set is null || _trigger is null || Unavailable(_set, _item, _trigger) is not null) return;
        var set = _set;
        var item = _item;
        var t = _trigger;
        var project = Project;
        // One at a time: a second click while the first reads the game's ids would be handed
        // the same id, since neither line is in the project yet.
        _adding = true;
        RefreshNewButton();
        Status.Text = "reading the game's voice ids…";
        try
        {
            var gameIds = await _gameIds;
            var session = Session;
            var taken = project.VoiceLines.Select(l => l.VoiceId).ToList();
            var id = await Task.Run(() => VoiceLineBuilder.AllocateVoiceId(session, set, taken, gameIds));
            // The project was replaced while the id was read (New, Open): the line has no home.
            if (!ReferenceEquals(project, Project))
            {
                Status.Text = "another project was opened while the line was being added; no line was added.";
                return;
            }
            if (project.VoiceLines.Any(l => l.VoiceId == id))
                id = VoiceLineBuilder.AllocateVoiceId(session, set, project.VoiceLines.Select(l => l.VoiceId), gameIds);
            // It starts on the first entry that has lines: always a target the build accepts,
            // and the composer is where it is changed.
            var start = Usable(set, item, t).First();
            var line = new VoiceLine
            {
                Set = set.Key,
                Item = set.ItemPackages.Count > 1 ? item : null,
                VoiceId = id,
                Target = VoiceTriggers.Target(t, start.Keys, start.Index),
            };
            project.VoiceLines.Add(line);
            _line = line;
            _owner.VoiceLinesChanged();
            RefreshSetFilter();
            RefreshTriggers();
            RefreshLines();
            Compose();
            Status.Text = $"added VoiceID {id}. Give it a subtitle and a condition here, and its audio in the main window under \"New lines\".";
        }
        catch (Exception ex) { Status.Text = "could not add a line: " + ex.Message; }
        finally { _adding = false; RefreshNewButton(status: false); }
    }

    private void Delete(VoiceLine l)
    {
        Project.VoiceLines.Remove(l);
        if (_line == l) _line = null;
        _owner.VoiceLinesChanged();
        RefreshSetFilter();
        RefreshTriggers();
        RefreshLines();
        Compose();
        Status.Text = $"removed VoiceID {l.VoiceId}.";
    }

    // ---- the composer -----------------------------------------------------------------------

    private void Compose()
    {
        ComposerBody.Children.Clear();
        var l = _line;
        if (l is null)
        {
            ComposerBody.Children.Add(Muted(
                "Pick a voice set at the top and a trigger on the left, then + New line. " +
                "Pick one of your lines in the table to edit it."));
            return;
        }
        var set = _setByKey.GetValueOrDefault(l.Set);
        var t = VoiceTriggers.Of(l);
        if (set is null || t is null)
        {
            ComposerBody.Children.Add(Muted($"This line's voice set or trigger is not in this game ({l.Set}, {l.Target}). It cannot be built; delete it or open the game it was made for."));
            ComposerBody.Children.Add(DeleteButton(l));
            return;
        }

        // Title: what fires it, for whom, and the way out.
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock
        {
            Text = $"{t.Name}  ·  {set.Label}" + (set.ItemPackages.Count > 1 ? $", {FormName(set, l.ItemIn(set))}" : ""),
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            Foreground = Palette.Fg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var del = DeleteButton(l);
        Grid.SetColumn(del, 1);
        head.Children.Add(del);
        ComposerBody.Children.Add(head);
        ComposerBody.Children.Add(Muted(t.Description));

        ComposerBody.Children.Add(Field("When", ConditionEditor(l, set, t)));
        if (VoiceTriggers.KeysDiffer(t, l) is { } stale)
            ComposerBody.Children.Add(new TextBlock
            {
                Text = $"This line's target {stale}. Until then it cannot be built.",
                Foreground = Palette.TagRed,
                TextWrapping = TextWrapping.Wrap,
            });

        var subtitle = new TextBox { Text = l.Subtitle, PlaceholderText = "What the subtitle shows while the line plays", MinWidth = 300 };
        subtitle.TextChanged += (_, _) =>
        {
            if (l.Subtitle == subtitle.Text) return;
            l.Subtitle = subtitle.Text ?? "";
            if (_subtitleCells.TryGetValue(l, out var cell))
            {
                cell.Text = SubtitleCell(l);
                cell.Foreground = string.IsNullOrWhiteSpace(l.Subtitle) ? Palette.Muted : Palette.Fg;
            }
            _typing.Stop();
            _typing.Start();
        };
        ComposerBody.Children.Add(Field("Subtitle", subtitle));

        // A trigger only ever spoken to teammates says so when Teammates is off: the line
        // would never be heard at all.
        var alliesNote = new TextBlock
        {
            Text = "Only teammates ever hear this trigger's lines: with Teammates off, nobody will.",
            Foreground = Palette.TagRed,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = t.NeedsAllies && !l.HearAllies,
        };
        var heard = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        heard.Children.Add(Flag("You", l.HearSelf, v => l.HearSelf = v));
        heard.Children.Add(Flag("Teammates", l.HearAllies, v => { l.HearAllies = v; alliesNote.IsVisible = t.NeedsAllies && !v; }));
        heard.Children.Add(Flag("Enemies", l.HearEnemies, v => l.HearEnemies = v));
        _relevantBox = Flag("Those it involves", l.HearRelevant ?? TemplateRelevant(l, set, t), v => l.HearRelevant = v);
        ToolTip.SetTip(_relevantBox,
            "Whoever the moment involves, on either team: the player a buff lands on or comes from, the teammate " +
            "a revive is for. Starts as the game's own lines in the same place have it.");
        heard.Children.Add(_relevantBox);
        var heardPanel = new StackPanel { Spacing = 2 };
        heardPanel.Children.Add(heard);
        heardPanel.Children.Add(alliesNote);
        ComposerBody.Children.Add(Field("Heard by", heardPanel));

        var staged = _owner.StagedFiles.FirstOrDefault(p => p.MediaId == l.MediaId);
        var audio = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        audio.Children.Add(new TextBlock
        {
            Text = staged is null ? "none yet" : $"{staged.FileName}{(staged.Seconds is { } s ? $"  ({s:0.00} s)" : "")}",
            Foreground = staged is null ? Palette.TagRed : Palette.TagGreen,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var show = new Button { Content = staged is null ? "Stage it in the main window" : "Show in the main window", Classes = { "tool" } };
        show.Click += (_, _) => _owner.RevealNewLines();
        audio.Children.Add(show);
        ComposerBody.Children.Add(Field("Audio", audio));

        ComposerBody.Children.Add(Muted($"VoiceID {l.VoiceId}  ·  event {l.EventName}  ·  media {l.MediaId}"));
    }

    /// <summary>Which entry the line joins, in the trigger's own terms.</summary>
    private Control ConditionEditor(VoiceLine l, VoiceSet set, VoiceTriggers.Trigger t)
    {
        var entries = EntriesOf(l, set, t);
        var item = l.ItemIn(set);
        var panel = new StackPanel { Spacing = 4 };
        var note = Muted("");

        void Retarget(IReadOnlyDictionary<string, string> keys, int index)
        {
            if (HealDeferred(set, item, t, keys))
            {
                note.Text = HealNote;
                return;
            }
            var target = VoiceTriggers.Target(t, keys, index);
            if (target != l.Target)
            {
                l.Target = target;
                _owner.VoiceLinesChanged();
                RefreshLines();
                // A line left as its siblings follows the template of the entry it now joins.
                if (l.HearRelevant is null && _relevantBox is not null)
                {
                    _quiet = true;
                    _relevantBox.IsChecked = TemplateRelevant(l, set, t);
                    _quiet = false;
                }
            }
            note.Text = JoinNote(l, set, t);
        }

        if (t.Shape == VoiceTriggers.Shape.Single)
        {
            note.Text = JoinNote(l, set, t);
            panel.Children.Add(note);
            return panel;
        }

        var (keys, index) = VoiceTriggers.Condition(l);

        if (t.HeroKey)
        {
            // Keyed by a hero: pick the hero; the entry is found, or made when it is new.
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var choices = new List<(string Id, string Label)>();
            if (entries.Any(e => e.Keys.GetValueOrDefault("HeroID") == "0"))
                choices.Add(("0", "Anyone (when no other entry matches)"));
            foreach (var h in _heroes)
            {
                var have = entries.Where(e => e.Keys.GetValueOrDefault("HeroID") == h.HeroId).Sum(e => e.Voices.Count);
                choices.Add((h.HeroId, have > 0 ? $"{h.HeroName}   (has {have})" : h.HeroName));
            }
            var heroBox = new ComboBox { ItemsSource = choices.Select(c => c.Label).ToList(), MinWidth = 260 };
            var formBox = new ComboBox { MinWidth = 90 };
            row.Children.Add(heroBox);
            row.Children.Add(formBox);
            var hero = keys.GetValueOrDefault("HeroID") ?? "0";
            heroBox.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Id == hero));

            void FillForms(string heroId, string want)
            {
                List<int> forms = heroId == "0" ? [0] : Forms(heroId, entries);
                formBox.ItemsSource = forms.Select(f => $"form {f}").ToList();
                formBox.IsVisible = forms.Count > 1;
                formBox.Tag = forms;
                formBox.SelectedIndex = Math.Max(0, forms.IndexOf(int.TryParse(want, out var w) ? w : 0));
            }
            void Apply()
            {
                if (heroBox.SelectedIndex < 0) return;
                var heroId = choices[heroBox.SelectedIndex].Id;
                var forms = (List<int>)formBox.Tag ?? [0];
                var form = forms.ElementAtOrDefault(Math.Max(0, formBox.SelectedIndex));
                Retarget(new Dictionary<string, string> { ["HeroID"] = heroId, ["SculptID"] = form.ToString() }, -1);
            }
            FillForms(hero, keys.GetValueOrDefault("SculptID"));
            heroBox.SelectionChanged += (_, _) => { FillForms(choices[Math.Max(0, heroBox.SelectedIndex)].Id, "0"); Apply(); };
            formBox.SelectionChanged += (_, _) => Apply();
            panel.Children.Add(row);
            note.Text = JoinNote(l, set, t);
            panel.Children.Add(note);
            return panel;
        }

        // Any other trigger: one of the set's entries, and -- where the game picks new ones --
        // key values of your own.
        var entryBox = new ComboBox
        {
            ItemsSource = entries.Select(e => EntryText(set, e)).ToList(),
            MinWidth = 420,
            MaxWidth = 640,
        };
        var current = keys.Count > 0 ? VoiceTriggers.Find(entries, keys) : entries.ElementAtOrDefault(index);
        entryBox.SelectedIndex = current is null ? -1 : entries.IndexOf(current);
        panel.Children.Add(entryBox);

        var inputs = new Dictionary<string, AutoCompleteBox>();
        if (t.NewEntry && t.Keys is { Length: > 0 })
        {
            var grid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var k in t.Keys)
            {
                var box = new AutoCompleteBox
                {
                    ItemsSource = entries.Select(e => e.Keys.GetValueOrDefault(k) ?? "").Where(v => v.Length > 0).Distinct().ToList(),
                    Text = keys.GetValueOrDefault(k) ?? "",
                    MinimumPrefixLength = 0,
                    FilterMode = AutoCompleteFilterMode.Contains,
                    PlaceholderText = k,
                    MinWidth = k.Contains("Tag") ? 300 : 140,
                };
                ToolTip.SetTip(box, k);
                inputs[k] = box;
                grid.Children.Add(box);
            }
            panel.Children.Add(Muted("or your own values for a new entry:"));
            panel.Children.Add(grid);
        }

        entryBox.SelectionChanged += (_, _) =>
        {
            if (_quiet || entryBox.SelectedIndex < 0) return;
            var e = entries[entryBox.SelectedIndex];
            if (HealDeferred(set, item, t, e.Keys))
            {
                note.Text = HealNote;
                _quiet = true;
                entryBox.SelectedIndex = CurrentEntry(l, t, entries) is { } now ? entries.IndexOf(now) : -1;
                _quiet = false;
                return;
            }
            if (inputs.Count > 0)
            {
                _quiet = true;
                foreach (var (k, box) in inputs) box.Text = e.Keys.GetValueOrDefault(k) ?? "";
                _quiet = false;
            }
            Retarget(e.Keys, e.Index);
        };
        foreach (var (k, box) in inputs)
            box.TextChanged += (_, _) =>
            {
                if (_quiet) return;
                // Numbers as the game writes them ("0123" is 123), so a typed value finds its entry.
                var typed = inputs.ToDictionary(i => i.Key, i =>
                {
                    var v = (i.Value.Text ?? "").Trim();
                    var numeric = entries.Count > 0 && entries.All(e => int.TryParse(e.Keys.GetValueOrDefault(i.Key), out _));
                    return numeric && int.TryParse(v, out var n) ? n.ToString() : v;
                });
                var bad = KeyProblem(t, entries, typed);
                if (bad is not null) { note.Text = bad; return; }
                var match = VoiceTriggers.Find(entries, typed);
                _quiet = true;
                entryBox.SelectedIndex = match is null ? -1 : entries.IndexOf(match);
                _quiet = false;
                Retarget(typed, -1);
            };
        note.Text = JoinNote(l, set, t);
        panel.Children.Add(note);
        return panel;
    }

    /// <summary>
    /// What is wrong with typed key values, or null. A key the game stores as a number has to
    /// be one, or the build cannot write it; the existing entries say which keys are numbers.
    /// A new entry whose trigger names a key it must not leave at 0 is one the game never picks.
    /// </summary>
    private static string KeyProblem(VoiceTriggers.Trigger t, List<VoiceTriggers.Entry> entries, Dictionary<string, string> typed)
    {
        foreach (var (k, v) in typed)
        {
            if (v.Length == 0) return $"{k} is empty.";
            var numeric = entries.Count > 0 && entries.All(e => int.TryParse(e.Keys.GetValueOrDefault(k), out _));
            if (numeric && !int.TryParse(v, out _)) return $"{k} is a number in this voice item.";
        }
        if (t.PickKey is { } pk && VoiceTriggers.Find(entries, typed) is null)
        {
            var value = typed.GetValueOrDefault(pk);
            if (!int.TryParse(value, out var n) || n == 0)
                return $"A new entry needs {pk}: the game never picks one left at 0.";
            if (entries.Any(e => VoiceItemPath.SameKey(e.Keys.GetValueOrDefault(pk), value)))
                return $"An entry with {pk} {value} exists, and the game picks it for every buff from that ability: pick that entry above instead.";
        }
        return null;
    }

    private const string HealNote =
        "This hero's heals are voiced by \"Healed\" and \"Healed while low\" instead: the game never picks a heal entry here.";

    /// <summary>What the line's target amounts to: joining an entry's lines, or a new entry.</summary>
    private string JoinNote(VoiceLine l, VoiceSet set, VoiceTriggers.Trigger t)
    {
        var entries = EntriesOf(l, set, t);
        if (HealDeferred(set, l.ItemIn(set), t, VoiceTriggers.Condition(l).Keys)) return HealNote;
        var entry = CurrentEntry(l, t, entries);
        var others = Project.VoiceLines.Count(o => o != l && o.Set == l.Set && o.Target == l.Target);
        var yours = others > 0 ? $" Your {others} other line{(others == 1 ? "" : "s")} here join{(others == 1 ? "s" : "")} it too." : "";
        if (entry is null)
            return t.NewEntry
                ? "A new entry: the game has no lines for this, so it picks only from yours." + yours
                : "No such entry in this voice set.";
        var n = entry.Voices.Count;
        var sample = n > 0 ? $", such as \"{SubtitleOf(set, entry.Voices[0])}\"" : "";
        return $"Joins {n} line{(n == 1 ? "" : "s")} the game already has here{sample}; one is picked at random." + yours;
    }

    // ---- the render harness -----------------------------------------------------------------

    /// <summary>
    /// What a person would do, done through the same controls, so a render shows the window
    /// as it is after real use: pick a set and a trigger, add a line, pick its condition in the
    /// composer, type its subtitle. Returns what each step left behind.
    /// </summary>
    internal string DriveForShot(Action pump)
    {
        var log = new System.Text.StringBuilder();
        void PickSet(string heroId, bool own = false) =>
            SetFilter.SelectedIndex = _sets.FindIndex(s => s.HeroId == heroId && s.IsDefault != own) + 1;
        void PickTrigger(string field) => Triggers.SelectedItem = Triggers.Items.OfType<ListBoxItem>().First(i => (i.Tag as VoiceTriggers.Trigger)?.Field == field);
        void Add()
        {
            var t = NewLine();
            while (!t.IsCompleted) { pump(); Thread.Sleep(10); }
            pump();
        }
        // The first of these triggers the picked set and form can take a line for.
        string AddFirst(params string[] fields)
        {
            foreach (var f in fields)
            {
                PickTrigger(f);
                if (!BtnNewLine.IsEnabled) continue;
                Add();
                return f;
            }
            return null;
        }
        IEnumerable<T> InComposer<T>() => ComposerBody.GetLogicalDescendants().OfType<T>();
        void Type(string text) => InComposer<TextBox>().First(b => b.PlaceholderText?.StartsWith("What the subtitle") == true).Text = text;
        void Hero(string name)
        {
            var box = InComposer<ComboBox>().First();
            box.SelectedIndex = ((IEnumerable<string>)box.ItemsSource).ToList().FindIndex(s => s.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        }

        PickSet("1024");
        PickTrigger("KillVoices");
        Add(); Hero("Iron Man"); Type("Not so invincible now, Stark.");
        Add(); Hero("Iron Man"); Type("Thy armor makes a fine coffin.");
        PickTrigger("HealthPackVoices");
        Add(); Type("A little life, for the Queen of Hel.");
        InComposer<CheckBox>().First(c => (string)c.Content == "Enemies").IsChecked = true;
        PickTrigger("BuildingDestroyedVoices");
        Add(); Type("Another toy broken.");
        // Two voice sets of one hero, which share his voice bank: Loki's own and a skin's.
        PickSet("1016");
        PickTrigger("HealthPackVoices");
        Add(); Type("Loki, patched up.");
        PickSet("1016", own: true);
        PickTrigger("HealthPackVoices");
        Add(); Type("Loki in a different coat, patched up.");
        // Two voice sets sharing one VoiceItem: Spider-Man's own and his 505 skin's table.
        PickSet("1036");
        PickTrigger("HealthPackVoices");
        Add(); Type("Spidey, patched up.");
        PickSet("1036", own: true);
        PickTrigger("HealthPackVoices");
        Add(); Type("Spidey in a different suit, patched up.");
        // A hero with one VoiceItem per form: a line on the second form only.
        PickSet("1011");
        ItemFilter.SelectedIndex = 1;
        log.AppendLine("  Bruce Banner form 2: " + (AddFirst("KillVoices", "UltimateVoices", "RebornVoices", "HealthPackVoices") ?? "NO TRIGGER AVAILABLE"));
        Type("The other guy says hi.");
        // Ability lines (an enum key), and an entry that differs only by its condition.
        PickSet("1024");
        PickTrigger("AbilitiesVoices");
        Add(); Type("A new ability line for Hela.");
        PickSet("1016");
        PickTrigger("AbilitiesVoices");
        Add();
        var advance = InComposer<ComboBox>().First();
        advance.SelectedIndex = ((IEnumerable<string>)advance.ItemsSource).ToList().FindIndex(s => s.Contains("Advance"));
        log.AppendLine($"  Loki Advance entry picked: index {advance.SelectedIndex}");
        Type("Loki, advanced.");
        // The fourth audience: left as the template has it, and set by hand.
        PickSet("1024");
        PickTrigger("ApplyBuffVoices");
        Add(); Type("Hela's buff, heard as its siblings are.");
        PickTrigger("TakeBuffVoices");
        Add(); Type("Hela takes a buff, not heard by the one involved.");
        InComposer<CheckBox>().First(c => (string)c.Content == "Those it involves").IsChecked = false;
        PickSet("1024");
        pump();
        _typing.Stop();
        _owner.VoiceLinesChanged();

        foreach (var l in Project.VoiceLines)
            log.AppendLine($"  {l.VoiceId}  {l.Target}{(l.Item is null ? "" : "  in " + l.Item[(l.Item.LastIndexOf('/') + 1)..])}  \"{l.Subtitle}\"  " +
                           $"self={l.HearSelf} allies={l.HearAllies} enemies={l.HearEnemies} relevant={(l.HearRelevant?.ToString() ?? "as template")}");

        // Leave the first Iron Man line in the composer, under "All your lines".
        Triggers.SelectedIndex = 0;
        Lines.SelectedIndex = 0;
        pump();
        log.AppendLine("  composer: " + string.Join(" | ", ComposerBody.GetLogicalDescendants().OfType<TextBlock>()
                                                         .Select(b => b.Text).Where(s => !string.IsNullOrEmpty(s))));
        log.Append("  status: " + Status.Text);
        return log.ToString();
    }

    // ---- small parts ------------------------------------------------------------------------

    private Button DeleteButton(VoiceLine l)
    {
        // Two clicks, not a dialog: the second within a few seconds of the first deletes.
        var b = new Button { Content = "Delete line", Classes = { "tool" } };
        var armed = false;
        var disarm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        disarm.Tick += (_, _) => { disarm.Stop(); armed = false; b.Content = "Delete line"; };
        b.Click += (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                b.Content = HasAudio(l) ? "Click again: deletes it and unstages its audio" : "Click again to delete";
                disarm.Start();
                return;
            }
            disarm.Stop();
            Delete(l);
        };
        return b;
    }

    private CheckBox Flag(string label, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = label, IsChecked = value, Foreground = Palette.Fg };
        c.IsCheckedChanged += (_, _) => { if (_quiet) return; set(c.IsChecked == true); _owner.VoiceLinesChanged(); };
        return c;
    }

    private static Control Field(string label, Control content)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        grid.Children.Add(new TextBlock { Text = label, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Top, Margin = new Avalonia.Thickness(0, 6, 0, 0) });
        Grid.SetColumn(content, 1);
        content.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(content);
        return grid;
    }

    private static TextBlock Muted(string text) =>
        new() { Text = text, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap };
}
