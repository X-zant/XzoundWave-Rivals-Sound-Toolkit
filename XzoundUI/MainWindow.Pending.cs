using System.IO;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;

namespace MRAudioKit.UI;

/// <summary>
/// The pane down the right: what is staged, and the ways in and out of it.
///
/// Staging already worked from three places before this existed and none of them could
/// be SEEN, which made a mis-stage undoable except by clearing the lot. The pane is
/// therefore a display before it is anything else: it owns no list of its own, stages
/// nothing itself, and every button here goes through the same Build tab methods the
/// toolbar uses.
///
/// It is drawn from <see cref="Counts"/> and nowhere else. That is the one place both
/// routes to the list pass through -- Refresh for a redraw of the same rows, ApplyFilter
/// for a new set of them -- so anything that changes what is staged, or which rows are
/// open, or which banks are known, has already reached the pane by the time it finishes.
/// A funnel further down would be one the next caller can skip.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// What the pane last drew, so it is handed its items again only when a cell would
    /// actually read differently. Re-listing costs the selection, and the columns below
    /// are plain setters with no change notification, so the choice is between a redraw
    /// nobody needs and a stale cell.
    /// </summary>
    private string _pendingDrawn = "";

    /// <summary>
    /// The very objects the grid was handed alongside those readings, which is the other
    /// half of "the pane already shows this".
    ///
    /// Staging over an id that is already staged replaces the item rather than editing
    /// it, and the replacement is bound by the grid with its four computed columns still
    /// empty, because PendingStatus only runs afterwards. The readings above then come
    /// out identical to the ones before -- same ids, same banks, same subtitles -- so
    /// they cannot tell that anything needs re-reading, and the corrected file sits there
    /// with a blank "Lands in", which reads as a file that lands nowhere.
    /// </summary>
    private PendingWem[] _pendingDrawnItems = [];

    private void HookPendingPanel()
    {
        BuildPendingColumns();
        PendingGrid.ItemsSource = _pending;

        // Avalonia has no DataGrid.RowStyle, so the tooltip the WPF pane bound in its
        // row style is set as the row is realised, the same way the sound list sets a
        // row's fill. Null rather than the empty string when there is nothing to say
        // yet: an empty tip still opens, as a bare box hanging off the pointer.
        PendingGrid.LoadingRow += (_, e) => ToolTip.SetTip(e.Row,
            e.Row.DataContext is PendingWem p && !string.IsNullOrWhiteSpace(p.Tip) ? p.Tip : null);

        BtnAddFiles.Click += (_, _) => _ = AddPendingFilesAsync();
        BtnRemovePending.Click += (_, _) => RemovePending();
        BtnClearPending.Click += (_, _) => _ = ClearAllStagedAsync();

        DropFormats.Text = FormatsSentence();

        DropHelp.IsExpanded = Config.ShowDropHelp;
        DropHelp.Expanded += (_, _) => RememberDropHelp();
        DropHelp.Collapsed += (_, _) => RememberDropHelp();

        // Which sub-panel is showing, remembered from last time. The staged list on a first
        // run, because it is the one of the two that has to be visible while something is
        // being dropped and the other says nothing at all until a row is selected.
        PaneTabs.SelectedIndex = Config.RightPanelDetails ? 1 : 0;
        PaneTabs.SelectionChanged += (_, _) => { PaneShown(); RememberPanePick(); };
        PaneShown();

        // The details sub-panel follows the sound list, which is this window's first
        // subscriber to that selection. See MainWindow.Details.cs.
        HookDetails();
    }

    // ---- which sub-panel is showing -----------------------------------------

    /// <summary>
    /// A switch the PROGRAM made rather than the reader, so it must not be written to the
    /// settings file. A drop switches to the staged list, and remembering that as a choice
    /// would have the panel open on a sub-panel nobody picked.
    /// </summary>
    private bool _paneByProgram;

    /// <summary>
    /// Put the right sub-panel on screen. The switch's own SelectedIndex is the state, so
    /// there is one answer to which one is showing and no second flag to keep in step.
    ///
    /// A ListBox can be ctrl+clicked back to nothing selected, which is why this tests for
    /// the details index rather than against the staging one: nothing selected has to leave
    /// the panel showing something.
    /// </summary>
    private void PaneShown()
    {
        var details = PaneTabs.SelectedIndex == 1;
        PaneStaging.IsVisible = !details;
        PaneDetails.IsVisible = details;

        // Nothing is drawn into the details panel while it is hidden, so being shown is
        // when it has to catch up, and it has to do so whether or not the selection moved.
        if (details) ShowDetails(force: true);
    }

    /// <summary>
    /// Show one sub-panel from code, without that counting as the reader's choice. The
    /// guard holds only for as long as the assignment, because Avalonia raises
    /// SelectionChanged synchronously from it.
    /// </summary>
    public void PaneShow(bool details)
    {
        if ((PaneTabs.SelectedIndex == 1) == details) return;
        _paneByProgram = true;
        try { PaneTabs.SelectedIndex = details ? 1 : 0; }
        finally { _paneByProgram = false; }
    }

    /// <summary>
    /// Keep the reader's pick. The same shape the help header uses: compared first, so
    /// nothing is written when the state did not actually move.
    /// </summary>
    private void RememberPanePick()
    {
        if (_paneByProgram) return;
        var details = PaneTabs.SelectedIndex == 1;
        if (Config.RightPanelDetails == details) return;
        Config.RightPanelDetails = details;
        Config.Save();
    }

    /// <summary>
    /// How many files are staged, on the switch's own label.
    ///
    /// While the details sub-panel is showing, the staged list is not on screen and this
    /// number is the only thing saying that a drop landed. It is <c>_pending.Count</c> and
    /// never the toolbar's count, which is a count of rows in the open skin that carry a
    /// replacement: that one read "2 staged" over a panel holding five, because three of
    /// the five belonged to banks nobody had opened.
    /// </summary>
    private void PaneCount() =>
        PaneTabFiles.Content = _pending.Count == 0 ? "Staged files"
                                                  : $"Staged files   {_pending.Count:N0}";

    /// <summary>
    /// Three of the five the WPF pane showed. In code rather than in the markup so the
    /// widths sit beside the method that fills the computed columns, and so the headless
    /// test can assert about them the way it already does about the sound list.
    ///
    /// Codec went first, because five will not fit and it is the one that earns its place
    /// least. Measured on this host, at the 470 px the pane is: the five need 532 px of the
    /// 456 the pane has for columns, and the two that give way are the two star columns,
    /// which came out at 48 px each -- a single letter of header, so the reader cannot even
    /// tell which column they are looking at. Four need 454. And Codec reads VORBIS for very
    /// nearly every staged file, because anything that is not already Wwise Vorbis is
    /// encoded to it on the way in; the exceptions are named in the staging report, listed
    /// again in the "Check these files" dialog, and carried in the row's tooltip, so
    /// dropping the column lost no fact.
    ///
    /// Replaces went with the sub-panels, and it is the one column whose removal is worth
    /// the space rather than merely affordable. Its value is a shipped subtitle: a typical
    /// 43-character line measures 269 px of text plus 24 px of cell, so it needs about 293
    /// px to be read and had 91, which is about eleven characters and an ellipsis. Giving it
    /// what it needs inside this grid would want a pane of roughly 750 px, which is 280 px
    /// off the list. Dropping it instead takes "Your note" from 97 px to 189 px at the same
    /// 470, from about eleven readable characters to about twenty-two, and the fact itself
    /// is now prose on the Details sub-panel where it wraps and needs no width at all. It
    /// stays in the staged row's tooltip too, which is what keeps it reachable while
    /// Details is showing a different sound.
    /// </summary>
    private void BuildPendingColumns()
    {
        void Add(string header, string path, DataGridLength width)
            => PendingGrid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = width,
            });

        // The WPF pane's own numbers were 88 / 165, and on this host's font they cut the
        // word "Media ID" down to "Media I". These two are the design widths for the font
        // this window was drawn against; what they have to hold is decided by
        // <see cref="FloorPending"/> instead, because neither is a width a constant can
        // state. A ten-digit media id is eighteen per cent wider in DejaVu Sans than in
        // Segoe UI, and "Lands in" holds a .bnk FILENAME -- the sound list's own .bnk
        // column shows the stem (SoundIndex.cs:281) while BankOfMedia keeps the name with
        // its extension (SoundIndex.cs:265), so the list's width is not evidence about
        // this one.
        var star = new DataGridLength(1, DataGridLengthUnitType.Star);
        Add("Media ID",  nameof(PendingWem.MediaId),  new DataGridLength(110));
        Add("Your note", nameof(PendingWem.Note),     star);
        Add("Lands in",  nameof(PendingWem.Status),   new DataGridLength(160));

        // The same reason the sound list does it: a DataGridTextColumn leaves
        // TextTrimming at None, so a bank name too wide for its column comes out
        // shortened rather than marked, and a shortened .bnk name is a plausible
        // different .bnk name.
        PendingGrid.Styles.Add(new Style(x => x.Is<DataGridCell>().Descendant().OfType<TextBlock>())
        {
            Setters = { new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis) },
        });
    }

    /// <summary>
    /// Give every pane column a floor wide enough for what it is being asked to show,
    /// measured in the font this host resolved.
    ///
    /// The list floors its own columns from the TYPE, because a media id is bounded above
    /// by uint and a tag by its vocabulary. "Lands in" is not: it holds a .bnk filename
    /// the game chose, which this window cannot enumerate. So the floor is asked of the
    /// rows in front of it, which is also the strictest answer available -- a column that
    /// holds every value it has been handed hides nothing.
    ///
    /// All four, including the two star columns, which is the change that stopped them
    /// collapsing to one letter of header each. Their VALUES are free text with no ceiling
    /// and go on ellipsising with the row's tooltip carrying the rest; what they are
    /// floored to is their header alone, because a column whose header is gone is a column
    /// of text with nothing saying what the text is. When the four floors come to more
    /// than the pane has, the pane scrolls sideways, which is the same answer the sound
    /// list's Subtitle column already gives and is visible rather than silent.
    ///
    /// Only ever raised, so a column cannot narrow under a reader who is looking at it,
    /// and Auto is still not the answer for the reason FloorColumns gives: Auto follows
    /// the rows the grid has REALISED and would step as the pane scrolls.
    /// </summary>
    private void FloorPending()
    {
        foreach (var (header, values) in PendingMustShow())
        {
            var column = PendingGrid.Columns.FirstOrDefault(c => c.Header?.ToString() == header);
            if (column is null) continue;

            var needs = values.Select(v => CellWidth(v))
                              .Append(CellWidth(header, header: true)).Max();
            if (needs > 0) column.MinWidth = Math.Max(column.MinWidth, Math.Ceiling(needs));
        }

        FloorThePane();
    }

    /// <summary>
    /// Give the panel itself a floor wide enough for the columns above, measured rather than
    /// declared.
    ///
    /// The markup's 380 is this host's three columns rounded up, and a wider font needs more:
    /// the floors are measured against whatever typeface the theme resolved, so writing one
    /// number down would leave the panel able to be dragged to where its own grid scrolls
    /// sideways on any machine whose font is wider than this one's. Raised only, like every
    /// column floor, and never past the column's own maximum, which a floor above a ceiling
    /// would otherwise make a layout that cannot be satisfied.
    ///
    /// The details sub-panel imposes no floor of its own. It is prose in a wrapping column,
    /// and at 260 px a 59-character asset path wrapped mid-path rather than clipping, so what
    /// decides this number is the staged grid.
    /// </summary>
    private void FloorThePane()
    {
        if (Body.ColumnDefinitions.Count < 5) return;
        var pane = Body.ColumnDefinitions[4];

        var needs = PendingGrid.Columns.Where(c => c.IsVisible).Sum(c => c.MinWidth)
                    + PendingGrid.Margin.Left + PendingGrid.Margin.Right
                    + DropPane.BorderThickness.Left + DropPane.BorderThickness.Right;
        if (needs <= pane.MinWidth) return;
        pane.MinWidth = Math.Min(pane.MaxWidth, Math.Ceiling(needs));
    }

    /// <summary>
    /// What each pane column has to show whole: its own header always, plus every value
    /// it has been handed for the two that carry an identifier. The star column yields no
    /// values on purpose, because a note has no ceiling and the claim being made about it
    /// is the header and the ellipsis.
    /// </summary>
    private IEnumerable<(string Header, IEnumerable<string> Values)> PendingMustShow()
    {
        yield return ("Media ID",  _pending.Select(p => p.MediaId.ToString()));
        yield return ("Your note", []);
        yield return ("Lands in",  _pending.Select(p => p.Status));
    }

    /// <summary>
    /// Every pane column against what it has to show: its own header, and whatever
    /// <see cref="PendingMustShow"/> adds for it. The same answer <see cref="ColumnFits"/>
    /// gives for the sound list, so one check in the headless test covers both grids --
    /// which is the point, because the pane shipped with two columns clipped to a single
    /// letter while that check existed and was called by nobody.
    ///
    /// Walked from the GRID rather than from the floor table, so a column dropped out of
    /// that table loses its floor and keeps its claim, and the check goes red instead of
    /// quietly having nothing to say about it.
    /// </summary>
    public IReadOnlyList<ColumnFit> PendingColumnFits()
    {
        // Zero is CellWidth saying no cell has been measured yet, which is "this could not
        // be measured" and not "nothing fits". Same answer ColumnFits gives then.
        if (CellWidth("Media ID", header: true) <= 0) return [];

        var table = PendingMustShow().ToDictionary(m => m.Header, m => m.Values);
        var fits = new List<ColumnFit>();
        foreach (var column in PendingGrid.Columns.Where(c => c.IsVisible))
        {
            var header = column.Header?.ToString();
            if (string.IsNullOrEmpty(header)) continue;

            var widest = header;
            var needs = CellWidth(header, header: true);
            foreach (var value in table.GetValueOrDefault(header) ?? [])
            {
                var w = CellWidth(value);
                if (w <= needs) continue;
                needs = w;
                widest = value;
            }
            fits.Add(Fit(PendingGrid, header, widest, needs));
        }
        return fits;
    }

    // ---- drawing it ---------------------------------------------------------

    /// <summary>
    /// Put the pane in step with what is staged. Called from <see cref="Counts"/>.
    /// </summary>
    private void ShowPending()
    {
        PendingStatus();

        // Above the guard below: the count on the switch has to be right even when nothing
        // about the drawn rows moved, and it is the only thing on screen saying what is
        // staged while the details sub-panel is showing.
        PaneCount();

        // And the other sub-panel, which reads the rows on screen and the staged list as
        // well as the selection, so a filter typed or a file staged changes what it says
        // without the selection having moved at all.
        DetailsChanged();

        // Adding and removing are seen without this: the list is an ObservableCollection
        // and the grid follows it. What it cannot follow is the four columns PendingStatus
        // writes in place, so their current reading is fingerprinted and the grid is
        // handed its items again when that moves.
        //
        // Which is not on its own enough, because a replacement on an id that is already
        // staged is a DIFFERENT object bound with those four still empty: the grid follows
        // the remove and the add, PendingStatus fills the new item in afterwards, and the
        // reading is by then the same one as before. So the items themselves are compared
        // as well, and a row whose object was swapped is re-read even though every cell in
        // the pane is about to say what it said before.
        var items = _pending.ToArray();
        var drawn = string.Join("\u0001", items.Select(p =>
            $"{p.MediaId}\u0002{p.Status}\u0002{p.Original}\u0002{p.OriginalEvent}\u0002{p.BankList}"));
        if (drawn == _pendingDrawn && items.SequenceEqual(_pendingDrawnItems)) return;
        _pendingDrawn = drawn;
        _pendingDrawnItems = items;

        // Below the guard above, not before it: every value a floor is measured from is
        // part of that fingerprint, and this runs on each keystroke in the filter box,
        // where a folder of three hundred staged files would otherwise be nine hundred
        // text measurements for an answer that cannot have moved.
        FloorPending();

        var keep = PendingGrid.SelectedItems?.OfType<PendingWem>().ToList() ?? [];
        PendingGrid.ItemsSource = null;
        PendingGrid.ItemsSource = _pending;
        if (keep.Count == 0 || PendingGrid.SelectedItems is not { } back) return;

        // Same guard the sound list's redraw carries: the grid throws for an item the
        // new list does not hold, and a redraw must never be what fails the thing it
        // was called to show.
        try { foreach (var p in keep) back.Add(p); }
        catch (ArgumentException) { }
    }

    /// <summary>
    /// Tell the author what each staged file will actually do: land inside a named bank,
    /// ship as a loose streamed wem, or belong to a skin they have not opened.
    ///
    /// The open skin is asked first and the whole-game index second, so a file staged
    /// before you navigate anywhere still names its bank once the index has caught up.
    /// </summary>
    private void PendingStatus()
    {
        if (_pending.Count == 0) return;

        // One pass over the rows rather than a scan per staged file: a folder drop is
        // hundreds of staged files against thousands of rows, and this runs on every
        // keystroke in the filter box.
        var rowById = new Dictionary<uint, SoundRow>();
        foreach (var r in Sounds?.Rows ?? []) rowById.TryAdd(r.MediaId, r);

        foreach (var p in _pending)
        {
            var inBank = Sounds is not null &&
                         Sounds.BankOfMedia.TryGetValue(p.MediaId, out var b) ? b : null;
            var foreign = false;
            var all = Session?.BanksForMedia(p.MediaId) ?? [];
            if (inBank is null && all.Count > 0)
            {
                inBank = Path.GetFileName(all[0]);
                foreign = true;
            }
            // Say so when the same media sits in several banks: every one of them gets
            // rebuilt, and the author should know that before shipping.
            if (inBank is not null && all.Count > 1) inBank += $"  +{all.Count - 1} more";

            p.BankList = all.Count > 1
                ? string.Join(Environment.NewLine, all.Take(12).Select(x => "  " + Path.GetFileName(x))) +
                  (all.Count > 12 ? $"{Environment.NewLine}  … and {all.Count - 12} more" : "")
                : "";

            var loose = Session?.Provider is not null && Session.LooseMedia(p.MediaId) is not null;

            // Name the bank rather than saying "in a bank": the whole point of knowing
            // where it lands is knowing which single file has to be rebuilt and shipped.
            p.Status = inBank
                    ?? (loose ? "loose Media wem"
                              : Session?.BankIndexReady == true ? "unknown media id" : "indexing…");
            if (foreign) p.Status += "  (other skin)";

            // What this file is about to overwrite: the shipped subtitle when there is
            // one, the event name otherwise, so an SFX slot is still identifiable.
            var row = rowById.GetValueOrDefault(p.MediaId);
            p.OriginalEvent = row?.Display ?? "";
            p.Original = !string.IsNullOrWhiteSpace(row?.Subtitle) ? row.Subtitle : row?.Display ?? "";
        }
    }

    // ---- the buttons --------------------------------------------------------

    /// <summary>
    /// Stage by filename, whatever the sound list has selected. That is what the pane
    /// means: the toolbar's "Set replacement…" is the route that reads the selection,
    /// and a pane whose button quietly did something else would make the two
    /// indistinguishable from the outside.
    /// </summary>
    private async Task AddPendingFilesAsync()
    {
        // Straight from the decoder, for the reason the toolbar's picker gives: a dialog
        // that offers a format the staging path then rejects, or hides one it accepts,
        // is a lie either way.
        var picked = await PickFiles("Audio to stage  ({media id}-note.wem)", "Audio",
                                     [.. AudioDecode.Extensions.Select(e => "*" + e)]);
        if (picked.Count == 0) return;
        await Stage(picked);
    }

    /// <summary>
    /// Unstage the files picked in the pane. The one per-item way back out, which is
    /// what makes staging onto the wrong id an ordinary mistake rather than a reason to
    /// clear everything and start again.
    /// </summary>
    private void RemovePending()
    {
        var gone = PendingGrid.SelectedItems?.OfType<PendingWem>().ToList() ?? [];
        if (gone.Count == 0) { Say("pick the staged files to take back out first."); return; }

        foreach (var p in gone) _pending.Remove(p);
        MarkStagedRows();
        BuildRedraw();
        // No dash in the sentence: a literal double hyphen is read as two hyphens by anyone
        // looking at the status line, which is where this appears.
        Say($"{gone.Count} unstaged, {_pending.Count} still staged. {StagedScope()}");
    }

    // ---- the help header ----------------------------------------------------

    /// <summary>
    /// Which formats this host takes, asked rather than listed.
    ///
    /// The WPF pane named six. This machine's vgmstream declares twenty, and a machine
    /// where the decoder never unpacked accepts five, so any sentence written down here
    /// would be wrong on most of them. Spelled out in full rather than summarised: the
    /// question someone has while looking at a file is whether THAT extension is in
    /// there, which a count cannot answer.
    /// </summary>
    private static string FormatsSentence()
    {
        var names = AudioDecode.Extensions.Select(e => e.TrimStart('.')).ToList();
        if (names.Count == 0) return "No decoder is available, so nothing can be read here.";

        var list = names.Count == 1 ? names[0]
                 : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

        // Null is this class's public way of saying there is no decoder, which is also
        // what makes the list above the short fallback one. Worth saying: the list being
        // short otherwise reads as this tool not accepting mp3.
        var missing = AudioDecode.DecoderRevision is null
            ? "  That is the short list, because vgmstream did not unpack on this machine."
            : "";

        return $"{list} all work. Anything that is not already a wem is encoded to " +
               "Vorbis on the way in." + missing;
    }

    /// <summary>
    /// Keep the header's state. The compare is what stops this writing the settings file
    /// when Settings is the thing that just moved the expander.
    /// </summary>
    private void RememberDropHelp()
    {
        if (Config.ShowDropHelp == DropHelp.IsExpanded) return;
        Config.ShowDropHelp = DropHelp.IsExpanded;
        Config.Save();
    }

    /// <summary>
    /// Put the header back in step with the setting, for the Settings dialog's switch
    /// and for its reset. A checkbox that says the help is showing, over a pane that is
    /// not showing it until the next launch, is the switch not working.
    /// </summary>
    private void ApplyPendingHelp() => DropHelp.IsExpanded = Config.ShowDropHelp;

    // ---- while a drag is over it --------------------------------------------

    /// <summary>
    /// Light the pane's border, or put it back. The whole border rather than a fill, as
    /// the WPF pane did, so the feedback cannot be mistaken for a row having been hit.
    /// </summary>
    private void PaneLit(bool lit) =>
        DropPane.BorderBrush = lit ? Palette.Accent : Palette.Line;

    /// <summary>Whether the pointer is over the pane. See <see cref="Over"/>.</summary>
    private bool OverThePane(DragEventArgs e) => Over(e, DropPane);
}
