using System.IO;
using Avalonia;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace MRAudioKit.UI;

/// <summary>
/// Dropping files onto the window.
///
/// The old window had a drop pane in one corner, which meant the drop only worked
/// once you had found it. Here the whole window takes a drop and the pointer decides
/// what it means: over the list, with sounds selected, one file replaces every one of
/// them; anywhere else the filenames are the assignment.
///
/// Nothing here stages anything itself. The Build tab owns the staged list and both
/// ways into it, so a drop is only another way of calling what is already there, and
/// a change to how staging works cannot leave this file saying something else.
/// </summary>
public partial class MainWindow
{
    /// <summary>What the status line said before a drag arrived, to put back on leave.</summary>
    private string _dragStatusBefore;

    /// <summary>
    /// The drag this window is currently being dragged over, and what it was found to
    /// carry. Drag over fires on every pointer move, so everything that costs anything
    /// is worked out once per drag and this reference is what says the drag is still
    /// the same one. Classifying is not free either: telling a folder from a file is a
    /// stat per path, and a dropped folder of audio can carry hundreds.
    /// </summary>
    private object _dragSource;
    private Carried _dragCarried = Carried.Nothing;

    private bool _dropHooked;

    /// <summary>
    /// Start taking drops.
    ///
    /// Idempotent because the wiring has two ways in: this is public so the window's
    /// constructor may call it, and <see cref="OnOpened"/> calls it regardless.
    /// Registering the handlers twice would stage one dropped file twice.
    /// </summary>
    public void HookDragAndDrop()
    {
        if (_dropHooked) return;
        _dropHooked = true;

        // AllowDrop inherits down the tree, so setting it on the window covers every
        // control in it and one set of handlers sees every drop. The events bubble, so
        // these fire wherever the pointer is; where that was is read off e.Source.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>
    /// Wired from here rather than from the constructor because taking drops is this
    /// file's own business. A partial that needs a line added to a file somebody else
    /// owns is a partial that silently does nothing until they add it, and a feature
    /// that is inert until someone reads the handover notes is not a feature yet.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        HookDragAndDrop();
    }

    // ---- while the pointer is over us ---------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var carried = CarriedBy(e);
        e.DragEffects = carried.Anything ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        // The pane's border and the status line are two views of one answer, so both
        // come off the same test here. The border is lit from the drag rather than from
        // a Drop handler on the pane, because a handler there would be a second one
        // seeing the same file: this one still runs afterwards, and the file would be
        // staged twice, reported as "N staged (0 new, N updated)".
        PaneLit(OverThePane(e) && carried.Anything);

        // Where a drop lands depends on where the pointer is, which a lit border cannot
        // say on its own. Saying it before the drop is the whole point: one file onto
        // four hundred selected rows is not something to find out afterwards.
        _dragStatusBefore ??= Status.Text;
        Say(DropHint(e, carried));
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // Leaving fires for every boundary crossed inside the window as well, including
        // the ones between the pane's own children. Both of these are put back rather
        // than held: drag over runs on the next pointer move and sets them again, which
        // is what makes a boundary crossed inside the pane invisible, while a drag that
        // has really left the window gets no such move and must not stay lit.
        PaneLit(false);
        if (_dragStatusBefore is null) return;
        Say(_dragStatusBefore);
        _dragStatusBefore = null;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var carried = CarriedBy(e);
        e.Handled = true;

        // Tells whoever started the drag that their files were copied, not moved.
        // Left unset, what goes back is whatever the platform offered, and a drag out
        // of Explorer offers Move among the rest: a drop answered with Move is
        // Explorer deleting the file it just handed us.
        e.DragEffects = carried.Anything ? DragDropEffects.Copy : DragDropEffects.None;

        // The hint and the highlight are spent: whatever runs below writes the real
        // outcome, and a drag that starts after this one must read the status line and
        // the border fresh.
        PaneLit(false);
        _dragStatusBefore = null;
        _dragSource = null;
        _dragCarried = Carried.Nothing;

        if (carried.Paths.Count == 0) { Say("nothing to stage, that drop carried no files."); return; }

        // A bank or a pak is something to LOOK AT, not a replacement to stage. The
        // extension says which, so the same target serves both.
        if (carried.Banks.Count > 0) { _ = DropOpen(carried.Banks); return; }

        // Everything below this line stages something, so the staged list is put on screen
        // first. The panel's own reason for existing is that staging already worked from
        // three places and none of them could be SEEN, which made a mis-stage undoable
        // except by clearing the lot; a drop that lands behind the details panel is that
        // same blindness back again. Not from OnDragOver, which fires on every pointer move
        // and would flicker as the pointer crossed the panel, and not for a .bnk or a drop
        // carrying nothing, neither of which stages anything. It is deliberately not
        // remembered: see PaneShow.
        PaneShow(details: false);

        List<SoundRow> rows = OverTheList(e) ? SelectedRows : [];

        // One file onto a selection is the explicit assignment and the one people use
        // most: the filename is irrelevant, the encode happens once, and every selected
        // sound gets those same bytes. It has to be the only thing dropped, not merely
        // the only audio file: a folder alongside it expands to audio of its own, which
        // this branch would throw away without saying so.
        if (carried.Paths.Count == 1 && carried.Audio.Count == 1 && rows.Count > 0)
        {
            _ = StageOnto(carried.Audio[0], rows);
            return;
        }

        // Folders and files together: Stage expands a folder itself and ignores what it
        // cannot decode, so the drop is handed over whole. Anything reaching here with
        // rows selected has had that selection ignored, whether because several files
        // came in or because one of the things dropped was a folder.
        _ = Stage(carried.Paths, rows.Count > 0
            ? "only a single audio file replaces a selection, so the filenames decided, and the selection was ignored."
            : null);
    }

    // ---- what a drop is offering --------------------------------------------

    /// <summary>
    /// What one drag carries, worked out once when it arrives.
    /// </summary>
    /// <param name="Paths">Every local path in the drag, in the order it offered them.</param>
    /// <param name="Audio">Those that can be staged.</param>
    /// <param name="Banks">Those that can be opened instead, meaning .bnk and .pak.</param>
    /// <param name="Folders">Whether any of them is a directory, which Stage expands.</param>
    private sealed record Carried(List<string> Paths, List<string> Audio,
                                  List<string> Banks, bool Folders)
    {
        public static readonly Carried Nothing = new([], [], [], false);

        /// <summary>Whether letting go here would do anything at all.</summary>
        public bool Anything => Audio.Count > 0 || Banks.Count > 0 || Folders;
    }

    /// <summary>
    /// What the drag carries. Paths without a local file are dropped: a drag from a
    /// remote share or a virtual folder has nothing for us to read.
    /// </summary>
    private Carried CarriedBy(DragEventArgs e)
    {
        // The null check is what keeps the cache honest once it has been reset: without
        // it a drag carrying nothing would match the reset sentinel and be answered
        // with the contents of the drag before it.
        if (e.DataTransfer is not null && ReferenceEquals(_dragSource, e.DataTransfer))
            return _dragCarried;

        _dragSource = e.DataTransfer;

        var paths = e.DataTransfer?.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList() ?? [];

        _dragCarried = new Carried(
            paths,
            paths.Where(AudioDecode.CanDecode).ToList(),
            paths.Where(ModBank.CanOpen).ToList(),
            paths.Any(Directory.Exists));

        return _dragCarried;
    }

    /// <summary>
    /// Whether the pointer is over a given control. The events bubble from whatever is
    /// under it, which for the list is a cell or a row rather than the grid itself, and
    /// for the pane is whichever of its three parts the pointer happens to be on.
    /// </summary>
    private static bool Over(DragEventArgs e, Visual target)
    {
        for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
            if (ReferenceEquals(v, target)) return true;
        return false;
    }

    /// <summary>Whether the pointer is over the sound list, which is what makes a drop
    /// a replacement for the rows it has selected.</summary>
    private bool OverTheList(DragEventArgs e) => Over(e, Grid_);

    private string DropHint(DragEventArgs e, Carried carried)
    {
        if (!carried.Anything)
            return "that is not audio, a .bnk or a .pak.";

        if (carried.Banks.Count > 0)
            return $"drop to open {Path.GetFileName(carried.Banks[0])}" +
                   (carried.Banks.Count > 1 ? ", one .bnk or .pak at a time." : ".");

        // Counted off the grid rather than through SelectedRows: this runs on every
        // pointer move, and building a list of four hundred rows each time to learn
        // how many there are is work nobody asked for.
        var selected = OverTheList(e) ? Grid_.SelectedItems?.Count ?? 0 : 0;

        // The same test the drop itself makes, so this line cannot promise a
        // replacement the drop will then decline to carry out.
        if (carried.Paths.Count == 1 && carried.Audio.Count == 1 && selected > 0)
        {
            var one = Path.GetFileName(carried.Audio[0]);
            return selected == 1
                ? $"drop to replace the selected sound with {one}."
                : $"drop to replace all {selected} selected sounds with {one}.";
        }

        // How many are in a folder is not known until Stage walks it, so a drag
        // carrying one cannot honestly be counted.
        if (carried.Folders)
            return "drop to stage the audio in there, named {media id}-note.wem.";

        return carried.Audio.Count == 1
            ? $"drop to stage {Path.GetFileName(carried.Audio[0])} against the media id in its name."
            : $"drop to stage {carried.Audio.Count} files against the media ids in their names.";
    }

    // ---- a bank or a pak ----------------------------------------------------

    /// <summary>
    /// Open a dropped .bnk or .pak, which is the Mods tab's job. Only the first one:
    /// opening a pak asks which banks to take out of it, and a queue of those dialogs
    /// is not what someone dropping a handful of files is asking for.
    /// </summary>
    private async Task DropOpen(List<string> openable)
    {
        // Same refusal the Mods tab makes, for the same reason: with no game mounted
        // every row in the mod reads as NEW, which is the one wrong answer that looks
        // right.
        if (Session?.Provider is null)
        {
            Say("load the game first, a mod's rows are read against the game's own .bnk files.");
            return;
        }

        // The empty lists are the report the Mods tab collects when it opens several
        // files at once and has to sum them up at the end. One file says everything
        // about itself as it goes, which is why that tab discards them too.
        await ModOpenOneAsync(openable[0], [], []);

        // Appended rather than said first: opening writes its own report over anything
        // said beforehand, and a warning nobody can still read has not been given.
        if (openable.Count > 1)
            Say($"{Status.Text}  Only {Path.GetFileName(openable[0])} was opened; " +
                "drop one .bnk or .pak at a time.");
    }
}
