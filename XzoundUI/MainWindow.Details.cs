using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace MRAudioKit.UI;

/// <summary>
/// The right panel's other sub-panel: everything about the selected sound that no column
/// in the list can hold.
///
/// A row knows far more than the list shows. What the .bnk does to the sound, where each
/// of those figures came from, whether the audio ships inside a bank or beside it, which
/// other alternatives the same container picks between, where else a music clip is placed:
/// none of it fits a column, and none of it was reachable anywhere in this window. The old
/// window had two of these as columns with the rest in tooltips, which is how a sentence
/// that answers "why does it sound different in game" ended up living for about a second
/// in a status line the next click overwrote.
///
/// Three rules this file keeps to.
///
/// Every line is traceable. The figures come off the row, the rows the skin was indexed
/// into, the bank's own media directory and the game's loose files, and where the truth is
/// that several placements disagree it says so rather than printing one of them as fact.
/// A section with nothing to say is not drawn, so the panel shrinks instead of showing
/// labels over blanks: on the game's music bank the subtitle, the variation set, the family
/// and the silenced banner are all legitimately absent, and on a voice bank the placements
/// section is.
///
/// It has a SUBJECT of its own, held in <see cref="_detailsAnchor"/> and not read off the
/// list's current row: the boxes it draws write the list's selection, so a panel that read
/// the selection back could not be clicked without rewriting the page it was drawn on.
///
/// It reads the current row only where it has no subject yet, never <see cref="SelectedRows"/>,
/// which materialises a list on every call. And it is coalesced rather than drawn in the
/// handler, because a redraw of the list re-adds every previously selected row one at a time.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// What the panel last drew, so it is not rebuilt for a selection change that cannot
    /// have altered a line of it. The alternative measured badly: one drop onto a large
    /// selection puts the grid through <see cref="BuildRedraw"/>, which re-adds each row
    /// separately, and every one of those is a selection change.
    /// </summary>
    private string _detailsDrawn;

    /// <summary>A redraw is already queued for the end of this dispatcher pass.</summary>
    private bool _detailsQueued;

    /// <summary>
    /// The sound this panel is describing, which is NOT the list's current row.
    ///
    /// The pane had no subject of its own. It read <see cref="MainWindow.CurrentRow"/> and the
    /// list's selection directly, and the boxes drive exactly those, so a box could not be
    /// clicked without rewriting the page it was drawn on. Two defects came out of that one
    /// fact. Ctrl+clicking a second box, which is the gesture the boxes were built for and the
    /// one their own tooltip teaches, took the pane from a sound's answers to a selection's and
    /// threw away everything the reader was reading: what the game does to it, where the audio
    /// is, the chain. And a PLAIN click on a box moved the pane to a different variation set on
    /// 436 of the 1,359 containers across skins 1030001, 1029001, 1034001, 1022001, 1048001 and
    /// 1016001, 32.1 per cent of them, because a box stands for a SLOT of one container while
    /// the row behind it carries whichever container its own medium was met in first: on skin
    /// 1022001, clicking the second box of media 673786398's set redrew container 479715779,
    /// which does not hold 673786398 at all.
    ///
    /// So the subject is held here instead, and the list's selection is left to mean what it
    /// means everywhere else in this window, which is what a build or a drop will act on. A
    /// reader can now ctrl+click their way down a set with the sound's own answers staying
    /// still in front of them.
    ///
    /// It moves when the reader picks a row IN THE LIST, which is a selection change this panel
    /// did not cause that leaves exactly one row selected; when the render harness is given
    /// --pick, by the same route; and when a box is double-clicked, which is the pane's own way
    /// of saying go to this one. It is dropped when nothing is selected, and when its row is
    /// not among the rows on screen, because a subject the list cannot show is not a subject.
    ///
    /// It survives being deselected while other rows are not: ctrl+clicking the subject's own
    /// box takes its row out of the selection and leaves its box unlit, and the pane goes on
    /// describing it. That is the whole point of the two being separate, and the line under the
    /// name says in words that the sound is not one of the selected ones.
    /// </summary>
    private SoundRow _detailsAnchor;

    /// <summary>
    /// This panel moved the list's selection itself, so the subject does not follow it.
    ///
    /// Set by the gestures the pane offers and read once by <see cref="Anchored"/>, rather than
    /// cleared where it is set: the write raises SelectionChanged synchronously and the redraw
    /// it causes is coalesced onto the dispatcher (see <see cref="_detailsQueued"/>), so a flag
    /// reset by the writer would always be false again by the time the draw read it.
    /// </summary>
    private bool _detailsHeld;

    /// <summary>
    /// Which of the panel's dropdowns the reader has left open, named by what each one holds
    /// rather than held as the control, because the control does not survive a redraw: every
    /// draw builds its expanders again, and pressing a box inside the mixer family is a
    /// selection change and therefore a redraw, so the family shut under the reader after
    /// every click and ctrl+clicking down it was impossible.
    ///
    /// Kept when the panel moves to another sound, on the reasoning that a reader who opened
    /// "Where each comes from" for one sound is reading chains and wants the next one's open
    /// too. Shutting a dropdown is what forgets it. See <see cref="Remembered"/>.
    /// </summary>
    private readonly HashSet<string> _detailsOpen = [];

    /// <summary>
    /// How long the last draw took, in milliseconds, which <see cref="DetailsShape"/> reports.
    ///
    /// Kept because the one rule this panel bends for cost is <see cref="InlineBoxes"/>, and a
    /// threshold is a claim about how long something takes: without a figure anyone can
    /// reproduce, the next person to change it has nothing to change it against. Two timestamp
    /// reads against a draw that builds dozens of controls, so it costs nothing measurable on
    /// the path it measures.
    /// </summary>
    private double _detailsMs;

    /// <summary>
    /// How many bytes of a medium the open bank actually carries, which is only knowable by
    /// reading them: the media directory's entry is what the bank ships and the row's own
    /// Bytes is the LOOSE size (SoundIndex fills it from the loose file, or leaves it zero),
    /// so the two numbers the panel puts side by side come from two places. Kept because the
    /// read reopens the bank, and thrown away whenever a different set of rows arrives, so a
    /// mod opened over the same media id cannot be reported at the game's size.
    /// </summary>
    private readonly Dictionary<uint, long> _bankBytes = [];
    private SkinSounds _bankBytesFor;

    /// <summary>
    /// How many boxes a list draws on screen before it goes behind an expander instead.
    ///
    /// ONE number for every list of boxes in this panel, so a reader meets one rule rather
    /// than a different answer per section. It used to be two: a container's alternatives were
    /// always inline and a mixer family always behind a shut expander, which meant the family's
    /// boxes existed and nobody had ever seen them.
    ///
    /// Eight, from two measurements rather than from taste.
    ///
    /// LINES. A container's slots are drawn one to a line, and the largest container across
    /// skins 1030001, 1029001, 1034001, 1022001, 1048001 and 1016001 holds nine of them over
    /// 1,359 distinct sets, so nine lines of boxes is already what this panel spends and what
    /// nobody has complained about. A family's boxes wrap two to a line at the panel's width,
    /// which is 470 px by default and does not narrow at the window's own minimum, so eight of
    /// them is four lines there and eight lines even on a panel dragged narrow enough to take
    /// one box a line. Nothing inline is longer than the longest thing that was already inline,
    /// at any width the panel can be put at.
    ///
    /// MILLISECONDS. --uitest times twenty redraws of a panel holding families of 4, 8, 9 and
    /// 224 and prints each. On this machine a draw costs 3 to 4 ms with the list behind the
    /// expander and 7 to 9 ms with eight boxes on it, so a box is about half a millisecond.
    /// Sixteen was tried first and measured 14.15, 15.97 and 16.38 ms over three runs, which is
    /// at or past one frame at 60 Hz, and this panel is rebuilt once per dispatcher pass for every
    /// selection change a reader or a drop causes (see <see cref="_detailsDrawn"/>). One frame
    /// is where the budget stops, and eight is the round number comfortably inside it. Anyone
    /// changing this can reproduce both figures by changing it and running --uitest.
    ///
    /// Eight covers 214 of the 404 families holding more than one sound across those six skins,
    /// 53.0 per cent, and every one of the 1,359 containers, because none holds more than nine
    /// slots. What no setting of this number does is put most READERS' families on screen:
    /// families are small but rows are not spread evenly over them, and of the 7,438 rows the
    /// default filter shows, the median one is in a family of 98. The share of rows whose
    /// family is small enough to draw runs from 8.3 per cent here to 23.3 per cent at 24, so
    /// the choice is between two small numbers and is settled on lines and milliseconds rather
    /// than on reach.
    /// </summary>
    private const int InlineBoxes = 8;

    /// <summary>
    /// The rows the list is holding, as the panel last read them, so every box it draws can
    /// ask in constant time whether it is one of the selected ones.
    ///
    /// Read once at the top of <see cref="ShowDetails"/> rather than asked of the grid's own
    /// collection per box: that collection is an IList and answers Contains by walking, and
    /// the largest mixer family measured on skin 1030001 draws 224 boxes against a selection
    /// that Select all makes 1,331 rows long. The family's boxes are built later, when its
    /// expander is opened, and read this set as it stood when the panel was drawn; the
    /// selection cannot have moved under them, because a selection change is what rebuilds
    /// the panel.
    /// </summary>
    private readonly HashSet<SoundRow> _detailsPicked = [];

    /// <summary>
    /// The modifiers of the press that is about to become a box's Click, and the control
    /// that press landed on.
    ///
    /// Avalonia puts the modifiers on the pointer event and not on Click, and a box needs
    /// both: Click is what a keyboard reaches and what the headless checks raise, and the
    /// modifiers are what tell a plain click from the ctrl and shift ones the list itself
    /// answers to. The control is kept with them because a press that never becomes a click,
    /// which is what dragging off a button is, would otherwise leave its modifiers behind for
    /// whatever the keyboard pressed next.
    /// </summary>
    private KeyModifiers _boxMods;
    private object _boxPressed;

    /// <summary>
    /// The row the last press on a box was for, so the second press of a double-click can be
    /// told from a press on some other box. A row rather than the control, because the control
    /// the first press landed on is gone by the second: see the handler in
    /// <see cref="MediaBox"/> that reads it.
    /// </summary>
    private SoundRow _boxDown;

    /// <summary>
    /// Where a run selected with shift is measured from: the box a plain or ctrl click last
    /// landed on, named by one of the media it stands for.
    ///
    /// A medium rather than a position, because the panel is rebuilt between the two clicks
    /// and a position would point into whatever list is drawn by then. Where one container
    /// holds the same medium in two of its slots the run is measured from the first of them,
    /// which can only make the run longer, and a run adds rather than replacing.
    /// </summary>
    private uint _boxAnchor;

    /// <summary>
    /// Follow the list. Wired from here rather than from the window's constructor because
    /// this panel is this file's business, and because it is the only subscriber the sound
    /// list has: before this there was no SelectionChanged handler on it at all, so every
    /// cost below is new and is bounded on purpose.
    /// </summary>
    private void HookDetails() => Grid_.SelectionChanged += (_, _) => DetailsChanged();

    /// <summary>
    /// The selection or the rows moved. Queued rather than drawn: see
    /// <see cref="_detailsDrawn"/>.
    ///
    /// Everything that raises this is synchronous, so the whole burst is over before the
    /// dispatcher gets control back and one job answers all of it. Posted at the ordinary
    /// priority rather than a lower one because the headless harness drives the loop by
    /// hand, and a job the harness's RunJobs does not drain is a render of an empty panel.
    /// </summary>
    private void DetailsChanged()
    {
        if (!PaneDetails.IsVisible || _detailsQueued) return;
        _detailsQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _detailsQueued = false;
            ShowDetails();
        }, DispatcherPriority.Normal);
    }

    /// <summary>
    /// Draw the panel for what is selected now.
    /// </summary>
    /// <param name="force">
    /// Redraw even when the fingerprint says nothing moved, which is what the switch
    /// itself needs: the panel is not drawn while it is hidden, so the first thing that
    /// happens on being shown is that it has to catch up.
    /// </param>
    public void ShowDetails(bool force = false)
    {
        if (!PaneDetails.IsVisible && !force) return;

        // The current row off the grid rather than through SelectedRows, and the selection
        // read into the set every box is lit from. One pass over it, and only one per
        // dispatcher pass however many selection events the burst carried, because
        // DetailsChanged coalesces them (see _detailsQueued): the cost the drag hint warns
        // about is a list built per EVENT, and a drop onto four hundred rows raises four
        // hundred of them.
        _detailsPicked.Clear();
        if (Grid_.SelectedItems is { } holding)
            foreach (var held in holding)
                if (held is SoundRow one) _detailsPicked.Add(one);
        var count = _detailsPicked.Count;
        var shown = (Grid_.ItemsSource as IEnumerable<SoundRow>)?.ToList() ?? [];

        // The subject, settled before the fingerprint so the fingerprint can carry it. See
        // _detailsAnchor for the rule; the two calls here are the whole of it.
        Anchored(shown);
        var row = _detailsAnchor ?? CurrentRow;

        if (!ReferenceEquals(_bankBytesFor, Sounds))
        {
            _bankBytes.Clear();
            _bankBytesFor = Sounds;
        }

        // The staged file this row's own "Staged over it" section reads out, by the figures it
        // prints rather than by its presence. Neither the count nor the path can see the two
        // ways those figures move while both stay put: re-exporting a file under the SAME name
        // and staging it again is a different PendingWem at the same path, which is the whole
        // reason ShowPending compares the items themselves and not only their readings; and
        // Status is written in place by PendingStatus, so "indexing…" becomes a bank name under
        // an item that never changed. Without this the panel went on quoting a byte count and a
        // duration belonging to audio that had been replaced.
        var staged = row is null ? null : _pending.FirstOrDefault(p => p.MediaId == row.MediaId);

        // Everything the panel reads that can change without the selection changing: which
        // rows are on screen, how many files are staged, and for one row the two properties
        // the window writes in place after it is built.
        // The ends of both lists as well as their sizes: two different filters can leave the
        // same NUMBER of rows on screen while the answers counted off them differ, and with
        // nothing selected those counts are the whole panel.
        var key = $"{shown.Count}|{shown.FirstOrDefault()?.MediaId}|{shown.LastOrDefault()?.MediaId}|" +
                  $"{_pending.Count}|{count}|{row?.MediaId}|{row?.Bank}|" +
                  $"{row?.EventHash}|{row?.ReplacementPath}|{row?.Muted}|" +
                  $"{staged?.FileName}|{staged?.Bytes}|{staged?.Seconds}|{staged?.Status}|" +
                  // The Ingame button, because the panel states in words which of the two
                  // things Play is about to do. Measured: pressing that button flips the
                  // setting and relabels the toolbar, and the panel went on saying "Play
                  // plays the file as it is" with Ingame on, because nothing about the
                  // selection had moved.
                  $"{Config.PreviewAsInGame}|" +
                  // The subject, because it does not follow from the selection: a box's click
                  // moves the selection and holds the subject, and a double-click moves the
                  // subject while the selection lands where a plain click would have left it.
                  $"{RuntimeHelpers.GetHashCode(_detailsAnchor)}|" +
                  $"{Picked()}";
        if (!force && key == _detailsDrawn) return;
        _detailsDrawn = key;

        var began = Stopwatch.GetTimestamp();
        Details.Children.Clear();

        if (Sounds is null)
            Para("Pick a .bnk or a skin in the tree on the left. This panel then says what " +
                 "the bank does to the sound you select, where its audio is, and what it is " +
                 "one of.", Palette.Muted);
        else if (row is null) ListDetails(shown);
        // Several rows and nothing anchored is the one state that still gets the set's own
        // answers rather than a sound's: with no subject, drawing one row's level would be the
        // pane inviting a reader to take it for the group's, which is what ManyDetails exists
        // to stop. It is reached by selecting several rows without having picked one first,
        // which is Select all, a filter that leaves a selection behind, and the harness.
        else if (_detailsAnchor is null && count > 1) ManyDetails(shown);
        else RowDetails(row, shown);

        _detailsMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;

        // WHICH rows are selected and not only how many, because the boxes are lit from the
        // selection and a box left lit after its row dropped out of it would be the pane
        // disagreeing with the list it is describing. The ends of the selection cannot see
        // that: swapping one row in the middle of three leaves the count and both ends where
        // they were, and this fingerprint is what decides whether the panel is redrawn at
        // all. Added rather than concatenated, so the order the grid hands its selection back
        // in cannot make one set of rows read as two, and on the identity hash because a
        // selection holds rows by identity: two rows of the same medium under events that
        // both hash to nothing are different rows and the panel has to be able to tell them
        // apart.
        string Picked()
        {
            ulong mix = 0;
            foreach (var one in _detailsPicked)
                mix += (ulong)RuntimeHelpers.GetHashCode(one) * 0x9E3779B97F4A7C15UL;
            return mix.ToString("x");
        }
    }

    /// <summary>
    /// Settle which sound the panel is describing. The rule is <see cref="_detailsAnchor"/>'s;
    /// this is the whole of its mechanism.
    ///
    /// Never re-derived from the current row on a path the panel's own gestures reach, which is
    /// the loop this would otherwise be: a box writes the selection, the selection raises the
    /// change this draw is answering, and a subject taken from the row that write left current
    /// would be the subject following the box after all.
    /// </summary>
    private void Anchored(List<SoundRow> shown)
    {
        var held = _detailsHeld;
        _detailsHeld = false;

        // Nothing selected is nothing to describe. This is also how the subject is let go of:
        // ctrl+clicking the only selected row, in the list or on its box, empties the selection
        // and the panel goes back to describing the list.
        if (_detailsPicked.Count == 0) { _detailsAnchor = null; return; }

        // A row the list is not showing cannot be the subject. A filter, another bank or
        // another skin all arrive this way, and each of them would otherwise leave the panel
        // describing a sound that is nowhere on screen.
        if (_detailsAnchor is not null && !shown.Contains(_detailsAnchor)) _detailsAnchor = null;

        if (held && _detailsAnchor is not null) return;

        // Exactly one row, and this panel did not put it there, so the reader picked it in the
        // list or with the keyboard. Several at once leaves the subject alone: a ctrl+click in
        // the LIST is a reader adding to a set, not changing what they are reading, and it is
        // the same act as the ctrl+click on a box two lines above.
        if (_detailsPicked.Count == 1) _detailsAnchor = CurrentRow ?? _detailsPicked.First();
    }

    // ---- one row ------------------------------------------------------------

    private void RowDetails(SoundRow row, List<SoundRow> shown)
    {
        // Which row this is, always, and first. At the default window width the list draws
        // Mod, Event, Media ID and Category and the subtitle column is already past the
        // right edge, so a reader who has scrolled the grid sideways cannot otherwise see
        // which row the panel is describing.
        Para(row.Display, bold: true, size: 13.5);
        Para($"media {row.MediaId} · {row.Bank} · {row.Kind}", Palette.Muted);

        if (row.Subtitle.Length > 0) Para(row.Subtitle, top: 6);
        AlsoSelected(row);

        // The rows on screen as a set, built once here and handed to both of the sections
        // that draw a box per medium. Each box has to say whether it can reach its own row,
        // and asking that of the list itself would be a scan of the whole list per box: the
        // largest mixer family measured on skin 1030001 holds 224 media, so that is 224
        // scans of 1,331 rows on a path this file deliberately keeps cheap.
        var onScreen = shown.ToHashSet();

        Applied(row);
        WhereTheAudioIs(row);
        Alternatives(row, onScreen);
        Placements(row);
        Belongs(row, shown, onScreen);
        Staged(row);
    }

    /// <summary>
    /// What else the list is holding, in two lines under the name of the sound being described.
    ///
    /// The panel used to answer a selection INSTEAD of a sound, and a reader ctrl+clicking down
    /// a set of alternatives lost the page they were reading on the second click. It answers
    /// the sound now (see <see cref="_detailsAnchor"/>), so what the selection's own answers
    /// knew has to survive somewhere: how many rows a build or a drop is about to act on, and
    /// whether any of them are silent or already staged. The rest of what that view said is
    /// either the sound's own, and above, or the set's, and printed beside the set's boxes.
    ///
    /// Whether the sound is one of the selected rows is said in words, because it need not be:
    /// ctrl+clicking its own box takes its row out of the selection and leaves the panel
    /// describing it, and its box drawn unlit is the other half of that sentence.
    /// </summary>
    private void AlsoSelected(SoundRow row)
    {
        if (_detailsPicked.Count <= 1) return;
        var rows = _detailsPicked.ToList();
        var media = rows.Select(r => r.MediaId).Distinct().Count();
        var silent = rows.Count(r => r.Applied?.Silenced == true);
        var staged = rows.Count(r => r.ReplacementPath is not null);
        Para($"{rows.Count:N0} rows selected over {media:N0} distinct media" +
             (silent > 0 ? $", {silent:N0} of them silent" : "") +
             (staged > 0 ? $", {staged:N0} already staged" : "") +
             (_detailsPicked.Contains(row) ? "." : ", and this sound is not one of them."),
             Palette.Muted, top: 6);
        Para("A file dropped here replaces every one of them.", Palette.Muted);
    }

    /// <summary>
    /// What the .bnk does on top of the audio, as labelled lines rather than one run-on,
    /// with the chain it was added up from underneath and shut.
    /// </summary>
    private void Applied(SoundRow row)
    {
        Head("What the game does to it");
        if (row.Applied is not { } a)
        {
            Para("Not read for this .bnk.", Palette.Muted);
            return;
        }

        // The same arithmetic the row's own column does (WwiseProps.Applied.Summary), split
        // into labelled lines. Each is left out at its default rather than printed as
        // "unchanged": a panel of defaults is a panel nobody reads twice, and the fallback
        // below is what says the difference between nothing set and nothing looked at.
        var any = false;
        if (a.Silenced)
        {
            Pair("Level", $"silent, {a.StaticDb:0.#} dB", Palette.TagRed);
            any = true;
        }
        else if (Math.Abs(a.StaticDb) > 0.001)
        {
            Pair("Level", $"{a.StaticDb:+0.#;-0.#} dB");
            any = true;
        }

        var cents = a.Props.Where(p => !p.Driven && p.Unit == WwiseProps.Unit.Cents).Sum(p => p.Float);
        if (Math.Abs(cents) > 0.001) { Pair("Pitch", $"{cents:+0.#;-0.#} cents"); any = true; }

        var speed = a.Props.Where(p => p.Unit == WwiseProps.Unit.Multiplier)
                           .Aggregate(1f, (all, p) => all * p.Float);
        if (Math.Abs(speed - 1) > 0.001) { Pair("Speed", $"{speed:0.##}x"); any = true; }

        var delay = a.Props.Where(p => p.Id is 0x0F or 0x3B).Sum(p => p.Float) + a.ActionDelay;
        if (delay > 0.0005) { Pair("Starts late by", $"{delay:0.###} s"); any = true; }

        if (a.LoopCount == 0) { Pair("Repeats", "for ever"); any = true; }
        else if (a.LoopCount > 1) { Pair("Repeats", $"{a.LoopCount} times"); any = true; }

        if (a.ClipSeconds is > 0)
        {
            Pair("Clip length", $"{a.ClipSeconds:0.###} s" + (a.ClipTrimmed ? ", trimmed" : ""));
            any = true;

            // Both figures are stated by the bank, so which is bigger is arithmetic rather
            // than a guess, and it is the thing a replacement runs into: the segment is
            // what the clip plays inside.
            //
            // The segment is a value like every other figure here, so it is a Pair; only the
            // MISMATCH is prose, because only the mismatch changes what the reader does. The
            // agreeing case used to spend a wrapped sentence saying the segment "is the clip's
            // own length, so the segment is no longer than the audio it was built around",
            // which is the same number said three ways.
            if (a.SegmentSeconds is { } seg && a.ClipSeconds is { } clip)
            {
                Pair("Segment", $"{seg:0.###} s");
                var gap = Math.Abs(seg - clip);
                if (gap > MusicIndex.Tolerance)
                    Para(seg > clip
                            ? $"The segment runs {gap:0.###} s past the end of this clip."
                            : $"{gap:0.###} s of this clip is past the segment's end.",
                         Palette.Muted, top: 2);
            }
        }

        if (!any)
        {
            // The level settings StaticDb adds up, asked for separately because "none that
            // reach the level" was false on the rows where they reach it and come to nothing:
            // vo_10300029_q_p13_play is -2 dB on the sound against -1 and +3 dB of make-up
            // gain on two mixers above it, and sfx_1030001_ftp_play stacks +1, +2, -9 and +6.
            // 8 rows of skin 1030001 and 9 of 1029001 land here with level settings in the
            // chain, and every one of those carries a value that is not 0 dB, which is why
            // the branch below can say they cancel rather than that they are all zero.
            var levels = a.Props.Where(p => !p.Driven && p.Unit == WwiseProps.Unit.Db &&
                                            p.Id is 0x00 or 0x06).ToList();
            Para(levels.Any(p => Math.Abs(p.Float) > 0.001)
                    ? $"Nothing you can hear. The bank sets the level {levels.Count} times and " +
                      "they cancel to 0 dB."
                 : a.Props.Count > 0
                    ? "Nothing you can hear. Nothing it sets reaches the level, the pitch, " +
                      "the timing or the loop."
                    : "Nothing. It plays as the file is.", Palette.Muted);
        }

        // The caveats, under the numbers they qualify rather than in a trailing sentence,
        // because a level a curve raises from its stored minimum reads as "they muted it".
        if (a.DrivenProps.Any(p => p.Unit == WwiseProps.Unit.Db))
            Para("A game parameter drives the level, so that is only where it starts.",
                 Palette.TagRed, top: 4);
        if (a.StateGroups > 0)
            Para($"{a.StateGroups} state group(s) can change it, and the bank cannot say " +
                 "which one is on.", Palette.Muted, top: 2);

        if (a.Silenced)
        {
            // The banner keeps its weight and loses its capitals, like the headings above:
            // bold red on a panel with no other red bold line is already the loudest thing on
            // screen, and this is the one line nobody may skim past.
            //
            // What went with the capitals was a paragraph of reasoning underneath. It named
            // the 60 dB this panel actually treats as silent (WwiseProps.Applied.Silenced),
            // worked out that sixty decibels down is a thousandth of the authored amplitude,
            // and concluded that a cut that deep is deliberate. Every word of it was true and
            // none of it changed what the reader does next: the cut itself is already on the
            // Level line above, and what they act on is whether anything can raise it, which
            // is the line below.
            Para("This sound is silent.", Palette.TagRed, bold: true, top: 8);

            // Whether anything could raise it again is asked of this row rather than
            // asserted. The sentence this replaces said "no game parameter, no state" on
            // every silenced row, which contradicts the two lines above it on a row that
            // has either, and those are the rows where it would matter most.
            //
            // Asked of LevelDriven and not of DrivenProps, which marks a property the object
            // stores and so misses a curve on a level the object leaves at its default:
            // sfx_1029001_right_trajectory_play at -102 dB carries a Volume curve on the 4th
            // actor-mixer above it, printed by the chain below, and was still told that
            // nothing raises it and that replacing the audio changes nothing anybody will
            // hear. 6 rows of skin 1029001 and 1 of 1034001 were given that instruction.
            Para(a.LevelDriven && a.StateGroups > 0
                    ? "A game parameter or a state group could still raise it."
                 : a.LevelDriven
                    ? "A game parameter drives its level, so it could still be raised."
                 : a.StateGroups > 0
                    ? "A state group could still raise it."
                    : "Nothing raises it again.", Palette.TagRed);
            Para(a.LevelDriven || a.StateGroups > 0
                    ? "Short of that, replacing the audio changes nothing anybody hears."
                    : "Replacing the audio changes nothing anybody hears.", Palette.TagRed);
        }

        // The answer to the question this window is asked most: why it sounds different in
        // game. Both halves already exist and are already tested; what they did not have
        // was anywhere to be read.
        var missing = InGameChain.Unapplied(a);
        if (missing.Length > 0)
            Para($"Playing it here cannot reproduce {missing}.", Palette.Muted, top: 8);
        Para(Config.PreviewAsInGame
                ? "Ingame is on, so Play applies what the bank does."
                : "Play plays the file as it is. Ingame applies what the bank does.",
             Palette.Muted, top: missing.Length > 0 ? 2 : 6);

        Chain(a);
    }

    /// <summary>
    /// The inherited chain, shut, and grouped by property name when opened.
    ///
    /// Flat, which is how the tooltip in the old window showed it, it is nearly unreadable:
    /// 1,276 of one skin's 1,331 rows name the same property at more than one level, and the
    /// worst of them repeats Game aux send seven times, Attenuation four, Priority three and
    /// Make-up gain twice, which is sixteen lines in no order. Grouped, that row is four
    /// headings.
    ///
    /// A group is only SUMMED where the code that reads the bank already sums: volume and
    /// make-up gain in dB, undriven, which is what <see cref="WwiseProps.Applied.StaticDb"/>
    /// adds up. Wwise does not add up seven aux sends, so neither does this: the others show
    /// their contributions and no total, because a total nobody computes is a number this
    /// panel would have invented.
    /// </summary>
    private void Chain(WwiseProps.Applied a)
    {
        if (a.Props.Count == 0) return;

        var body = new StackPanel();
        foreach (var group in a.Props.GroupBy(p => p.Name))
        {
            var parts = group.ToList();
            var summable = parts.All(p => !p.Driven && p.Unit == WwiseProps.Unit.Db &&
                                          p.Id is 0x00 or 0x06);
            var heading = parts.Count == 1 ? $"{group.Key}: {parts[0].Text}"
                        : summable ? $"{group.Key}: {parts.Sum(p => p.Float):+0.##;-0.##;0} dB " +
                                     $"over {parts.Count} levels"
                        : $"{group.Key}: {parts.Count} values the bank does not add up";
            body.Children.Add(new TextBlock
            {
                Text = heading,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Palette.Fg,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 1),
            });
            // One level named on its own line under the heading, which for a single
            // contribution is where it came from and for several is the value as well.
            // Where is what keeps the heading's promise: it carries an ordinal counted out
            // from the sound (WwiseProps.cs:608), so the seven actor-mixers a chain can hold
            // read as seven different lines instead of seven copies of "on the actor-mixer".
            // Measured after that landed: no group on skin 1030001 or 1029001 prints the same
            // value against the same level twice.
            foreach (var p in parts)
                body.Children.Add(new TextBlock
                {
                    Text = parts.Count == 1 ? p.Where : $"{p.Text}   {p.Where}",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Palette.Muted,
                    FontSize = 12,
                    Margin = new Thickness(12, 0, 0, 0),
                });
        }

        if (a.Rtpc > 0)
        {
            // Rtpc counts CURVES, and the list names the setting each one aims at, which is a
            // different number: 10 objects across skin 1030001's banks aim two curves at the
            // same setting, so 188 of its 1,271 curve-carrying rows stated a figure and then
            // named fewer things than that. Both numbers are given where they differ, rather
            // than padding the list with an entry a reader cannot tell from the one above it.
            var aimed = a.Live.Distinct().ToList();
            body.Children.Add(new TextBlock
            {
                Text = aimed.Count == 0
                        ? $"{a.Rtpc} game-parameter curve(s) change this while it plays."
                     : aimed.Count == a.Rtpc
                        ? $"{a.Rtpc} game-parameter curve(s) change this while it plays: " +
                          $"{string.Join(", ", aimed)}."
                        : $"{a.Rtpc} game-parameter curve(s) change this while it plays, " +
                          $"aimed at {aimed.Count} settings: {string.Join(", ", aimed)}.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Palette.Muted,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        Details.Children.Add(Remembered("chain", new Expander
        {
            Header = $"Where each comes from ({a.Props.Count})",
            FontSize = 12,
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            // Shut until a reader opens it (see _detailsOpen), whatever its size, and this is
            // the one list here that is not put through
            // BoxList's threshold. It is not a list of boxes and not a list of things: it is
            // the PROVENANCE of the numbers above it, which is exactly the kind of prose the
            // pane has just been shortened by cutting, and it is what somebody reads once they
            // disbelieve a figure rather than on the way past. It is also never short: the
            // median row across six skins carries 9 property lines and the worst 24, so an
            // inline rule at any threshold worth having would open it for about half the rows
            // in the game and undo the shortening on those.
            Content = body,
        }));
    }

    /// <summary>
    /// The two copies of one sound, and what a build does to them.
    ///
    /// The strongest thing in this panel and the one no column can carry, because the two
    /// numbers have to sit side by side to mean anything. On one skin, 456 of 1,331 rows
    /// have BOTH a bank copy and a loose file, and in every one of those 456 the bank copy
    /// was a fraction of the loose one. The bank's streaming flag does not identify them,
    /// which is measured rather than assumed: it was false on all 456. So the sizes are
    /// compared here rather than a rule about them being written down, and the flag itself is
    /// not printed, because what it tells a reader to do is nothing.
    /// </summary>
    private void WhereTheAudioIs(SoundRow row)
    {
        Head("Where the audio is");

        var bank = Sounds.BankOfMedia.GetValueOrDefault(row.MediaId);
        var inBank = bank is not null ? BankBytes(row.MediaId) : 0;
        var loose = Session?.LooseMedia(row.MediaId);
        var modLoose = Mods.LooseFor(row.MediaId, Session?.Language) is { Changed: true } lo ? lo : null;

        if (bank is not null)
            Pair("In the bank", inBank > 0 ? $"{bank}, {inBank:N0} B" : bank);
        if (modLoose is not null)
        {
            Pair("Mod's loose file", $"{modLoose.Bytes.Length:N0} B, from {modLoose.SourceName}");
            Para(modLoose.MountedAt, Palette.Muted, size: 11.5);
        }
        if (loose is not null)
        {
            Pair(modLoose is null ? "Loose file" : "Game's loose file", $"{loose.Size:N0} B");
            Para(loose.Path, Palette.Muted, size: 11.5);
        }
        if (bank is null && loose is null && modLoose is null)
        {
            Para("No bank copy and no loose file. It can still be staged, and the build says " +
                 "where it goes.", Palette.Muted);
            return;
        }

        if (Sounds.Raw.ContainsKey(row.MediaId))
            Para("These bytes are the opened mod's, not the game's.", Palette.TagPurple, top: 2);

        // Two sizes compared, and nothing claimed beyond them. That the smaller one is the
        // opening of the larger is checked elsewhere and on one medium at a time, so it is
        // not something to state on every row; that the bank does not hold the whole sound
        // follows from the numbers themselves.
        //
        // What used to follow was the streaming flag, said either way on every row of this
        // shape. It was there because it is the flag somebody would reach for and it was
        // measured false on all 456 of them, which is a finding about this tool rather than
        // about the sound: a reader is told below what a build does, and the flag changes
        // none of it.
        if (bank is not null && loose is not null && inBank > 0 && inBank < loose.Size)
            Para($"The bank holds {(double)inBank / loose.Size:P1} of it. The loose file is " +
                 "the whole sound.", Palette.Muted, top: 4);

        // What a swap will actually do, which is the build's behaviour and not a guess: the
        // window builds with prefetch mode off, so the bank entry grows to the whole file
        // and nothing is written under Media/ for an id a bank already holds.
        //
        // The bank is never named again here. It is on the "In the bank" line four pixels
        // above, and printing it twice was the clearest case in the pane of a sentence spent
        // restating what the reader had just read.
        Para(bank is null
                ? "A build ships your file as a loose wem under WwiseAudio/Media."
             : loose is null
                ? "A build rewrites this entry and ships that one file."
                : "A build grows this entry to your whole file and leaves the loose copy " +
                  "alone.", top: 6);

        // Derived from what the bank names the source against what a staged file is encoded
        // to, rather than from a list of plugin names: a source the bank does not play from
        // a Vorbis wem is one where a replacement is not the same kind of thing.
        if (!string.IsNullOrEmpty(row.Codec) &&
            !row.Codec.Contains("VORBIS", StringComparison.OrdinalIgnoreCase))
            Para($"This source is {row.Codec}, not Wwise Vorbis. Anything staged is encoded " +
                 "to Vorbis, so a replacement here may not play.", Palette.TagRed, top: 4);
    }

    /// <summary>
    /// The container's other alternatives, in full, with the odds on the boxes.
    ///
    /// The fact that decides whether replacing one file will look like the mod barely works:
    /// 759 of one skin's 1,331 rows are one of several, and a line heard one time in five is
    /// heard one time in five however good the replacement is. No set over the 1,359 measured
    /// across six skins is longer than nine, so every one of them is drawn whole and none of
    /// them ever reaches <see cref="InlineBoxes"/>, and an entry the list
    /// has a row for is a box that plays it and selects it, because the reader's next act is
    /// to hear the others and then go and stage them. Selecting them is the whole set at once
    /// where that is what they are after, since the boxes take the list's own ctrl and shift
    /// clicks: staging a set one member at a time is the trip this panel exists to save.
    ///
    /// Whole means the CONTAINER's members. The list advertises itself as the set, and a
    /// modder replacing the alternatives off it would leave out whatever it failed to draw
    /// and go on hearing the old sound some of the time, so anything short of the container
    /// is worse here than no list at all.
    /// </summary>
    private void Alternatives(SoundRow row, HashSet<SoundRow> onScreen)
    {
        if (row.Applied?.Alternative is not { Count: > 1 } v) return;

        Head("One of several");
        Para(v.Mode == "sequence"
                ? $"Number {v.Index + 1} of {v.Count}, in a fixed order."
                : $"Number {v.Index + 1} of {v.Count}, picked at random.");
        // The one fact here that a reader acts on, and the only one the boxes cannot show.
        //
        // Three lines went for the boxes to carry instead. This one's share was printed in
        // words and is on its own box; that the share came from the container's weights, or
        // that it is an even split assumed, is carried by every box, because a slot's Share is
        // worded by the set itself (WwiseProps.cs:728) and reads "~33 % (even split assumed)"
        // where the container states no playlist; that the bank weights this one differently
        // from its siblings is the boxes' own figures disagreeing, which is visible without
        // being said; and in a sequence there are no shares at all, so "the order is fixed, so
        // position is what decides when it is heard" said the line above it again.
        Para("Replacing this one leaves the others as they are.", Palette.Muted);
        AlternativeBoxes(v, row, onScreen);
        // Under the boxes it is counted from, and only where there is a selection to count.
        // This is the half of the old several-selected view that a reader is building as they
        // ctrl+click down the boxes, so it belongs where they are clicking.
        if (_detailsPicked.Count > 1) SetCoverage(v, row.Bank, _detailsPicked.ToList());
    }

    /// <summary>
    /// One box per slot of the container, in the container's own order.
    ///
    /// Drawn in both of the panel's selected states, and that is what makes the boxes a
    /// selection surface rather than a row of labels. Ctrl+clicking a second box takes the
    /// list from one row selected to two, which takes the panel from
    /// <see cref="RowDetails"/> to <see cref="ManyDetails"/>, and a list of boxes that
    /// disappeared at that point could not be ctrl+clicked down. The boxes move once when
    /// that happens, because the panel's answers for a selection are not the same lines as
    /// its answers for a row, and they stay put for every click after it.
    /// </summary>
    /// <param name="v">
    /// The container, which is the one <paramref name="here"/> names. Its slots and not
    /// whatever rows carry its group id: those two are not the same set. The index keeps one
    /// row per bank, event and medium, and this game's banks build one set of media into many
    /// containers through Sound objects of their own each time, so the rows scatter across
    /// whichever container each medium was met in first. Container 183834482 of
    /// bnk_sfx_1022001 holds media 673786398, 53978063 and 634398524, and gathering rows on
    /// its group id finds only the first, the other two naming container 479715779. That drew
    /// ONE box for a set of three and then a sentence saying the bank states 3 and one is
    /// open, which was false about the list a modder replaces a set from. Counted by
    /// --variations, which walks skins 1022001, 1034001 and 1030001 and reads each container
    /// out of the .bnk: 32 sets over those three, 18 of them in bnk_sfx_1022001 alone.
    /// </param>
    /// <param name="here">
    /// The row the panel is anchored on, which is the box a plain click landed on. One box
    /// says so in words, because the figures in the rest of the panel are that row's.
    /// </param>
    /// <param name="onScreen">The rows the list is showing, which decides what a box can select.</param>
    private void AlternativeBoxes(WwiseProps.Variation v, SoundRow here,
                                  HashSet<SoundRow> onScreen)
    {
        // Every row in THIS bank for the media each slot reaches. The bank is the anchor
        // row's, because Applied was read from that bank's own hierarchy, so the container
        // and its children are objects in it and nowhere else.
        //
        // Gathered before anything is drawn, because a box has to know its siblings: shift
        // selects the run from the last box clicked to this one, and the run is measured in
        // slots.
        var slots = v.Members
            .Select(slot => (Slot: slot, Rows: slot.Media
                .SelectMany(m => Sounds.Rows.Where(r => r.MediaId == m && r.Bank == here.Bank))
                .Distinct().ToList()))
            .ToList();
        var group = slots.Select(s => s.Rows).ToList();

        // Named by media id rather than by event, because only 65 per cent of members carry
        // a name at all and 192 of 193 sets on that skin have every member under one event
        // name, so naming them by event would print the same string down the list.
        //
        // Reading the container also settles what used to need collapsing. A slot held one
        // medium and several rows, and the collapse was justified on "no slot holds more than
        // one medium", which skin 1034001 falsifies: container 369457676 of bnk_sfx_1034001
        // gives each of its three slots a layer container of two sounds, and those three are
        // the only ones of the 591 slots that bank's rows fall into to carry two media. There
        // is no collapse now, because the slot is the unit the bank states and its media
        // travel with it.
        //
        // Drawn into a column of its own and handed to the one rule every list of boxes in
        // this panel goes through. It is inline on every container this game ships: no set of
        // the 1,359 measured across six skins holds more than 9 slots, so the expander branch
        // is what a bank with forty alternatives would get rather than something a reader
        // meets here. See InlineBoxes.
        var column = new StackPanel();
        BoxList("set", slots.Count, $"All {slots.Count} of them, one box each", column, () =>
        {
            for (var at = 0; at < slots.Count; at++)
            {
                var (slot, rows) = slots[at];

                // The medium the reader came from, then one the list is showing, then any. A
                // medium two events reach is two rows, and picking a hidden copy would draw a
                // box answering "none of those are in the list as it is filtered now" while
                // its twin sat on screen. That does not happen on any bank measurable here:
                // over the 4,199 member media of skins 1030001, 1029001, 1034001, 1022001 and
                // 1048001 the first row is never the hidden one while another is shown, so the
                // middle clause is a no-op on all five. It stays because the label is the
                // slot's and the reach is the row's, so the cost of getting it wrong falls on
                // a reader and not on a number. Nothing the box SAYS depends on which copy is
                // picked.
                var mine = slot.Index == v.Index && slot.Media.Contains(here.MediaId);
                var lead = rows.FirstOrDefault(r => ReferenceEquals(r, here))
                        ?? rows.FirstOrDefault(onScreen.Contains)
                        ?? rows.FirstOrDefault();

                // Worded exactly as the line this box replaces was, because the string is what
                // says which alternative this is and what it costs to leave the others alone.
                // A slot reaching several media has no one of them to name, so it says how
                // many it holds instead of naming the first and passing that off as the
                // alternative.
                //
                // "A group of" and not "played together", because the slot's child can be
                // either kind and the two are opposites: bnk_sfx_1034001's container 369457676
                // gives its slots layer containers, whose sounds do play together, and
                // bnk_vo_1022001's container 34402701 gives two of its four a seven-way random
                // container, whose sounds are alternatives of each other. Which it is belongs
                // on those members' own rows, where each states the container it sits in
                // directly.
                var what = slot.Media.Count == 1 ? $"media {slot.Media[0]}"
                         : slot.Media.Count == 0 ? "no media in this bank"
                         : $"a group of {slot.Media.Count}: " + string.Join(", ", slot.Media);
                var text = $"{slot.Index + 1}. {what}" +
                           (slot.Share.Length > 0 ? $"   {slot.Share}" : "") +
                           (rows.Any(r => r.ReplacementPath is not null) ? "   staged" : "") +
                           (mine ? "   this one" : "");

                // A member with no row at all is a line and not a box, because neither half of
                // a box could act on it: Play needs a row to read the chain off and the list
                // has nothing to select. Saying so is still the point, since the reader is
                // replacing a set and this is one of it.
                column.Children.Add(lead is null
                    ? Line($"{text}   no row in this list", Palette.Muted, top: 2)
                    : MediaBox(lead, text, onScreen, group, at));
            }
        });
    }

    /// <summary>
    /// Where else the bank places the same music clip, and whether those places agree.
    ///
    /// A music row is one row per medium and its numbers are the FIRST place the bank puts
    /// it, because something has to be shown. On the game's music bank 347 of 655 media are
    /// placed more than once, up to eighteen times, and 141 of them have a placement
    /// stating a different level or segment: printing the row's level with nothing else to
    /// say would be over-confident about better than a fifth of that bank. Collapsed by the
    /// line each one prints, which is what makes it affordable: at worst ten lines, and two
    /// for most of them.
    /// </summary>
    private void Placements(SoundRow row)
    {
        if (row.Placements.Count <= 1) return;

        // Grouped on the SENTENCE and not on the raw figures. The level is printed to one
        // decimal and the segment to three, and music_formal stores two of media 173639734's
        // segments as 24000 and 23999.999999999993 ms, so grouping on the doubles made two
        // groups whose lines came out character for character identical: the reader saw
        // "-7 dB, segment 24 s, trimmed, in 3 of them" twice with nothing to tell them
        // apart. Grouping on what is shown also makes the count below agree with the list
        // by construction, which the separate PlacementsDiffer test could not promise.
        var groups = row.Placements
            .GroupBy(Sentence)
            .OrderByDescending(g => g.Count())
            .ToList();

        Head("Placed elsewhere");
        // "Places" and not "tracks": a placement is one source occurrence, and seven of this
        // bank's tracks carry the same medium more than once, which had media 679409166
        // reading as three tracks when it is one track placing the clip three times.
        if (groups.Count == 1)
        {
            Para($"Placed {row.Placements.Count} times, every one at the same level and " +
                 "segment.", Palette.Muted);
            return;
        }

        Para($"The numbers above are one of {row.Placements.Count} placements, and they do " +
             "not all agree.", Palette.TagRed);
        Para("A swap reaches all of them.", Palette.Muted, top: 2);

        foreach (var group in groups)
        {
            var shown = group.Any(p => p.Shown);
            Para($"{group.Key}   in {group.Count()}" + (shown ? ", the one above" : ""),
                 shown ? Palette.Fg : Palette.Muted, top: 2);
        }

        // One placement in the words the panel puts it in, so the grouping above and the
        // line below cannot drift apart.
        static string Sentence(MusicPlacement p)
        {
            var level = Math.Abs(p.Db) > 0.001 ? $"{p.Db:+0.#;-0.#} dB" : "0 dB";
            if (p.Driven) level += ", driven";
            var segment = p.SegmentSeconds is { } s ? $"segment {s:0.###} s" : "no segment length";
            return $"{level} · {segment}{(p.Trimmed ? " · trimmed" : "")}";
        }
    }

    /// <summary>
    /// What this sound belongs with: one line for the mixing family, one for the other
    /// events that reach it, one for the other rows that are the same media id.
    ///
    /// The family is a line and then a list. 1,321 of one skin's 1,331 rows are in a family of
    /// more than one, so the line is present almost always, and 1,140 of them are in a family
    /// larger than the twelve the old tooltip printed, so the list it replaces was truncated
    /// for most of it anyway. Where the family is small enough the members are boxes under the
    /// line, and where it is not they are behind an expander that states how many there are
    /// before it is opened, which is the one thing a truncated list cannot do. Both come out of
    /// <see cref="BoxList"/>, which is where the threshold and its measurements are.
    /// </summary>
    private void Belongs(SoundRow row, List<SoundRow> shown, HashSet<SoundRow> onScreen)
    {
        var family = row.Applied?.FamilyId ?? 0;
        var sameId = shown.Where(r => r.MediaId == row.MediaId && !ReferenceEquals(r, row)).ToList();

        // The guard is exactly the three conditions the three sections below are drawn
        // under, and a family id is not one of them. SoundIndex leaves FamilyMembers empty
        // for a family whose only member is this row (SoundIndex.cs:472), so a row with an
        // id and no siblings used to clear the old guard on the id alone and draw the
        // heading over nothing at all: 7 rows of skin 1030001, 8 of 1029001 and 4 of
        // 1034001 are in a family of one, named by no second event and sharing their media
        // id with no other row.
        if (row.FamilyMembers.Count <= 1 &&
            row.AlsoPlayedBy.Count == 0 && sameId.Count == 0) return;

        Head("Grouped with");

        if (row.FamilyMembers.Count > 1)
        {
            // Sounds, counted by distinct media id, and not rows. FamilyMembers holds one
            // entry per ROW (SoundIndex.cs:479) and a sound two events reach is two rows, so
            // the count printed here ran ahead of the mixer's contents on 329 of skin
            // 1030001's 1,331 rows and 854 of 1029001's 1,819: sfx_1030001_e_child_drain_play
            // read "one of 9" for a mixer holding 6, and media 472982720's mixer holds one
            // sound and was printed as 2. The selection below still counts rows, because a
            // selection holds rows, so where the two figures differ that line says which of
            // them it is counting rather than leaving the reader with a contradiction.
            //
            // One row per medium is kept rather than only the count, because the expander
            // below draws a box per medium and the number in this sentence has to be the
            // number of boxes it draws. Counting twice is how those two drift. The row kept
            // for a medium is the current one where it is that medium, then one the list is
            // showing, then any: a medium two events reach is two rows and they need not be
            // equally reachable, since 155 of 321 families measured across skins 1030001,
            // 1029001, 1034001, 1022001 and 1048001 hold a member the default row filter
            // hides. On those five skins the middle clause never changes the pick, because
            // in none of their 6,770 family media groups is the first row hidden while
            // another copy is shown; it is kept because which copy is picked is a reader's
            // reach and not a count, and the fallback below would be a silent wrong answer
            // on a bank where the two differ rather than a visible one.
            var perMedium = Sounds.Rows.Where(r => (r.Applied?.FamilyId ?? 0) == family)
                                       .GroupBy(r => r.MediaId)
                                       .Select(m => m.FirstOrDefault(r => ReferenceEquals(r, row))
                                                 ?? m.FirstOrDefault(onScreen.Contains)
                                                 ?? m.First())
                                       .ToList();
            var sounds = perMedium.Count;

            // The mixer's own child count, which is the one thing the bank states about what
            // it holds, against the number counted off the rows. The index keeps a single row
            // per medium, so a medium sitting under two mixers is attributed to whichever it
            // was met on first and the other mixer loses it: mixer 34047687 of skin 1030001
            // lists 147 Sound children and 10 of them reach a row, and the line read "one of
            // 10 sounds that share a mixer" and offered to select all 10. 7 of that skin's 57
            // families and 15 of 1029001's 76 are short of the mixer's own list. Where the
            // mixer's children are containers the count runs the other way and is right, so
            // this only fires where the bank contradicts it.
            var listed = row.Applied?.FamilySize ?? 0;
            var shortOfIt = sounds < listed;
            // The mixer in the pane's own label-and-value shape rather than as a sentence about
            // it. What went was the clause saying a mixer is a mixing group and not an order:
            // the boxes below carry no numbers, so there is no order on screen to deny, and
            // the sentence was answering a question the panel had not raised.
            Pair("Mixer", row.Family);
            Para(shortOfIt
                    ? $"{sounds} of its {listed} children are rows here, so this is not the " +
                      "whole of it."
                 : sounds > 1
                    ? $"One of {sounds} sounds on it."
                    : $"The only sound on it. The other {row.FamilyMembers.Count - 1} row(s) " +
                      "are this same audio under another event.", Palette.Muted);
            SelectLine(!shortOfIt && sounds == row.FamilyMembers.Count
                           ? $"Select all {row.FamilyMembers.Count} of them"
                           : $"Select the {row.FamilyMembers.Count} rows in that group",
                       Sounds.Rows.Where(r => (r.Applied?.FamilyId ?? 0) == family));

            // A box each, through the one rule a list of boxes goes through here. This used to
            // be an expander whatever the family held, and that is the reason nobody had ever
            // seen these boxes: the expander was put there for the 224-media case, where the
            // largest measured holds 224 distinct media on skin 1030001 and 244 on 1022001,
            // and then applied to the family of three as well. A reader who had just been
            // shown boxes for a set of four met a shut header for a mixer of four.
            //
            // Wrapped rather than a column, which is the one thing that differs from the set
            // above: a family box carries a media id and nothing to line up, so it costs half
            // the lines side by side. See InlineBoxes for where the threshold comes from.
            if (sounds > 1)
            {
                var wrapped = new WrapPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                // The count the sentence above just printed, from the same list the boxes are
                // drawn out of, and worded to match which of the two things it is: a mixer the
                // list is short of does not hold "all" of anything.
                BoxList("family", sounds, shortOfIt ? $"The {sounds} this list has, one box each"
                                                    : $"All {sounds} of them, one box each",
                        wrapped, () =>
                {
                    // One row per box here, unlike a container's slot, but the boxes are
                    // still handed the whole group: shift selects the run from the box last
                    // clicked to this one, and the run is measured in the order they are
                    // drawn in.
                    var group = perMedium.Select(m => new List<SoundRow> { m }).ToList();
                    for (var at = 0; at < perMedium.Count; at++)
                    {
                        var member = perMedium[at];
                        var here = ReferenceEquals(member, row);
                        // The media id and nothing else. There is no number to give a family
                        // box, because a mixer states no order for the sounds on it. Nor the
                        // event name, which distinguishes the members of a family in 162 of
                        // 412 measured cases and runs 224 media under 11 names on the largest
                        // of them, so as a label it would be the same string down the wrap. It
                        // is on the box's tooltip instead, where it costs no width, and in the
                        // list's own Event column for the row it selects.
                        var text = $"media {member.MediaId}" +
                                   (member.ReplacementPath is not null ? "   staged" : "") +
                                   (here ? "   this one" : "");
                        wrapped.Children.Add(MediaBox(member, text, onScreen, group, at,
                                                      new Thickness(0, 0, 6, 4)));
                    }
                });
            }
        }

        if (row.AlsoPlayedBy.Count > 0)
            Para($"Also played by {row.AlsoPlayedBy.Count} other event(s), which a swap " +
                 "changes too: " +
                 string.Join(", ", row.AlsoPlayedBy.Take(8)) +
                 (row.AlsoPlayedBy.Count > 8 ? $" and {row.AlsoPlayedBy.Count - 8} more" : ""),
                 Palette.Muted, top: 4);

        // Derived from the rows on screen rather than from AlsoPlayedBy, which is built by
        // filtering to events that have a shipped name: 174 media ids on one skin carry more
        // than one row, most of them under unnamed events, so this catches 372 rows the
        // named list cannot mention.
        if (sameId.Count > 0)
        {
            Para($"{sameId.Count} other row(s) here are this same media id, so a swap shows " +
                 "on all of them.", Palette.Muted, top: 4);
            // All of them, and no box. Every row here is the SAME media id as the one the
            // panel is describing, so a play button on any of them would play the bytes the
            // panel's own box already plays: ReplacementPath is assigned per media id
            // (MainWindow.Build.cs) and every lookup in AudioPreview is keyed on row.MediaId.
            // These are rows, which is what a line of blue text means everywhere else in this
            // panel, and the list is short enough to print whole: the largest group measured
            // over six skins is 17 rows, so 16 others. The Take(6) that used to be here said
            // "and N more" and then offered no way to reach them.
            foreach (var other in sameId) Link(other.Display, other);
        }
    }

    /// <summary>
    /// Which file is waiting on this sound. The Mod column can only say REPLACED, and which
    /// file is doing the replacing is exactly the thing that does not fit a column.
    /// </summary>
    private void Staged(SoundRow row)
    {
        if (row.ReplacementPath is null) return;
        var staged = _pending.FirstOrDefault(p => p.MediaId == row.MediaId);

        Head("Staged over it");
        Para(staged?.FileName ?? row.ReplacementPath, Palette.TagGreen);
        if (!string.IsNullOrWhiteSpace(staged?.Note))
            Para($"Your note: {staged.Note}", Palette.Muted);
        if (staged is not null)
            Para($"{staged.Bytes:N0} B, {staged.Codec}" +
                 (staged.Seconds is { } s ? $", {s:0.##} s" : "") +
                 (staged.Converted.Length > 0 ? $", converted from {staged.Converted}" : "") +
                 $". Lands in {staged.Status}.", Palette.Muted);
    }

    // ---- nothing selected ---------------------------------------------------

    /// <summary>
    /// What the list in front of you is, which is a view this window has never had.
    ///
    /// The empty state is not an empty frame and is not the last row's details either: one
    /// of those says nothing and the other is a lie about what is selected. Everything here
    /// is counted off the rows on screen, so it moves with the filter and with the tree.
    /// Kept to a handful of lines, because a dashboard here would be mirroring the columns,
    /// which is the thing this panel exists to stop.
    /// </summary>
    private void ListDetails(List<SoundRow> shown)
    {
        Head("Nothing selected");
        Para("Pick a sound to see what the bank does to it and where its audio is.",
             Palette.Muted);

        if (shown.Count == 0)
        {
            Para("Nothing is on screen. The filter box and Settings › Rows both decide that.",
                 Palette.Muted, top: 8);
            return;
        }

        Head("On screen");
        var media = shown.Select(r => r.MediaId).Distinct().Count();
        Para($"{shown.Count:N0} rows over {media:N0} distinct media " +
             (media == 1 ? "id" : "ids") +
             (media == shown.Count ? "." : ", so some rows share their audio."));

        var silent = shown.Count(r => r.Applied?.Silenced == true);
        if (silent > 0)
            Para($"{silent:N0} are silenced by the bank.", Palette.TagRed);

        var alternatives = shown.Count(r => r.Applied?.Alternative is { Count: > 1 });
        if (alternatives > 0)
            Para($"{alternatives:N0} are one of several alternatives, so replacing one alone " +
                 "is heard only some of the time.", Palette.Muted);

        var both = 0; var bankOnly = 0; var looseOnly = 0; var generators = 0; var notShipped = 0;
        foreach (var r in shown)
        {
            var inBank = Sounds.BankOfMedia.ContainsKey(r.MediaId);
            var loose = Session?.LooseMedia(r.MediaId) is not null;
            if (inBank && loose) both++;
            else if (inBank) bankOnly++;
            else if (loose) looseOnly++;
            else if (SoundSources.IsGenerator(r)) generators++;
            else if (r.Streamed) notShipped++;
        }
        Para($"{both:N0} ship in a bank and loose as well, {bankOnly:N0} only in a bank, " +
             $"{looseOnly:N0} only loose.", Palette.Muted);
        // Rows with nothing behind them, said rather than left to read as broken.
        if (generators > 0)
            Para($"{generators:N0} play a generator built into Wwise (silence, controller vibration, a synthesizer) " +
                 "rather than a recording, so they have no audio file.", Palette.Muted);
        if (notShipped > 0)
            Para($"{notShipped:N0} stream a file the game does not ship, so they are silent in a match; " +
                 "a file staged on one is added where the game looks for it.", Palette.Muted);

        if (_pending.Count > 0)
        {
            var onScreen = shown.Select(r => r.MediaId).ToHashSet();
            var here = _pending.Count(p => onScreen.Contains(p.MediaId));
            Para($"{here:N0} of the {_pending.Count:N0} staged files land on a row you can " +
                 "see here.", Palette.TagGreen);
        }
    }

    // ---- several selected ---------------------------------------------------

    /// <summary>
    /// What is true OF THE SET, never the first row's numbers.
    ///
    /// A reader will read one row's level as the group's, so the group gets its own answers.
    /// The one that earns its place is whether a selection is a WHOLE variation set or is
    /// missing members: four of five alternatives is precisely the mistake that makes a
    /// finished mod look broken.
    /// </summary>
    private void ManyDetails(List<SoundRow> shown)
    {
        var rows = SelectedRows;
        Head($"{rows.Count:N0} selected");

        var media = rows.Select(r => r.MediaId).Distinct().Count();
        Para($"{rows.Count:N0} rows over {media:N0} distinct media " +
             (media == 1 ? "id" : "ids") +
             (media == rows.Count ? "." : ", so some of them are the same audio."));

        var levels = rows.Where(r => r.Applied is not null).Select(r => (double)r.Applied.StaticDb)
                         .ToList();
        var silent = rows.Count(r => r.Applied?.Silenced == true);
        if (levels.Count > 0)
            Para(Math.Abs(levels.Max() - levels.Min()) < 0.001
                    ? $"Every one of them is at {levels[0]:+0.#;-0.#} dB."
                    : $"The bank puts them between {levels.Min():+0.#;-0.#} and " +
                      $"{levels.Max():+0.#;-0.#} dB.", Palette.Muted);
        if (silent > 0)
        {
            // Asked of the selection the same way the one-row verdict asks it of the row, so
            // the two states cannot disagree about the same sound: three rows of skin
            // 1029001's sfx_1029001_right_trajectory_play are one set, all silent, and all
            // three carry a Volume curve on a mixer above them.
            var liftable = rows.Count(r => r.Applied is { Silenced: true } s &&
                                           (s.LevelDriven || s.StateGroups > 0));
            Para(liftable == 0
                    ? $"{silent:N0} of them are silent; replacing those changes nothing " +
                      "anybody hears."
                 : liftable == silent
                    ? $"{silent:N0} of them are silent, though a game parameter or a state " +
                      "can raise every one of those again."
                    : $"{silent:N0} of them are silent. {liftable:N0} can be raised again by " +
                      "a game parameter or a state; replacing the rest changes nothing " +
                      "anybody hears.", Palette.TagRed);
        }

        var banks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loose = 0; var inBank = 0;
        foreach (var r in rows)
        {
            if (Sounds.BankOfMedia.GetValueOrDefault(r.MediaId) is { } b) { banks.Add(b); inBank++; }
            if (Session?.LooseMedia(r.MediaId) is not null) loose++;
        }
        Para($"{inBank:N0} have a bank copy and {loose:N0} have a loose file. A swap across " +
             $"this selection rebuilds {banks.Count:N0} .bnk file(s).", Palette.Muted, top: 4);

        var already = rows.Count(r => r.ReplacementPath is not null);
        if (already > 0)
            Para($"{already:N0} already have a file staged.", Palette.TagGreen);

        Para("Play uses the row the keyboard is on. A file dropped here replaces every one " +
             "of them.", Palette.Muted, top: 6);

        // Whole set or part of one. Counted against the skin's rows rather than the ones on
        // screen, because a filter hiding two members is exactly how a selection comes to be
        // missing them without anybody choosing that.
        //
        // Anchored on ONE container and not on the distinct group ids the selected rows carry.
        // A row carries the id of whichever container its medium was met in first, and this
        // game's banks build one set of media into several containers, so the rows covering
        // one container's slots need not agree about its id: the three rows covering container
        // 183834482 of bnk_sfx_1022001 carry two ids between them. Counting distinct ids told
        // a reader who had just selected all three of that container's alternatives from the
        // boxes that they spanned two different sets. The container anchored on is the one the
        // current row names, which is the box a plain click landed on and the row every other
        // figure in this panel already belongs to.
        var named = rows.Where(r => r.Applied?.Alternative is { Count: > 1 }).ToList();
        if (named.Count > 0)
        {
            // Counted in the container's SLOTS, the way the one-row section counts them, and
            // sized by the bank's own figure rather than by how many rows carry it. A
            // container two events reach is two rows in every slot, so counting rows made a
            // set of 2 read as a set of 4, and a selection holding both of its alternatives
            // was told in red that 2 members were left alone: container 252453612 of
            // bnk_sfx_1030001 is two slots carrying four rows, because both
            // sfx_1030001_q_child_loop_play and its _lv_up twin reach each of its media, and
            // 2 sets of skin 1030001 and 6 of 1029001 carry more rows than their container
            // has slots. The one-row section said "one of 2 alternatives" about the same
            // container at the same time.
            var lead = named.FirstOrDefault(r => ReferenceEquals(r, CurrentRow)) ?? named[0];
            Head("Their set");
            SetCoverage(lead.Applied.Alternative, lead.Bank, rows);

            // The set itself, box by box, so the gesture that got here goes on working. A
            // reader ctrl+clicking their way down a set arrives in this state on the second
            // click, and the list they were clicking in has to still be under the pointer.
            AlternativeBoxes(lead.Applied.Alternative, lead, shown.ToHashSet());
        }
    }

    /// <summary>
    /// How much of one container's set a selection covers, and what the members it leaves out
    /// go on doing. Four of five alternatives is precisely the mistake that makes a finished
    /// mod look broken, and it is the one thing a count of selected rows cannot show.
    ///
    /// Shared by the two states that can say it, so they cannot come to say different things
    /// about the same container: the sound's own view prints it under the boxes a reader is
    /// ctrl+clicking, and the several-selected view prints it where there is no sound to
    /// anchor on.
    ///
    /// Counted against the skin's rows rather than the ones on screen, because a filter hiding
    /// two members is exactly how a selection comes to be missing them without anybody choosing
    /// that. Counted in the container's SLOTS and sized by the bank's own figure rather than by
    /// how many rows carry it: a container two events reach is two rows in every slot, so
    /// counting rows made a set of 2 read as a set of 4 and told a selection holding both of
    /// its alternatives that 2 members were left alone. Container 252453612 of bnk_sfx_1030001
    /// is two slots carrying four rows, because both sfx_1030001_q_child_loop_play and its
    /// _lv_up twin reach each of its media, and 2 sets of skin 1030001 and 6 of 1029001 carry
    /// more rows than their container has slots.
    /// </summary>
    private void SetCoverage(WwiseProps.Variation set, string bank, List<SoundRow> rows)
    {
        var stated = set.Count;
        var mine = Slots(rows, set, bank);
        var left = stated - mine;
        // How many of the ones left out are on the skin's rows at all, because a member the
        // index has no row for cannot be selected however the list is filtered, and telling a
        // reader to go and get it would send them looking for nothing.
        var reachable = Slots(Sounds.Rows, set, bank) - mine;

        if (left <= 0)
        {
            Para($"The selection is the whole of one set of {stated} alternatives.",
                 Palette.Muted, top: 4);
            return;
        }

        // Two short lines rather than one sentence with three subordinate clauses. The first is
        // the warning and the second is what to do about it, and a reader who has read the
        // first already knows whether they need the second.
        Para($"{mine} of its {stated} alternatives are selected. The {left} left out go on " +
             "playing the old sound.", Palette.TagRed, top: 4);
        Para(reachable <= 0 ? "None of them are rows in this list."
           : reachable < left ? $"{reachable} of them are rows you can select here."
                              : "They are all rows you can select here.", Palette.TagRed);

        // Rows of the selection that this container does not hold, which is what a count of the
        // distinct group ids the rows carry was trying to say and got wrong. Said as a count of
        // rows left over rather than as a number of sets, because the sets those rows belong to
        // are their own containers and this panel is not describing them.
        var held = set.Members.SelectMany(m => m.Media).ToHashSet();
        var elsewhere = rows.Count(
            r => r.Applied?.Alternative is { Count: > 1 } &&
                 (!held.Contains(r.MediaId) ||
                  !string.Equals(r.Bank, bank, StringComparison.OrdinalIgnoreCase)));
        if (elsewhere > 0)
            Para($"{elsewhere:N0} of the selected rows belong to other sets.",
                 Palette.Muted, top: 2);

        // The slots of one container that a set of rows covers, matched on the MEDIA the
        // container states for each slot rather than on the group id the rows carry. A row
        // carries the id of whichever container its medium was met in first, and this game's
        // banks build one set of media into many containers, so the rows covering container
        // 183834482's three slots in bnk_sfx_1022001 carry two different ids and only one of
        // them is this one. Counting by the id read that as one slot covered of three and then
        // said in red that none of the two left out were rows to select, while both were
        // selected at the time.
        static int Slots(IEnumerable<SoundRow> of, WwiseProps.Variation set, string bank)
        {
            var media = of.Where(r => string.Equals(r.Bank, bank, StringComparison.OrdinalIgnoreCase))
                          .Select(r => r.MediaId).ToHashSet();
            return set.Members.Count(m => m.Media.Any(media.Contains));
        }
    }

    // ---- the parts every section is drawn out of ----------------------------

    /// <summary>
    /// A section heading: small, dim and in ordinary case, which is the same label the
    /// <see cref="Pair"/> lines below it already use, one size up in the hierarchy.
    ///
    /// It used to be upper-cased. Six of these down one narrow column is six bands of shouting
    /// over the lines that carry the answers, and a heading is not the thing anybody came to
    /// read. Nothing is lost by dropping it, because what marks a heading here is that it is
    /// smaller and dimmer than the body under it rather than that it is loud: the pane's own
    /// label-and-value lines are read that way on every row above.
    /// </summary>
    private void Head(string text) => Details.Children.Add(new TextBlock
    {
        Text = text,
        Foreground = Palette.Muted,
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 10, 0, 3),
    });

    /// <summary>One wrapped line. Wrapped and not trimmed: this panel is prose.</summary>
    private void Para(string text, IBrush ink = null, double top = 0, bool bold = false,
                      double size = 12.5) => Details.Children.Add(Line(text, ink, top, bold, size));

    /// <summary>
    /// The same line, handed back rather than added, for the lists that are built into a panel
    /// of their own before anybody knows whether that panel goes on screen or behind an
    /// expander. See <see cref="BoxList"/>.
    /// </summary>
    private static TextBlock Line(string text, IBrush ink = null, double top = 0,
                                  bool bold = false, double size = 12.5) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = ink ?? Palette.Fg,
        FontSize = size,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        Margin = new Thickness(0, top, 0, 2),
    };

    /// <summary>
    /// Put a list of boxes on screen: drawn where it is at most <see cref="InlineBoxes"/> long,
    /// and behind a shut expander that states its size where it is longer.
    ///
    /// The rule every list of boxes in this panel goes through, so that there is one rule. A
    /// mixer family used to be behind an expander whatever its size while a container's
    /// alternatives were always inline, and the difference was not a judgement about either
    /// list: the family's expander was put there for the 224-media case and then applied to
    /// the family of three as well, so a reader who had been shown boxes for a set of four saw
    /// a shut header for a mixer of four.
    /// </summary>
    /// <param name="which">
    /// Which list this is, for <see cref="_detailsOpen"/>: the name the reader's having opened
    /// it is remembered under, so the rebuilt panel draws it open again.
    /// </param>
    /// <param name="count">
    /// How many boxes <paramref name="fill"/> will draw. Passed rather than counted afterwards
    /// because the long case must not draw them at all: <paramref name="fill"/> is what costs,
    /// and running it to find out how much would be the cost this rule exists to avoid.
    /// </param>
    /// <param name="header">What the expander says while it is shut, size included.</param>
    /// <param name="body">The panel the boxes go into, which decides how they are laid out.</param>
    /// <param name="fill">
    /// Draws them into <paramref name="body"/>. Called now where the list is short, and on
    /// first opening where it is not: 224 media is 448 controls, and this panel is rebuilt on
    /// every selection change, including each of the hundreds a drop onto a large selection
    /// causes.
    /// </param>
    private void BoxList(string which, int count, string header, Panel body, Action fill)
    {
        if (count <= InlineBoxes)
        {
            fill();
            Details.Children.Add(body);
            return;
        }

        var all = Remembered(which, new Expander
        {
            Header = header,
            FontSize = 12,
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Content = body,
        });
        // Watched on the property rather than on an Expanding event so that the headless
        // checks, which have no pointer and set IsExpanded by hand, fill it by the same route
        // a reader's click does.
        all.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty || !all.IsExpanded ||
                body.Children.Count > 0) return;
            fill();
        };
        // Built open because the reader left it open, which raises no property change, so it
        // is filled here by the same call its first opening would have made. This is the one
        // case where the cost the expander exists to defer is paid on every redraw, and it is
        // the reader's to pay: they opened it, and a list that shut itself was worse.
        if (all.IsExpanded) fill();
        Details.Children.Add(all);
    }

    /// <summary>
    /// Build one of the panel's dropdowns open if the reader left it open, and remember what
    /// they do to it from here on. See <see cref="_detailsOpen"/>.
    /// </summary>
    private Expander Remembered(string which, Expander box)
    {
        box.IsExpanded = _detailsOpen.Contains(which);
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty) return;
            if (box.IsExpanded) _detailsOpen.Add(which);
            else _detailsOpen.Remove(which);
        };
        return box;
    }

    /// <summary>
    /// A label and its value on one line, with the labels aligned and the value wrapping in
    /// what is left. A grid and not a horizontal stack: a stack measures its children against
    /// infinite width, so a wrapping value in one never wraps and runs off the panel.
    /// </summary>
    private void Pair(string label, string value, IBrush ink = null)
    {
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("118,*") };
        var name = new TextBlock
        {
            Text = label,
            Foreground = Palette.Muted,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var read = new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ink ?? Palette.Fg,
            FontSize = 12.5,
        };
        Grid.SetColumn(read, 1);
        line.Children.Add(name);
        line.Children.Add(read);
        Details.Children.Add(line);
    }

    /// <summary>
    /// A line that takes you to another row. The point of the panel over a tooltip: after
    /// reading "3 of 5" the next thing the reader wants is the other four, and searching
    /// the list by media id is not that.
    /// </summary>
    private void Link(string text, SoundRow go) => SelectLine(text, [go], hold: false);

    /// <summary>A line that selects a set of rows when it is clicked.</summary>
    /// <param name="hold">
    /// Whether the panel goes on describing the same sound afterwards. True for the lines that
    /// select a GROUP, which is a reader gathering rows to act on rather than asking to read
    /// about a different one, and false for <see cref="Link"/>, whose whole promise is that it
    /// takes you to the row it names. See <see cref="_detailsAnchor"/>.
    /// </param>
    private void SelectLine(string text, IEnumerable<SoundRow> rows, bool hold = true)
    {
        var want = rows.ToList();
        var line = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.Accent,
            FontSize = 12.5,
            Margin = new Thickness(0, 2, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        // A line that does not hold moves the subject itself, by the double-click's own route,
        // rather than leaving it to be read back off the selection the line has just made, so
        // it lands on the row the line names whatever the list does with that selection.
        line.PointerPressed += (_, _) =>
        {
            _detailsHeld = hold;
            SelectRows(want);
            if (!hold && want.Count > 0) GoTo(want[0]);
        };
        Details.Children.Add(line);
    }

    /// <summary>
    /// One medium, as a box: press the glyph to hear it, press the label to select it.
    ///
    /// A reader has two things to do with a sound this panel merely NAMES, and until now it
    /// offered one of them. Hearing any of a container's other alternatives meant selecting
    /// one and then going back up to the toolbar, which is the trip this panel exists to
    /// save on every other question it answers.
    ///
    /// The label half is a SELECTION SURFACE and not a jump to one row. That is the difference
    /// between a list of alternatives you can read and a list you can act on: a reader
    /// replacing a variation set wants the whole set selected, which is several rows, and a
    /// box that only moved the list somewhere could not say whether its own row was the one
    /// the list was on. So a box is drawn selected when it is, and it answers the three
    /// gestures the list answers to. What it does NOT do is move what the panel is describing:
    /// that is <see cref="_detailsAnchor"/>'s, and a fourth gesture of its own.
    ///
    /// Those three were READ OFF THE LIST rather than chosen. The grid is left in the Extended
    /// selection mode it defaults to (MainWindow.axaml names no SelectionMode on Grid_), and
    /// driven with real clicks it does this: a plain click leaves the clicked row alone
    /// selected, a ctrl+click adds it or takes it away again, and a shift+click ADDS the run
    /// from the last plainly or ctrl clicked row to this one while keeping whatever else was
    /// selected. Clicking row 0, then ctrl+clicking row 2, then shift+clicking row 3 leaves
    /// rows 0, 2 and 3 selected and not rows 2 and 3. UiSelfTest measures that on the drawn
    /// grid and then asserts the boxes do the same, so the two cannot come apart.
    ///
    /// Two adjacent buttons and not one control with two gestures. A double-click, a modifier
    /// or a glyph that appears on hover are all invisible in a render, which is how everything
    /// in this window is checked, and an act nobody can find is not an act. The modifiers
    /// above are what the list forces: they are the gesture a reader already has for a
    /// selection, and a surface that ignored them could only ever hold one thing. Everything
    /// a box does without one is visible, and the play half takes no modifier at all.
    ///
    /// The one exception is the double-click that moves what the panel is DESCRIBING, and it is
    /// taken knowingly. None of the three gestures above can be given that job, because each of
    /// them already has one and because a panel that followed a plain click was the defect this
    /// pane's subject rule exists to close (see <see cref="_detailsAnchor"/>). It is named in
    /// the box's own tooltip beside the three that are visible, and it is not the only way
    /// there: a plain click leaves its row alone selected and scrolls it into view, so clicking
    /// that row IN THE LIST does the same thing by the route every reader already knows.
    ///
    /// Both halves are Buttons carrying the toolbar's own class, so the chrome is the rule at
    /// MainWindow.axaml:40 rather than a border drawn here that would drift from it: the box
    /// is the same control as the buttons along the top of the window. Selected is one more
    /// class on the same rule (MainWindow.axaml, Button.tool.picked). The glyph is on the
    /// LEFT because the toolbar's own Play reads "▶  Play" (MainWindow.Browse.cs:63), and
    /// because PaneDetails puts a scroll bar down the right edge exactly when a list is long
    /// enough to need one, which is where a right-hand glyph would end up.
    ///
    /// The two halves do not have the same reach, and that is the fact this box is built
    /// around. PlayRow needs the rows the skin was indexed into and never asks what is on
    /// screen, so a medium the filter is hiding can still be played; selecting it cannot be
    /// done at all. With only the default row filter on, about one box in four is in that
    /// state, so it is drawn differently rather than explained after a click that did nothing,
    /// and it is never drawn selected: a row that is not in the list cannot be in the list's
    /// selection, and a box that lit up and then changed nothing would be the worse half of
    /// both answers.
    /// </summary>
    /// <param name="member">The row the glyph plays, and the first the label selects.</param>
    /// <param name="label">What the box says, worded by the section it belongs to.</param>
    /// <param name="onScreen">The rows the list is showing, which decides the label's half.</param>
    /// <param name="group">
    /// Every box of the run this one belongs to, each with the rows it stands for, so shift can
    /// select from one of them to another. A slot's entry is several rows where the container
    /// gives it a sub-container: bnk_sfx_1034001's container 369457676 gives each of its three
    /// slots a layer container of two sounds, so one alternative is two rows and selecting one
    /// of them would be the box answering a different question from the one its label asks.
    /// </param>
    /// <param name="at">Which of <paramref name="group"/> this box is.</param>
    /// <param name="margin">Spacing, which differs between a column of boxes and a wrap of them.</param>
    private Control MediaBox(SoundRow member, string label, HashSet<SoundRow> onScreen,
                             List<List<SoundRow>> group, int at, Thickness? margin = null)
    {
        // Auto,* rather than two boxes in a horizontal stack, for the reason Pair gives: a
        // stack measures its children against infinite width, so the label would never be
        // held to the panel and would run off the edge of it.
        var box = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = margin ?? new Thickness(0, 2, 0, 0),
        };
        // Named, so the checks can ask the panel what it DREW rather than infer a box from
        // the shape of two controls that happen to sit beside each other. No style selects
        // it: the chrome is on the buttons inside, where the toolbar's rule reaches it.
        box.Classes.Add("mediabox");

        var play = new Button
        {
            Content = "▶",
            FontSize = 11,
            Padding = new Thickness(8, 4),
            Margin = new Thickness(0, 0, 4, 0),
        };
        play.Classes.Add("tool");
        // The toolbar's own sentence for its own Play button, so the two cannot come to say
        // different things about what is about to be heard, and then the ROW whose chain is
        // about to be applied. A box is one per medium and the Ingame preview is per row: in
        // bnk_sfx_1030001 154 of 623 distinct media are reached by more than one row and 136
        // of those by rows whose banks state a different level, media 378759846 by three at
        // -14, -7 and -4 dB. Which of them the box plays is a defensible pick and not a
        // wrong one, but it is a pick, and with Ingame on it decides the loudness, so the
        // box says whose it is rather than leaving the reader to assume there was only one.
        ToolTip.SetTip(play, $"Play media {member.MediaId}. A file staged over it plays " +
                             "instead of the game's, because that is what will ship." +
                             Environment.NewLine + Environment.NewLine +
                             $"With Ingame on it applies what the bank does to {member.Display}" +
                             (member.AppliedLabel is { Length: > 0 } what
                                 ? $": {what}." : ", which is nothing."));
        // Straight to the list's play path, with nothing repeated here. That one method is
        // where a staged file wins over the game's audio, where a prefetch stub is swapped
        // for the whole loose file, where Ingame decides between the file and the sound, and
        // where hearing nothing is reported rather than swallowed. A second play path would
        // have all four of those to get wrong.
        play.Click += (_, _) => PlayRow(member, original: false);
        box.Children.Add(play);

        // What this box stands for, and whether the list is holding it. Selected means ANY row
        // of it that the list is showing is selected, because a box stands for one medium and
        // a medium is reached by more than one row often enough that the other rule was wrong
        // most of the time it mattered: in bnk_sfx_1030001, 154 of 623 distinct media carry
        // several rows, so a reader clicking one of those in the list would have seen the box
        // for the medium they had just selected stay dark. What the box says and what
        // ctrl+click does to it are the same question asked twice, so ToggleRows flips on the
        // same "any" and the drawn state cannot disagree with the gesture.
        //
        // A box with nothing on screen is never selected, because a row the filter is hiding
        // cannot be in the list's selection.
        var want = group[at];
        var reach = want.Where(onScreen.Contains).ToList();
        var picked = reach.Any(_detailsPicked.Contains);

        var face = new Button
        {
            // An explicit TextBlock rather than the string, so the label is trimmed at
            // the panel's edge instead of widening the box past it, and so its colour is
            // the one this box chose rather than the one the button template inherits.
            Content = new TextBlock
            {
                // The mark this window already uses for "there are more of these and the
                // row filter is holding them back" (MainWindow.axaml.cs:803).
                Text = reach.Count > 0 ? label : label + " *",
                Foreground = reach.Count > 0 ? Palette.Fg : Palette.Muted,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        face.Classes.Add("tool");
        if (picked) face.Classes.Add("picked");
        ToolTip.SetTip(face, reach.Count == 0 ? CannotReach(member) : Gestures(want, picked));
        // The modifiers, taken off the press rather than off the click, because Avalonia puts
        // them on one and not the other. HandledEventsToo, because Button marks a left press
        // handled as it takes it, and a handler that asked to be skipped for that would never
        // run: the press is seen, nothing about it is consumed, and the Button goes on to
        // raise Click on release exactly as before. Click and not the press itself is what
        // acts, so a keyboard reaches a box and so the headless checks can raise it.
        face.AddHandler(InputElement.PointerPressedEvent,
                        (_, e) => { _boxMods = e.KeyModifiers; _boxPressed = face; },
                        RoutingStrategies.Bubble, handledEventsToo: true);
        // Left enabled where it cannot reach, on purpose. A disabled control cannot answer,
        // and the selection already has the honest sentence for this: pressing it says the row
        // is not in the list as it is filtered now, which is the same thing the tooltip says
        // and is said where the reader is looking after a press.
        face.Click += (_, _) =>
        {
            var mods = ReferenceEquals(_boxPressed, face) ? _boxMods : KeyModifiers.None;
            _boxPressed = null;
            PickBox(group, at, mods);
        };
        // Go to this one, which is the act the other three gestures deliberately do not do:
        // they move the list's selection and leave the panel describing the sound the reader
        // was reading (see _detailsAnchor). A double-click is the only one of the four that is
        // invisible in a render, which this file otherwise refuses on principle, and it is
        // taken here for two reasons. It is named in the box's own tooltip, beside the three
        // that are not invisible. And it is not the only way to do it: a plain click leaves
        // this row alone selected and scrolls it into view, so clicking it in the LIST is the
        // visible route to the same place and is the route a reader already knows.
        //
        // Read off the press's click count and not off DoubleTapped, which never fired on a box
        // that was not already selected. Its first click changes the selection, the selection
        // redraws the panel before the second press arrives, and the second press lands on the
        // box that redraw built; Avalonia raises a double-tap only when both presses land on the
        // same control, so the double-click did nothing on exactly the boxes a reader would
        // want it on. The click count is the mouse's and survives the redraw, and the row is the
        // same object in both panels, so the two together name the box that was double-clicked.
        // A plain one only: ctrl+clicking a box in and straight back out again is two presses
        // on one spot inside the double-click time too, and it has to stay two toggles.
        // Measured in UiSelfTest with two real presses.
        face.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            var second = e.ClickCount % 2 == 0 && ReferenceEquals(_boxDown, member) &&
                         e.KeyModifiers == KeyModifiers.None &&
                         e.GetCurrentPoint(face).Properties.IsLeftButtonPressed;
            _boxDown = member;
            if (second) GoTo(member);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        Grid.SetColumn(face, 1);
        box.Children.Add(face);
        return box;
    }

    /// <summary>
    /// What pressing a box's label will do to the selection, in the words the list's own rows
    /// would use. Three gestures is two more than a tooltip usually has to explain, and they
    /// are worth explaining precisely because the list teaches them elsewhere: a reader who
    /// has never ctrl+clicked a row will not guess that a box takes it.
    /// </summary>
    private string Gestures(List<SoundRow> want, bool picked) =>
        (want.Count == 1 ? "Click selects this row and nothing else."
                         : $"Click selects the {want.Count} rows this alternative holds and " +
                           "nothing else.") +
        (picked ? " It is one of the selected ones now." : "") +
        Environment.NewLine +
        "Ctrl+click adds it to the selection or takes it out again, and shift+click adds " +
        "every box from the last one you clicked to this one. That is what the list does " +
        "with its own rows." + Environment.NewLine +
        // The fourth gesture, and the only one that moves what this panel is describing. Said
        // here because it is the one nobody can see: the other three leave a mark on screen.
        "None of those change what the panel is describing. Double-click to go to this sound " +
        "instead." + Environment.NewLine + Environment.NewLine +
        (want.Count == 1
            ? want[0].Display
            : string.Join(Environment.NewLine,
                          want.Select(r => $"media {r.MediaId}   {r.Display}")));

    /// <summary>
    /// A box was pressed. Which of the list's three gestures that is, and what it does.
    ///
    /// Ctrl and a plain click both move the anchor a run is measured from, which is the grid's
    /// own rule and is measured on the drawn grid in UiSelfTest: clicking row 0, ctrl+clicking
    /// row 2 and then shift+clicking row 3 leaves rows 0, 2 and 3 selected, so the run was
    /// measured from row 2 and added to what was there rather than replacing it. Shift leaves
    /// the anchor where it found it. That last one cannot be measured on the grid, because a
    /// run that only ever adds can never shrink a selection back to show where it started.
    ///
    /// A shift with no anchor to measure from is a plain click, which is what the grid does
    /// with a shift+click into a list holding nothing, and is asserted of it there. That is
    /// also what happens when the anchor was a box in some other set: the boxes are rebuilt on
    /// every selection change, and a run can only be measured inside the group drawn now.
    /// </summary>
    private void PickBox(List<List<SoundRow>> group, int at, KeyModifiers mods)
    {
        // Whatever this gesture does to the selection, it does not move what the panel is
        // describing. See _detailsAnchor: a box rewriting the page it is drawn on is what made
        // the second ctrl+click of a set throw away the sound's own answers, and what moved the
        // panel to a different container on 32 per cent of them after a plain click.
        _detailsHeld = true;
        var want = group[at];
        if (mods.HasFlag(KeyModifiers.Control))
        {
            ToggleRows(want);
            Anchor(want);
            return;
        }
        if (mods.HasFlag(KeyModifiers.Shift))
        {
            var from = group.FindIndex(g => g.Any(r => r.MediaId == _boxAnchor));
            if (from >= 0)
            {
                var first = Math.Min(from, at);
                var last = Math.Max(from, at);
                AddRows(group.Skip(first).Take(last - first + 1)
                             .SelectMany(g => g).Distinct().ToList());
                return;
            }
        }
        SelectRows(want);
        Anchor(want);
    }

    /// <summary>Remember this box as the one a shift-selected run is measured from.</summary>
    private void Anchor(List<SoundRow> want) => _boxAnchor = want.Count > 0 ? want[0].MediaId : 0;

    /// <summary>
    /// Describe this sound instead, which is what a double-click on a box asks for.
    ///
    /// Written straight rather than through the list, because going through the list is exactly
    /// what it must not do: a row's own container need not be the one whose boxes were clicked
    /// (see <see cref="_detailsAnchor"/>), so a subject taken back off the selection would land
    /// wherever the row pointed. The selection the click already made is left where it is.
    /// </summary>
    private void GoTo(SoundRow member)
    {
        _detailsAnchor = member;
        _detailsHeld = true;
        ShowDetails(force: true);
    }

    /// <summary>
    /// Press the label half of one of the boxes the panel has drawn, for the render harness.
    ///
    /// A box is the one surface in this window a render could not exercise, and it is the
    /// surface the panel's whole subject rule is about: what a plain click on it does to the
    /// page it is drawn on is a layout claim, so it has to be a layout somebody can look at.
    /// </summary>
    /// <param name="box">Which box, counting from one in the order they are drawn.</param>
    /// <returns>How many boxes the panel had drawn.</returns>
    public int ClickBoxSync(int box)
    {
        // The drawn control, pressed by raising the event its own handler is on, which is how
        // the checks do it: a render has no pointer, and a harness that called what the handler
        // calls would be driving a copy of the panel rather than the panel. Found down the
        // visual tree because a box list lives in a panel of its own, and by the class the box
        // carries rather than by the shape of two controls that sit beside each other.
        var faces = Details.GetVisualDescendants().OfType<Grid>()
                           .Where(g => g.Classes.Contains("mediabox") && g.Children.Count == 2)
                           .Select(g => g.Children[1]).ToList();
        if (box < 1 || box > faces.Count) return faces.Count;
        faces[box - 1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return faces.Count;
    }

    /// <summary>
    /// Describe one medium with nothing awaited, for the headless checks.
    ///
    /// The same method a box's double-click calls and not a copy of it, for the reason
    /// <see cref="PlayMediaSync"/> gives: what the harness drives has to be what a reader
    /// doing it would get. A double-click is the one gesture on a box that cannot be put in
    /// through the headless mouse, which raises presses and releases and no click count, so
    /// this is the route the check has.
    /// </summary>
    /// <returns>Whether a row for that medium was found to describe.</returns>
    public bool GoToMediaSync(uint media)
    {
        var member = (Grid_.ItemsSource as IEnumerable<SoundRow>)?
                     .FirstOrDefault(r => r.MediaId == media);
        if (member is null) return false;
        GoTo(member);
        return true;
    }

    /// <summary>
    /// Why this box cannot select its row, named by the same table that removed it.
    ///
    /// <see cref="RowClasses.HidingKinds"/> is what the filter itself runs on, so the
    /// explanation cannot drift from the setting that caused it. It comes back empty when
    /// the filter box rather than a kind of row is what is holding it back, and that is a
    /// different sentence rather than a vaguer one: one of them is undone in Settings and
    /// the other by clearing a text box.
    /// </summary>
    private string CannotReach(SoundRow member)
    {
        var hidden = (IEnumerable<string>)_settings.HiddenRowClasses ?? RowClasses.HiddenByDefault;
        var by = RowClasses.HidingKinds(member, hidden)
                           .Select(c => c.Label).Distinct()
                           .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return $"media {member.MediaId} is not in the list as it is filtered now, so this " +
               "cannot select it. The play button still works: it reads the bank, not the " +
               "list." + Environment.NewLine + Environment.NewLine +
               (by.Count > 0
                   ? "Hidden by: " + string.Join(", ", by) + Environment.NewLine +
                     "Settings › Rows turns them back on."
                   : "The filter box is what is keeping it off screen.");
    }

    /// <summary>
    /// Which of these rows the list is showing, and the honest sentence where it is none of
    /// them. A member the filter is hiding is not on screen to be selected, and every one of
    /// the three gestures below has to answer that the same way or a reader would learn that
    /// ctrl+click works on a box where a plain click did nothing.
    /// </summary>
    private List<SoundRow> Reachable(List<SoundRow> want)
    {
        var onScreen = (Grid_.ItemsSource as IEnumerable<SoundRow>)?.ToHashSet() ?? [];
        var reachable = want.Where(onScreen.Contains).ToList();
        if (reachable.Count == 0) Say("none of those are in the list as it is filtered now.");
        return reachable;
    }

    /// <summary>
    /// Put the list's selection on these rows and nothing else, and say how many of them it
    /// could not reach. Silently selecting four of five would be the panel telling the reader
    /// they have the set.
    /// </summary>
    private void SelectRows(List<SoundRow> want)
    {
        if (Grid_.SelectedItems is not { } picked) return;
        var reachable = Reachable(want);
        if (reachable.Count == 0) return;

        picked.Clear();
        foreach (var r in reachable) picked.Add(r);
        Grid_.ScrollIntoView(reachable[0], null);
        Say(reachable.Count == want.Count
            ? $"{reachable.Count} selected."
            : $"{reachable.Count} of {want.Count} selected; the rest are not in the list as " +
              "it is filtered now.");
    }

    /// <summary>
    /// Add these rows to what is already selected, which is what a shift-selected run does.
    /// The count said afterwards is the whole selection's and not the run's, because that is
    /// the number the reader is building up and the one the list now holds.
    /// </summary>
    private void AddRows(List<SoundRow> want)
    {
        if (Grid_.SelectedItems is not { } picked) return;
        var reachable = Reachable(want);
        if (reachable.Count == 0) return;

        foreach (var r in reachable) if (!picked.Contains(r)) picked.Add(r);
        Grid_.ScrollIntoView(reachable[^1], null);
        Say($"{picked.Count} selected.");
    }

    /// <summary>
    /// Take these rows out of the selection where any of them is in it and put them all in
    /// where none is, which is what ctrl+click does to a row of the list.
    ///
    /// Out on ANY and in all together, because one box can stand for several rows: a slot the
    /// bank gives a layer container is two, and so is a medium two events reach. Flipping them
    /// one at a time would leave a box half selected, which it has no way to draw, and the
    /// condition is "any" rather than "all" so that it is the same condition the box is drawn
    /// lit by: whatever a reader can see is what their next ctrl+click undoes. Emptying the
    /// selection altogether is left to happen, since that is what ctrl+clicking the only
    /// selected row of the list does too, and the panel has an answer for nothing selected.
    /// </summary>
    private void ToggleRows(List<SoundRow> want)
    {
        if (Grid_.SelectedItems is not { } picked) return;
        var reachable = Reachable(want);
        if (reachable.Count == 0) return;

        if (reachable.Any(picked.Contains))
        {
            foreach (var r in reachable) picked.Remove(r);
            Say(picked.Count == 0 ? "nothing selected." : $"{picked.Count} selected.");
            return;
        }

        foreach (var r in reachable) if (!picked.Contains(r)) picked.Add(r);
        Grid_.ScrollIntoView(reachable[0], null);
        Say($"{picked.Count} selected.");
    }

    /// <summary>
    /// Select rows with nothing awaited, for the render harness and the headless checks.
    ///
    /// Both of them have to be able to put the details panel in each of its three states,
    /// and a click is not available to either. Going through the grid's own SelectedItems
    /// rather than a back door, so what is drawn is what a reader clicking would get.
    /// </summary>
    /// <param name="count">How many of the rows on screen to take, from the top.</param>
    /// <param name="media">
    /// One media id instead, which is how a render reaches a row that has something worth
    /// drawing rather than whichever row happens to sort first. Asking for more rows than
    /// that medium has goes on through the rest of its container, in the container's own
    /// order, because SEVERAL ROWS OF ONE SET is a state nothing else could reach: a whole
    /// set is what ctrl+clicking down the boxes leaves selected, and the rows of one are
    /// scattered through a list of hundreds sorted by something else, so neither a count from
    /// the top nor one medium can name them. It is the same rows a reader gets by clicking,
    /// because it goes through the same SelectRows their click does.
    /// </param>
    /// <returns>How many rows ended up selected.</returns>
    public int SelectSync(int count, uint media = 0)
    {
        var shown = (Grid_.ItemsSource as IEnumerable<SoundRow>)?.ToList() ?? [];
        if (media == 0)
        {
            SelectRows(shown.Take(count).ToList());
            return Grid_.SelectedItems?.Count ?? 0;
        }

        var want = shown.Where(r => r.MediaId == media).ToList();
        if (want.Count > 0 && want.Count < count &&
            want[0].Applied?.Alternative is { Count: > 1 } set)
            foreach (var also in set.Members.SelectMany(s => s.Media)
                                    .SelectMany(m => shown.Where(
                                        r => r.MediaId == m && r.Bank == want[0].Bank)))
                if (want.Count < count && !want.Contains(also)) want.Add(also);

        SelectRows(want.Take(Math.Max(count, 1)).ToList());
        // Naming a medium is naming a SUBJECT, which is the whole reason --pick exists: a
        // render of a row nobody asked for shows none of the sections only some rows have.
        // Written here so that asking for several rows of one set still draws that sound's own
        // answers rather than the set's, and held so the draw this queues keeps it. See
        // _detailsAnchor.
        if (want.Count > 0) { _detailsAnchor = want[0]; _detailsHeld = true; }
        return Grid_.SelectedItems?.Count ?? 0;
    }

    /// <summary>
    /// Open every expander the panel has drawn, for the render harness and the headless
    /// checks.
    ///
    /// The panel puts its two longest lists behind one, shut, and a render has nobody to
    /// click it: the whole point of the family's expander is that it holds two hundred boxes
    /// without lengthening the panel, and that is a layout somebody has to be able to look
    /// at. Written against the same IsExpanded a reader's click sets, so what is rendered is
    /// what opening it gives.
    /// </summary>
    /// <returns>How many were shut and now are not.</returns>
    public int ExpandDetailsSync()
    {
        var opened = 0;
        foreach (var box in Details.Children.OfType<Expander>())
            if (!box.IsExpanded) { box.IsExpanded = true; opened++; }
        return opened;
    }

    /// <summary>
    /// Play one medium with nothing awaited, for the render harness and the headless checks.
    ///
    /// The same method a box's play button calls, and not a copy of it, for the reason
    /// <see cref="SelectSync"/> gives about going through the grid's own selection: what the
    /// harness drives has to be what a reader pressing the button would get, or the check is
    /// of the harness. Found in the indexed rows rather than in the list, which is the whole
    /// difference between the two halves of a box: a medium the filter is hiding still plays.
    /// Any row carrying the medium will do, because a staged file is assigned by media id and
    /// every lookup the preview makes is keyed on it, so the bytes do not depend on which
    /// row asks for them.
    /// </summary>
    /// <returns>Whether a row for that medium was found to play.</returns>
    public bool PlayMediaSync(uint media)
    {
        var member = Sounds?.Rows.FirstOrDefault(r => r.MediaId == media);
        if (member is null) return false;
        PlayRow(member, original: false);
        return true;
    }

    /// <summary>
    /// What the panel actually drew, in pixels, for the render harness.
    ///
    /// A claim about density is a claim about vertical space, and a render cannot be squinted
    /// at for that: the same words at a different wrap take a different number of lines, and
    /// the thing a reader pays for is the height above the boxes rather than the character
    /// count. Read off the drawn controls for the reason the checks give, that the panel is
    /// built from the row and reading the row back would be comparing the row with itself.
    ///
    /// Only the boxes drawn INLINE are counted, because a box inside the expander's panel is
    /// not a child of <see cref="Details"/> and costs the reader nothing until it is opened.
    /// </summary>
    public string DetailsShape()
    {
        if (!PaneDetails.IsVisible) return "details: not showing";

        // The last child's bottom edge rather than the panel's own height. The panel sits in a
        // ScrollViewer that arranges it to the taller of its content and the viewport, so its
        // Bounds read 660 px for three different rows on this machine while the content under
        // them ran to 578, 596 and 660: the figure would have been the window's, not the
        // panel's, for everything that fits on screen.
        var drawn = Details.Children.Count > 0 ? Details.Children[^1].Bounds.Bottom : 0;
        var prose = Details.Children.OfType<TextBlock>().Count(t => t.Text is { Length: > 0 });

        // A box list lives in a panel of its own, which BoxList either adds here or hands to an
        // expander, so the boxes are a child's children and not this panel's. Counted through
        // the children rather than down the visual tree so that the ones inside a shut expander
        // are not counted: those cost the reader nothing, which is the whole point of the rule.
        var boxes = 0;
        double above = drawn;
        foreach (var child in Details.Children)
        {
            var inThis = child is Grid one && one.Classes.Contains("mediabox") ? 1
                       : child is Panel many
                           ? many.Children.Count(c => c is Grid g && g.Classes.Contains("mediabox"))
                           : 0;
            if (inThis == 0) continue;
            if (boxes == 0) above = child.Bounds.Top;
            boxes += inThis;
        }
        return $"details: {drawn:0} px drawn in {_detailsMs:0.0} ms, " +
               $"{above:0} px above the first box, {prose} paragraph(s), " +
               $"{boxes} box(es) inline";
    }

    /// <summary>How many bytes of this medium the open bank carries. See <see cref="_bankBytes"/>.</summary>
    private long BankBytes(uint mediaId)
    {
        if (_bankBytes.TryGetValue(mediaId, out var known)) return known;

        long bytes = 0;
        if (Sounds is not null && Sounds.Raw.TryGetValue(mediaId, out var raw))
            bytes = raw.LongLength;
        else if (Sounds is not null && Sounds.Embedded.TryGetValue(mediaId, out var data))
            // A bank that will not re-open must not be what fails the panel: the sizes are
            // worth having and nothing else here depends on them.
            try { bytes = data.GetData()?.LongLength ?? 0; } catch { bytes = 0; }

        _bankBytes[mediaId] = bytes;
        return bytes;
    }
}
