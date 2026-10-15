using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace MRAudioKit.UI;

/// <summary>
/// The Build tab: stage custom audio against media ids, set how loud each sound is,
/// and write a repak-ready folder.
///
/// Everything here reads from one list of staged files. Staging is what a build is
/// built FROM, which is why picking a skin is not a precondition: a staged file names
/// its own media id and the id names its .bnk, so a folder of named wems spanning
/// three characters is a complete instruction on its own.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The custom audio waiting to be built in. Sorted by media id so the same drop
    /// always reads the same way.
    ///
    /// Observable because the pane down the right draws straight from it. A plain list
    /// would need every one of the eight places below to remember to say so afterwards,
    /// and the one that forgot would leave the pane quietly showing yesterday's staging
    /// while the build used today's.
    /// </summary>
    private readonly ObservableCollection<PendingWem> _pending = [];

    /// <summary>
    /// The level every sound follows unless it was set by hand. Session-only for now:
    /// the old window kept it on the project, and projects are not ported yet, so
    /// closing the app forgets it.
    /// </summary>
    private double _bulkVolume = 1.0;

    /// <summary>stem -> the game's own path for that .bnk. See BuildGamePathForStem.</summary>
    private Dictionary<string, string> _buildBankPathByStem;

    /// <summary>
    /// The provider that map was read from. A remount is a different game -- another
    /// paks folder, or the same one re-read in another language -- and the paths from
    /// the old one are then wrong rather than merely old.
    /// </summary>
    private object _buildBankPathFrom;

    private IEnumerable<ToolButton> BuildTools()
    {
        // Staging marks the rows it lands on, but a skin read AFTER something was
        // staged has never been through that: its rows are built fresh by the tree and
        // nothing since has compared them with what is staged. Coming to this tab is
        // the one moment this file gets control, so it is where the two are brought
        // back in line -- otherwise a folder staged by filename builds fine while the
        // list says "0 staged" and shows nothing green.
        if (MarkStagedRows() > 0) BuildRedraw();

        return
        [
            new("Build mod", () => _ = BuildModAsync(), Key: true,
                Hint: "Rebuild every .bnk that embeds a staged or muted sound and package the mod for ~mods: " +
                      "a .pak, or, with new voice lines, the .utoc/.ucas/.pak bundle they need."),
            new("Install to ~mods", () => _ = InstallLastBuildAsync(),
                Hint: "Copy what the last build made into the game's ~mods folder, in a folder named after the project."),
            new("Staging", null, Hint: "Put audio on sounds, or take it off again.", Menu:
            [
                new("Set replacement…", () => _ = SetReplacementAsync(),
                    Hint: "Stage audio. With rows selected it goes to those sounds; otherwise the filenames say where it goes."),
                new("Clear staged…", () => _ = ClearStagedAsync(),
                    Hint: "Unstage. Everything by default; with staged rows selected it offers those alone."),
            ]),
            new("Volume", null, Hint: "Levels for sounds, and builds at several levels at once.", Menu:
            [
                new("Set volume…", () => _ = SetVolumeAsync(),
                    Hint: "A level for the selected sounds, kept against them so a bulk change leaves them alone."),
                new("Change all volumes…", () => _ = SetAllVolumesAsync(),
                    Hint: "One level for every sound in the open skin -- not just the filtered ones -- except those set by hand."),
                ToolButton.Separator,
                new("Build at these volumes…", () => _ = BuildVolumesAsync(),
                    Hint: "One .pak per level you give (1.0; 0.8; 0.6 ...), each named after its level. " +
                          "Sounds you set a level on by hand keep it in every one."),
            ]),
            new("Output", null, Hint: "How Build mod splits and names what it writes. Both are off unless ticked.", Menu:
            [
                new("One .pak per character", ToggleSplit, Checked: Config.SplitModsByGroup,
                    Hint: "Build mod writes one mod per character (and per map, menu or music for the sounds no character " +
                          "owns), each in a folder of its name, instead of one mod for everything."),
                new("Pak name list…", () => _ = PickPakNameListAsync(), Checked: HasPakNameList,
                    Hint: "Sort Build mod's output by a SoundKit 0-PAK-NAME-LIST.txt: each rule <search>:<folder>:<pakname> " +
                          "puts the banks whose name contains <search> (and a loose wem by its id, or by the bank that plays it) " +
                          "into <folder>\\<pakname>.pak. What no rule takes goes where it would anyway. " +
                          (HasPakNameList ? $"Now: {PakNameListPath}" : "Off.")),
            ]),
        ];
    }

    // ---- staging ------------------------------------------------------------

    /// <summary>
    /// Stage audio for the selected sounds, or by filename when nothing is selected.
    ///
    /// Two jobs, because they are the same act: with rows picked the author is saying
    /// "this file, for these sounds" and the filename is irrelevant; with nothing
    /// picked the filenames are the assignment, which is what makes a folder someone
    /// named months ago stage itself.
    /// </summary>
    private async Task SetReplacementAsync()
    {
        var rows = SelectedRows;
        var title = rows.Count == 1 ? $"Replacement for {rows[0].Display}"
                  : rows.Count > 1 ? $"Replacement for {rows.Count} selected sounds"
                  : "Audio to stage  ({media id}-note.wem)";

        // The formats come from the decoder rather than being typed out again: a dialog
        // that offers a format the staging path then rejects, or hides one it accepts,
        // is a lie either way.
        var picked = await PickFiles(title, "Audio",
                                     [.. AudioDecode.Extensions.Select(e => "*" + e)]);
        if (picked.Count == 0) return;

        // One file onto a selection is the explicit assignment. More than one file means
        // the author is staging a batch, and a batch can only be assigned by name.
        if (rows.Count > 0 && picked.Count == 1)
        {
            await StageOnto(picked[0], rows);
            return;
        }

        // Said as part of the outcome rather than before it: a status line written here
        // is overwritten by the staging report a moment later, so on its own this
        // warning is one nobody can read.
        await Stage(picked, rows.Count > 0
            ? $"{picked.Count} files picked, so the filenames decided -- the selection was ignored."
            : null);
    }

    /// <summary>
    /// Assign one file to the given rows, whatever the file is called.
    ///
    /// Converted once and reused for every row: the same audio onto fifty sounds would
    /// otherwise be fifty identical encodes, each a few hundred milliseconds.
    /// </summary>
    private async Task StageOnto(string path, List<SoundRow> rows)
    {
        var ids = rows.DistinctBy(r => r.MediaId).ToList();
        var name = Path.GetFileName(path);

        // One file onto many sounds is a big thing to do by accident -- the selection
        // can be four hundred rows -- and it is the one staging act whose scale is not
        // visible in what was picked. Asked once, before any work.
        if (ids.Count > 1 &&
            !await Ask("Replace many with one",
                       $"Replace all {ids.Count} selected sound(s) with {name}?" +
                       Environment.NewLine + Environment.NewLine +
                       "Every one of them will play this same audio.",
                       "Replace them", "Cancel"))
        {
            Say("not staged.");
            return;
        }

        var data = await Working($"reading {name}", () =>
        {
            var bytes = PendingWem.ToWemBytes(path, out var why, out var converted);
            return (bytes, why, converted);
        });
        if (data.bytes is null)
        {
            // A null reason means the read threw rather than refused, and Working has
            // already put the exception's own words on the status line. Replacing them
            // with "could not read it" would be losing the only useful part.
            if (data.why is not null) Say(data.why);
            return;
        }

        // After conversion this is Vorbis in almost every case. It can still fail to be
        // one -- a 24-bit PCM wem is left alone rather than guessed at -- and a codec
        // this game's banks do not declare is worth stopping for.
        var info = BnkBuilder.Inspect(data.bytes);
        if (info.FormatTag != 0xFFFF)
        {
            var go = await Ask("Codec mismatch",
                $"These slots declare {rows[0].Codec}.\n" +
                $"Your file is {info.Codec} ({info.SampleRate} Hz, {info.Channels}ch), and could not be converted.\n\n" +
                "Mismatched codecs usually play as silence or noise. Use it anyway?",
                "Use it", "Cancel");
            if (!go) { Say("not staged."); return; }
        }

        // A music slot states its own clip length, and no build can change that figure:
        // the hierarchy is copied through byte for byte, which is what keeps the rest of
        // the bank intact. Asked while the audio is being chosen, because that is when
        // another clip can still be picked; the build log says it again at the end.
        var seconds = BnkBuilder.Seconds(data.bytes);
        var retimed = ids.Where(r => MusicIndex.Disagrees(r.Applied?.ClipSeconds, seconds)).ToList();
        if (retimed.Count > 0)
        {
            var timed = retimed[0].Applied;
            var go = await Ask("The .bnk times this slot",
                (retimed.Count == 1 ? $"{retimed[0].Display}: "
                                    : $"{retimed.Count} of these slots are timed by the .bnk. " +
                                      $"The first, {retimed[0].Display}: ") +
                MusicIndex.LengthNote(timed.ClipSeconds, timed.SegmentSeconds, timed.ClipTrimmed,
                                      seconds ?? 0) + "\n\n" +
                "Audio of the length the bank states leaves every timing figure in it true.",
                "Stage it anyway", "Cancel");
            if (!go) { Say("not staged."); return; }
        }

        foreach (var row in ids)
        {
            foreach (var already in _pending.Where(x => x.MediaId == row.MediaId).ToList())
                _pending.Remove(already);
            _pending.Add(PendingWem.ForMedia(row.MediaId, path, data.bytes, row.Display, data.converted));
        }

        Reorder();
        MarkStagedRows();
        BuildRedraw();
        Say($"{name} staged for {ids.Count} sound(s) -- encoded once, {data.bytes.Length:N0} B. " +
            $"{_pending.Count} staged in total. {StagedScope()}");
    }

    /// <summary>
    /// Files or folders. Names decide the assignment: {MediaID}-{note}.{ext}, with
    /// everything from the first hyphen kept only as the author's own reminder.
    /// Anything that is not already a wem is converted on the way in.
    /// </summary>
    /// <param name="note">
    /// Something the caller needs said about this staging, carried through to the
    /// report at the end. A status line written before this runs would be overwritten
    /// by it within a moment, so anything said beforehand is said to nobody.
    /// </param>
    private async Task Stage(IEnumerable<string> paths, string note = null)
    {
        var files = AudioUnder(paths);
        if (files.Count == 0) { Say("nothing to stage -- no audio files there."); return; }

        // Converting an mp3 means decoding and re-encoding it, which is far too slow to
        // do on the drawing thread for a folder full of them.
        var results = await Working($"staging {files.Count} file(s)", () => files
            .Select(f => { var item = PendingWem.FromFile(f, out var why); return (item, why); })
            .ToList());
        if (results is null) return;

        var rejected = results.Where(r => r.item is null).Select(r => r.why).ToList();
        var (added, replaced) = Absorb(results.Where(r => r.item is not null).Select(r => r.item));

        Reorder();
        MarkStagedRows();
        BuildRedraw();

        var mismatched = _pending.Count(p => p.FormatTag != 0xFFFF);
        var convertedNow = results.Count(r => !string.IsNullOrEmpty(r.item?.Converted));
        Say($"{_pending.Count} staged ({added} new, {replaced} updated)" +
            (convertedNow > 0 ? $", {convertedNow} converted to Vorbis" : "") +
            (rejected.Count > 0 ? $", {rejected.Count} rejected" : "") +
            (mismatched > 0 ? $", {mismatched} not VORBIS" : "") + ". " +
            (note is null ? "" : note + " ") + StagedScope());

        // One summary rather than a dialog per file -- a 50-file drop should not be 50
        // dialogs, but a silent codec mismatch is the exact failure worth naming, and a
        // rejected file is one the author thinks they staged.
        var problems = new List<string>(rejected);
        problems.AddRange(_pending.Where(p => p.FormatTag != 0xFFFF)
            .Select(p => $"{p.FileName} -- {p.Codec}; this game's .bnk files declare VORBIS"));

        // The same length check the build makes, said once for the whole drop. A folder of
        // music files is exactly where this happens, and a filename carries no hint of it.
        foreach (var row in Sounds?.Rows ?? [])
        {
            var staged = _pending.FirstOrDefault(p => p.MediaId == row.MediaId);
            if (staged is null || !MusicIndex.Disagrees(row.Applied?.ClipSeconds, staged.Seconds))
                continue;
            // A colon and not the two hyphens the line above uses: LengthNote opens with a
            // lower-case clause, so the filename introduces it, and a reader sees a dash
            // rendered as two hyphens rather than the punctuation that was meant.
            problems.Add($"{staged.FileName}: " +
                         MusicIndex.LengthNote(row.Applied.ClipSeconds, row.Applied.SegmentSeconds,
                                               row.Applied.ClipTrimmed, staged.Seconds ?? 0));
        }
        if (problems.Count > 0)
            await Tell("Check these files",
                string.Join(Environment.NewLine, problems.Take(30)) +
                (problems.Count > 30 ? $"{Environment.NewLine}… and {problems.Count - 30} more" : ""));
    }

    /// <summary>
    /// Every audio file a set of dropped or picked paths comes to, in a stable order.
    /// Folders are expanded here rather than at the call site so the drop target can
    /// hand this whatever was let go of.
    /// </summary>
    private static List<string> AudioUnder(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                                        .Where(AudioDecode.CanDecode));
            else if (AudioDecode.CanDecode(p))
                files.Add(p);
        }
        return [.. files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Put converted files into the staged list, one per media id: staging the same id
    /// twice is an author correcting themselves, not two replacements for one sound.
    /// </summary>
    private (int Added, int Replaced) Absorb(IEnumerable<PendingWem> items)
    {
        var added = 0; var replaced = 0;
        foreach (var item in items)
        {
            var existing = _pending.FirstOrDefault(x => x.MediaId == item.MediaId);
            if (existing is not null) { _pending.Remove(existing); replaced++; } else added++;
            _pending.Add(item);
        }
        return (added, replaced);
    }

    /// <summary>
    /// Stage with nothing awaited, so a render can be taken of a pane that has something
    /// in it. Same assignment rule as <see cref="Stage"/> and the same list, without the
    /// progress line or the summary dialog, both of which need a thread that is free to
    /// answer. See <see cref="LoadGameSync"/> for why the harness cannot await.
    /// </summary>
    /// <returns>How many files are staged in total afterwards.</returns>
    public int StageSync(IEnumerable<string> paths)
    {
        Absorb(AudioUnder(paths)
               .Select(f => PendingWem.FromFile(f, out _))
               .Where(item => item is not null));
        Reorder();
        MarkStagedRows();
        BuildRedraw();
        return _pending.Count;
    }

    /// <summary>
    /// Put the staged list back in media id order.
    ///
    /// One item moved at a time rather than cleared and refilled. A Clear on an observable
    /// collection is a Reset, the grid answers a Reset by throwing away every row it had
    /// realised, and the pane's SelectedItems goes with them -- so staging a file that
    /// sorted ahead of the row someone had picked silently took their selection away, and
    /// the Remove button they were reaching for had nothing left to act on. That undid, one
    /// call earlier, the whole reason <see cref="ShowPending"/> puts the selection back.
    /// Moving is better for the drawing too: a Reset shows the pane empty for a frame on
    /// the way through, which on a folder drop is a visible flicker.
    /// </summary>
    private void Reorder()
    {
        var sorted = _pending.OrderBy(p => p.MediaId).ToList();

        // Cheap enough to skip when it would change nothing, and worth skipping: every
        // move below is a notification the pane redraws on, and a staging that arrived in
        // order should cost nothing at all.
        if (sorted.SequenceEqual(_pending)) return;

        // Selection sort, because each step is one notification and the grid is watching:
        // walking a position at a time means the collection is never in a state where an
        // item is missing from it.
        for (var want = 0; want < sorted.Count; want++)
        {
            var at = _pending.IndexOf(sorted[want]);
            if (at != want) _pending.Move(at, want);
        }
    }

    /// <summary>
    /// Tint the rows that have audio waiting on them. The list reads ReplacementPath
    /// when it draws a row, so this is what makes staging visible at all.
    /// </summary>
    /// <returns>
    /// How many rows changed. A row is only re-drawn as it is realised, so a redraw is
    /// the only way to see this -- and a redraw costs the selection, which is worth
    /// nothing when there was nothing to show.
    /// </returns>
    private int MarkStagedRows()
    {
        if (Sounds is null) return 0;
        var byId = _pending.ToDictionary(p => p.MediaId, p => p.FullPath);
        var changed = 0;
        foreach (var r in Sounds.Rows)
        {
            var want = byId.GetValueOrDefault(r.MediaId);
            if (want == r.ReplacementPath) continue;
            r.ReplacementPath = want;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Redraw the list without throwing the selection away.
    ///
    /// A row's fill is set as the row is realised, so the only way to re-colour one is
    /// to hand the grid its items again -- which clears what was selected. WPF's
    /// Items.Refresh kept it, and losing it here would mean re-picking fifty rows
    /// between one volume change and the next.
    /// </summary>
    private void BuildRedraw()
    {
        var keep = SelectedRows;
        Refresh();
        if (keep.Count == 0 || Grid_.SelectedItems is not { } back) return;

        // The grid throws ArgumentException for a row the new list does not hold, and a
        // redraw must never be what fails the thing it was called to show. Losing the
        // selection is what happened before this method existed, so it is the right
        // thing to fall back to.
        try
        {
            foreach (var r in keep) back.Add(r);
        }
        catch (ArgumentException) { }
    }

    /// <summary>
    /// What the staged ids actually reach: how many .bnk files, and how many ids no
    /// bank embeds. The second number is the surprising one -- those ship as loose
    /// Media/ wems rather than inside the bank.
    /// </summary>
    private string StagedScope()
    {
        if (_pending.Count == 0 || Session?.Provider is null) return "";
        if (!Session.BankIndexReady) return "still reading .bnk files.";

        var banks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var homeless = 0;
        foreach (var p in _pending)
        {
            var found = Session.BanksForMedia(p.MediaId);
            if (found.Count == 0) homeless++;
            foreach (var b in found) banks.Add(b);
        }
        return $"{banks.Count} .bnk(s) affected" +
               (homeless > 0 ? $", {homeless} id(s) in none of them (they ship loose)" : "") + ".";
    }

    /// <summary>
    /// Unstage: everything, or just the sounds picked in the list.
    ///
    /// The old button was wired to the drop pane's "Clear all" and wiped every staged
    /// file while sitting next to "Set replacement…", which reads as "clear this row".
    /// Hence the name, and hence asking first once there is more than one file's work
    /// in there to lose.
    ///
    /// The other scope is the row. The pane's own Remove is the per-item way back out,
    /// but it needs the file to be findable in the pane; taking one back from the row it
    /// landed on is the other direction, and it is the direction someone who has just
    /// realised they staged onto the wrong sound is looking in.
    /// </summary>
    private async Task ClearStagedAsync()
    {
        if (_pending.Count == 0) { Say("nothing is staged."); return; }

        var picked = SelectedRows.Select(r => r.MediaId).ToHashSet();
        var pickedStaged = _pending.Count(p => picked.Contains(p.MediaId));

        if (pickedStaged > 0 && pickedStaged < _pending.Count)
        {
            var answer = await AskScope("Clear staged audio",
                $"{_pending.Count} files are staged; {pickedStaged} of them are on the " +
                "sounds you have selected." + Environment.NewLine + Environment.NewLine +
                "The files on disk are untouched either way; only the assignments go.",
                "Unstage all", $"Unstage the {pickedStaged} selected");
            if (answer < 0) { Say("kept."); return; }
            if (answer == 1)
            {
                foreach (var p in _pending.Where(p => picked.Contains(p.MediaId)).ToList())
                    _pending.Remove(p);
                MarkStagedRows();
                BuildRedraw();
                Say($"{pickedStaged} replacement(s) cleared -- {_pending.Count} still staged. {StagedScope()}");
                return;
            }
            ClearAllStagedNow();
            return;
        }

        await ClearAllStagedAsync();
    }

    /// <summary>
    /// Unstage everything, asking first once there is more than one file's work in there
    /// to lose. The pane's "Clear all" and the toolbar's all-scope answer are the same
    /// act, so they are the same method: a second copy of the question is a second place
    /// for the threshold and the wording to drift.
    /// </summary>
    private async Task ClearAllStagedAsync()
    {
        if (_pending.Count == 0) { Say("nothing is staged."); return; }

        if (_pending.Count > 1 &&
            !await Ask("Clear staged audio",
                       $"{_pending.Count} files are staged. Unstage all of them?\n\n" +
                       "The files on disk are untouched; only the assignments go.",
                       "Unstage all", "Keep them"))
        {
            Say("kept.");
            return;
        }

        ClearAllStagedNow();
    }

    private void ClearAllStagedNow()
    {
        var n = _pending.Count;
        _pending.Clear();
        MarkStagedRows();
        BuildRedraw();
        Say($"{n} replacement(s) cleared.");
    }

    // ---- the build ----------------------------------------------------------

    /// <summary>
    /// Rebuild every bank that embeds a staged or muted media, and drop streamed
    /// replacements as loose wems. Output mirrors the pak layout so the folder can go
    /// straight to repak, rather than a flat bucket the author has to re-sort.
    /// </summary>
    private async Task BuildModAsync()
    {
        if (Session?.Provider is null) { Say("load the game first."); return; }
        if (await BuildOnModRefused()) return;

        // New voice lines' audio is staged like any sound's, but it belongs to no bank yet:
        // it goes to BuildVoiceLines after the regular build, never into it, where the
        // checks below would rightly find its ids in no .bnk at all.
        var lineIds = _project.VoiceLines.Select(l => l.MediaId).ToHashSet();
        var hasLines = _project.VoiceLines.Count > 0;
        var regular = _pending.Where(p => !lineIds.Contains(p.MediaId)).ToList();

        // A mute is a build instruction, not a test-build one: silencing a line you do
        // not want is a thing people ship.
        var mutedIds = Sounds?.Rows.Where(r => r.Muted).Select(r => r.MediaId).ToHashSet() ?? [];

        // Picking a skin is only needed when there is nothing staged to build FROM. A
        // staged file carries its own media id and the id says which .bnk it belongs in,
        // so a folder of named wems may even span characters -- which no single skin
        // selection could express.
        if (regular.Count == 0 && mutedIds.Count == 0 && !hasLines)
        {
            Say(PickedSkin is null
                ? "nothing to build -- stage audio with \"Set replacement…\", or pick a skin and mute something (right-click > Mute)."
                : "nothing to build -- stage audio with \"Set replacement…\", or mute a sound (right-click > Mute).");
            return;
        }
        if (regular.Count > 0 && PickedSkin is null && !Session.BankIndexReady)
        {
            Say("still reading .bnk files -- give it a moment, or pick a skin.");
            return;
        }

        var outRoot = BuildFolderForShot ?? await PickFolder("Output folder for the mod");
        if (outRoot is null) return;
        _lastBuilt.Clear();
        _lastBuiltRoot = outRoot;

        // The banks are written into a folder of this build's own, and the mod is made from
        // them: a .pak when only files change, the IoStore bundle when new voice lines add cooked
        // assets. The output folder gets the mod and its log, nothing loose to repack by hand.
        var work = Path.Combine(Path.GetTempPath(), "XzoundWave", "build-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(outRoot);
            await BuildModInto(outRoot, work, regular, mutedIds, hasLines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Said, not swallowed: the button discards this task, so an escape would leave the
            // status line on "building..." for good.
            Say("build failed: " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task BuildModInto(string outRoot, string work, List<PendingWem> regular, HashSet<uint> mutedIds, bool hasLines)
    {
        // Only new lines: there is no regular build to make first.
        if (regular.Count == 0 && mutedIds.Count == 0)
        {
            if (Config.SplitModsByGroup || HasPakNameList) await PackGroups(outRoot, work, [], "");
            else await BuildVoiceLines(outRoot, work, []);
            return;
        }

        // Which banks actually embed something staged? Ask the global index rather than
        // assuming the picked skin -- a mod can span skins, and a silently unpatched
        // bank is the worst possible outcome.
        var bankPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in regular)
            foreach (var bp in Session.BanksForMedia(p.MediaId)) bankPaths.Add(bp);
        // Muted sounds need their .bnk rebuilt too, even with nothing staged.
        foreach (var id in mutedIds)
            foreach (var bp in Session.BanksForMedia(id)) bankPaths.Add(bp);
        // A loaded mod can hold ids the shipped game does not.
        foreach (var bp in BuildModBankPaths(regular.Select(x => x.MediaId).Concat(mutedIds)))
            bankPaths.Add(bp);
        var skin = PickedSkin;
        if (!Session.BankIndexReady && skin is not null)
            foreach (var b in skin.Banks) bankPaths.Add(b.Path);

        // Nothing to rebuild is not the same as nothing to build. A medium the game streams
        // has no bank entry to replace and ships as a loose file instead, which the builder
        // has always written and which is the whole mod for 575 of the 656 music rows: this
        // refused every one of them once the bank index had finished, with a message about
        // filenames that were correct. The question the check is for is whether the game
        // knows the id at all, so it is asked that way.
        // Also a sound the game streams from a file it does not ship (silent in a match): staging
        // one gives it a file, written where the game looks. Asked off this thread, since the
        // first question reads every bank once to learn which Sound streams what.
        // A mute counts: silencing a sound no bank holds writes its loose file.
        var wanted = regular.Select(p => p.MediaId).Concat(mutedIds).ToList();
        var streamedOnly = bankPaths.Count == 0 && !wanted.Any(id => Session.LooseMedia(id) is not null)
            && await Working("looking for the sounds that stream these", () =>
                   regular.Any(p => Session.StreamingBank(p.MediaId) is not null));
        if (bankPaths.Count == 0 && !wanted.Any(id => Session.LooseMedia(id) is not null) && !streamedOnly)
        {
            Say("none of those media ids appear in any .bnk -- check the filenames.");
            return;
        }

        // 14% of this game's media are embedded in more than one bank -- one shared SFX
        // sits in 93 of them. Rebuilding all of them is CORRECT (otherwise the sound only
        // changes for some characters) but can turn a one-line edit into a huge mod, so
        // it is the author's call rather than a silent decision, and it stays a question
        // however many times they have answered it before.
        // Without a skin there is no "inside" to be outside of: the staged ids define
        // the scope by themselves, so the question does not arise.
        var skinBanks = skin?.Banks.Select(b => b.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        List<string> outside = skin is null ? [] : bankPaths.Where(p => !skinBanks.Contains(p)).ToList();
        var scopeNote = skin is null
            ? $"scope: {bankPaths.Count} .bnk(s), from the staged media ids."
            : "";

        if (outside.Count > 0)
        {
            // Muted ids belong in this list as much as staged ones: a mute reaches every
            // bank the media sits in, and a build with nothing staged at all can be the
            // one that asks this question. Listing only the staged ids left the dialog
            // naming a number and then showing nothing under it.
            var shared = regular.Select(p => p.MediaId).Concat(mutedIds).Distinct()
                .Select(id => (Id: id, In: Session.BanksForMedia(id).Count))
                .Where(x => x.In > 1)
                .OrderByDescending(x => x.In)
                .Select(x => $"  {x.Id} in {x.In} .bnk files")
                .Take(10).ToList();

            var inside = bankPaths.Count - outside.Count;
            var answer = await AskScope("This media is shared",
                $"{outside.Count} .bnk(s) outside {skin.SkinId} also embed the media you are changing:" +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, shared) + Environment.NewLine + Environment.NewLine +
                "Rebuild all of them and the sound changes everywhere it is used." + Environment.NewLine +
                $"Keep to this skin and only its {inside} .bnk(s) change, while the other " +
                $"{outside.Count} keep the game's audio.",
                "Rebuild all", "This skin only");

            if (answer < 0) { Say("build cancelled."); return; }
            if (answer == 1) bankPaths.RemoveWhere(p => !skinBanks.Contains(p));
            scopeNote = answer == 0
                ? $"scope: every .bnk containing a staged media ({bankPaths.Count})."
                : $"scope: this skin only ({bankPaths.Count}); {outside.Count} other .bnk(s) left stale.";
        }

        // Prefetch mode is off: the whole clip goes into the bank, which is what the
        // community ships and what is known to work in game. The old window exposed the
        // other mode as a checkbox; nothing in this toolbar sets it yet.
        //
        // Both lists can name the same media, and the builder resolves it one way: a
        // staged file wins over a mute, because staging audio onto a sound you silenced
        // earlier is plainly a change of mind. Nothing here has to sort them out first.
        var staged = regular;
        var built = await Working(bankPaths.Count > 0 ? $"building {bankPaths.Count} .bnk(s)"
                                                     : $"writing {regular.Count} audio file(s)", () =>
            ModBuilder.Build(Session, skin, staged, work, bankPaths,
                             prefetchMode: false, muted: mutedIds, baseBanks: BuildModBase()));
        if (built is null) return;   // Working has already said why
        if (File.Exists(Path.Combine(work, "build-log.txt")))
            File.Copy(Path.Combine(work, "build-log.txt"), Path.Combine(outRoot, "build-log.txt"), true);

        var summary = (built.Muted > 0 ? $"{built.Muted} muted. " : "") +
                      (built.Retimed > 0
                          ? $"{built.Retimed} clip(s) are not the length their .bnk states, which its timing " +
                            "still describes. " : "") +
                      scopeNote + " Details in build-log.txt.";
        if (Config.SplitModsByGroup || HasPakNameList)
        {
            await PackGroups(outRoot, work, built.Outputs, summary);
            return;
        }

        // The new lines go on top of what was just written, and the bundle they need
        // carries all of it.
        if (hasLines)
        {
            await BuildVoiceLines(outRoot, work, built.Outputs);
            return;
        }

        var outputBase = Path.Combine(outRoot, ModBaseName());
        var packed = await Working("packing the .pak", () => ModPackager.PackagePak(work, built.Outputs, outputBase));
        if (!packed.Ok)
        {
            if (packed.Message is not null) Say("could not pack the mod: " + packed.Message);
            return;
        }
        // A bundle an earlier build left under the same name would load its .utoc beside this
        // .pak; this mod is the .pak alone.
        var stale = new[] { ".utoc", ".ucas" }.Where(e => File.Exists(outputBase + e)).ToList();
        foreach (var e in stale) File.Delete(outputBase + e);
        _lastBuilt.Add(outputBase + ".pak");

        Say($"built {built.Banks} .bnk(s)" + (built.Loose > 0 ? $" and {built.Loose} audio file(s)" : "") +
            $" into {Path.GetFileName(outputBase)}.pak in {outRoot} -- copy it into ~mods, or Install to ~mods. " +
            (stale.Count > 0 ? $"(Removed the {string.Join("/", stale)} an earlier build left under that name.) " : "") +
            summary);
    }

    // ---- one mod per group ----------------------------------------------------------------

    /// <summary>
    /// What a built file is part of, for a build split into one mod per group: a character's
    /// banks go under the character's name, a bank no character owns (a map's ambience, the
    /// menus) under its own, and a loose Media/ file under the bank whose Sound streams it --
    /// a medium no Sound plays is a music track's. Runs off the drawing thread: the first loose
    /// file reads every bank once to learn which streams what.
    /// </summary>
    private string GroupOf(string relPath)
    {
        var name = Path.GetFileNameWithoutExtension(relPath);
        if (relPath.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(name, out var id) && (Session.StreamingBank(id) ?? Session.MusicBank(id)) is { } bank
                ? GroupOf(bank) : "Music";
        var skin = System.Text.RegularExpressions.Regex.Matches(name, @"\d{7}").LastOrDefault()?.Value;
        if (skin is not null) return HeroGroup(skin[..4]);
        var bare = name.StartsWith("bnk_", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
        // The music bank goes with the music files it streams, which group as "Music" above.
        if (bare.Split('_')[0] is var first && (first.Equals("music", StringComparison.OrdinalIgnoreCase) ||
                                                first.Equals("mus", StringComparison.OrdinalIgnoreCase)))
            return "Music";
        return ModSafeName(bare);
    }

    private string HeroGroup(string heroId)
    {
        var name = Session.HeroName(heroId);
        return ModSafeName(name == heroId ? heroId : name).Replace(' ', '_');
    }

    /// <summary>
    /// The built files, and the project's voice lines, as one mod per group, each in a folder of
    /// the group's name: the IoStore bundle for a character with new lines (it carries that
    /// character's swaps too, so no bank is in two mods), a .pak for every other group.
    /// </summary>
    private async Task PackGroups(string outRoot, string work, IReadOnlyCollection<string> outputs, string summary)
    {
        var split = Config.SplitModsByGroup;
        var list = ReadPakNameList();
        var sets = VoiceSetsOrNull() ?? [];
        string LineHero(VoiceLine l) => sets.FirstOrDefault(s => s.Key == l.Set) is { } set ? HeroGroup(set.HeroId) : "Unknown";
        // A mod is a folder under the output ("" for the output itself) and a pak name.
        (string Folder, string Base) Home(string group) => split ? (group, ModBaseName(group)) : ("", ModBaseName());
        var lineHeroes = _project.VoiceLines.Select(LineHero).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lineGroups = _project.VoiceLines.GroupBy(l => Home(LineHero(l)))
                                 .ToDictionary(g => g.Key, g => (IReadOnlyList<VoiceLine>)g.ToList());
        var placedByList = 0;
        var fileGroups = await Working("sorting the mod into groups", () => outputs.GroupBy(rel =>
        {
            var group = GroupOf(rel);
            // A character with new voice lines keeps its banks in its bundle: the bundle writes
            // that character's voice bank too, and a bank in two mods is one of them lost.
            if (lineHeroes.Contains(group)) return Home(group);
            if (list?.Match(rel, id => Session.PlayingBank(id)) is { } rule) { Interlocked.Increment(ref placedByList); return (Folder: rule.Folder, Base: rule.PakName); }
            return Home(group);
        }).ToDictionary(g => g.Key, g => g.ToList()));
        if (fileGroups is null) return;   // Working has already said why

        var made = new List<string>();
        foreach (var key in fileGroups.Keys.Union(lineGroups.Keys).OrderBy(k => k.Folder, StringComparer.OrdinalIgnoreCase)
                                     .ThenBy(k => k.Base, StringComparer.OrdinalIgnoreCase))
        {
            var files = fileGroups.GetValueOrDefault(key) ?? [];
            var outputBase = Path.Combine(outRoot, key.Folder, key.Base);
            var shown = key.Folder.Length == 0 ? key.Base : $"{key.Folder}/{key.Base}";
            if (lineGroups.TryGetValue(key, out var lines))
            {
                if (!await BuildVoiceLines(outRoot, work, files, lines, outputBase)) return;
                made.Add($"{shown}.utoc/.ucas/.pak");
                continue;
            }
            var packed = await Working($"packing {shown}", () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputBase)!);
                var r = ModPackager.PackagePak(work, files, outputBase);
                if (r.Ok) foreach (var e in new[] { ".utoc", ".ucas" }) if (File.Exists(outputBase + e)) File.Delete(outputBase + e);
                return r;
            });
            if (!packed.Ok) { if (packed.Message is not null) Say($"could not pack {shown}: {packed.Message}"); return; }
            _lastBuilt.Add(outputBase + ".pak");
            made.Add($"{shown}.pak");
        }
        Say($"built {made.Count} mod(s) in {outRoot}: {string.Join(", ", made)}" +
            (list is null ? "" : $" ({placedByList} file(s) placed by {Path.GetFileName(list.Path)}" +
                                 (list.Skipped.Count > 0 ? $", {list.Skipped.Count} of its line(s) skipped as malformed" : "") + ")") +
            " -- copy them into ~mods, or Install to ~mods. " + summary);
    }

    private bool HasPakNameList => !string.IsNullOrWhiteSpace(PakNameListPath);

    /// <summary>The list in use: the render harness's own, or the one in the settings.</summary>
    private string PakNameListPath => PakNameListForShot ?? Config.PakNameList;

    /// <summary>The render harness's pak name list, which never reaches the settings file.</summary>
    internal string PakNameListForShot;

    /// <summary>The pak name list Build mod sorts by, or null when none is set or it cannot be read (said once).</summary>
    private PakNameList ReadPakNameList()
    {
        if (!HasPakNameList) return null;
        try { return PakNameList.Read(PakNameListPath); }
        catch (Exception ex) { Say($"the pak name list could not be read ({ex.Message}); building without it."); return null; }
    }

    /// <summary>Pick a SoundKit 0-PAK-NAME-LIST.txt for Build mod to sort by, or stop using the one set.</summary>
    private async Task PickPakNameListAsync()
    {
        if (HasPakNameList &&
            await Ask("Pak name list", $"Build mod sorts its output by {Config.PakNameList}.", "Stop using it", "Pick another"))
        {
            Config.PakNameList = "";
            Config.Save();
            ShowToolbar();
            Say("Build mod no longer uses a pak name list.");
            return;
        }
        var file = await PickFile("A SoundKit 0-PAK-NAME-LIST.txt", "Pak name list", "*.txt");
        if (file is null) return;
        PakNameList list;
        try { list = PakNameList.Read(file); }
        catch (Exception ex) { Say($"could not read {Path.GetFileName(file)}: {ex.Message}"); return; }
        if (list.Rules.Count == 0) { Say($"{Path.GetFileName(file)} has no rules (<search>:<folder>:<pakname>)."); return; }
        Config.PakNameList = file;
        Config.Save();
        ShowToolbar();
        Say($"Build mod now sorts its output by {Path.GetFileName(file)}: {list.Rules.Count} rule(s)" +
            (list.Skipped.Count > 0 ? $", {list.Skipped.Count} line(s) skipped as malformed" : "") + ".");
    }

    private void ToggleSplit()
    {
        Config.SplitModsByGroup = !Config.SplitModsByGroup;
        Config.Save();
        ShowToolbar();
        Say(Config.SplitModsByGroup ? "Build mod now writes one mod per character (and per map, menu, music)."
                                    : "Build mod now writes one mod for everything.");
    }

    // ---- install ----------------------------------------------------------------------------

    /// <summary>The mod files the last Build mod wrote, and the folder it wrote them under.</summary>
    private readonly List<string> _lastBuilt = [];
    private string _lastBuiltRoot;

    /// <summary>Where Install puts them: ~mods inside the game's Paks folder, a folder per project.</summary>
    internal string InstallRootForShot;

    private async Task InstallLastBuildAsync()
    {
        var files = _lastBuilt.Where(File.Exists).ToList();
        if (files.Count == 0) { Say("nothing to install -- Build mod first; Install copies what it made."); return; }
        string target;
        if (InstallRootForShot is not null) target = InstallRootForShot;
        else
        {
            if (string.IsNullOrWhiteSpace(_settings.PaksDir) || !Directory.Exists(_settings.PaksDir))
            { Say("set the game's Paks folder on Setup first."); return; }
            target = Path.Combine(_settings.PaksDir, "~mods", ModBaseName()[..^"_9999999_P".Length]);
            if (!await Ask("Install to ~mods",
                           $"Copy {files.Count} file(s) into {target}?" + Environment.NewLine + Environment.NewLine +
                           "The game loads them the next time it starts. Files of the same name there are replaced.",
                           "Install", "Cancel"))
            { Say("not installed."); return; }
        }
        var copied = await Working($"installing {files.Count} file(s)", () =>
        {
            foreach (var f in files)
            {
                var dest = Path.Combine(target, Path.GetRelativePath(_lastBuiltRoot, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, true);
                // A .pak installed over an earlier bundle of the same name must not load beside its .utoc.
                if (f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && !files.Any(x => x.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)
                                                                                         && Path.ChangeExtension(x, null) == Path.ChangeExtension(f, null)))
                    foreach (var e in new[] { ".utoc", ".ucas" })
                        if (File.Exists(Path.ChangeExtension(dest, e))) File.Delete(Path.ChangeExtension(dest, e));
            }
            return files.Count;
        });
        if (copied == 0) return;
        Say($"installed {copied} file(s) into {target}. Start the game to hear them.");
    }

    /// <summary>The render harness: the rows of the open bank.</summary>
    internal SkinSounds SoundsForShot => Sounds;

    /// <summary>Build mod into a folder, then Install, as the render harness presses them.</summary>
    internal async Task BuildAndInstallForShot(string outRoot, string installRoot, bool split)
    {
        Config.SplitModsByGroup = split;
        BuildFolderForShot = outRoot;
        InstallRootForShot = installRoot;
        await BuildModAsync();
        await InstallLastBuildAsync();
    }

    /// <summary>
    /// Where the render harness builds to: the folder a person would pick, given instead of
    /// asked for, since nobody is there to answer a picker. Null everywhere else.
    /// </summary>
    internal string BuildFolderForShot;

    /// <summary>The Build button, pressed by the render harness into <paramref name="outRoot"/>.</summary>
    internal Task BuildForShot(string outRoot)
    {
        BuildFolderForShot = outRoot;
        return BuildModAsync();
    }

    /// <summary>
    /// The mods standing in front of the .bnk files a build is about to write. Every
    /// builder reads through this, so a build layers onto a loaded mod instead of
    /// throwing its work away.
    /// </summary>
    private BankBase BuildModBase() =>
        !Mods.Any ? BankBase.None
        : new BankBase(Mods.All.Select(e => new KeyValuePair<string, byte[]>(e.Stem, e.Bank.Bytes)));

    /// <summary>
    /// True when the author backed out of building on top of a loaded mod. Worth saying
    /// once: the surprising reading is the other one, where a build quietly ignores the
    /// mod on screen and writes the game's audio instead.
    /// </summary>
    /// <param name="withLoose">
    /// The build reads a loaded mod's loose files too (a volume build scales them). Otherwise
    /// only banks are a base: a loaded loose file is a file of its own, which the build neither
    /// carries nor overwrites unless something is staged on the same sound.
    /// </param>
    private async Task<bool> BuildOnModRefused(bool withLoose = false)
    {
        var loose = withLoose ? Mods.LooseFiles.Where(l => l.Changed).ToList() : [];
        if (Mods.Count == 0 && loose.Count == 0) return false;

        // The literal key is the one the old window stored, so an author who silenced
        // this warning there is not asked again here. Never reuse a key for a different
        // message: someone who silenced the old meaning did not agree to the new one.
        if (Config?.SuppressedWarnings?.Contains("build-uses-a-loaded-mod-as-its-base",
                                                 StringComparer.OrdinalIgnoreCase) == true)
            return false;

        var names = Mods.All.Select(e => e.BankName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Concat(loose.Count > 0
                ? [$"{loose.Count} loose wem(s) from {string.Join(", ", loose.Select(l => l.SourceName).Distinct())}"]
                : []);
        var go = await Ask("Building on a loaded mod",
            "These files come from a mod and will be the base for this build:" +
            Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, names) +
            Environment.NewLine + Environment.NewLine +
            "Whatever that mod changed stays in and your work goes on top. Close the mod " +
            "first to build against the game's original audio instead.",
            "Build on it", "Cancel");
        if (!go) Say("build cancelled.");
        return !go;
    }

    /// <summary>
    /// The .bnk files a loaded mod embeds these ids in. A mod can add a media id the
    /// game has never shipped, and the game's index cannot know about it -- without
    /// this the build would put that audio in a loose file and leave the .bnk that
    /// actually plays it untouched.
    /// </summary>
    private IEnumerable<string> BuildModBankPaths(IEnumerable<uint> ids)
    {
        if (!Mods.Any || Session?.Provider is null) yield break;
        var want = ids.ToHashSet();
        foreach (var entry in Mods.All)
        {
            if (!entry.Sounds.Rows.Any(r => want.Contains(r.MediaId))) continue;
            if (BuildGamePathForStem(entry.Stem) is { } path) yield return path;
        }
    }

    /// <summary>
    /// A mod names a .bnk but not where the game keeps it, and the output has to mirror
    /// the game's layout. Built once per mount.
    ///
    /// Per MOUNT, not once: the old window threw this map away whenever it filled the
    /// tree again, and it was right to. Mounting is what sets both the paks folder and
    /// the language, so a map kept across one would put a rebuilt bank at a path from
    /// the previous game -- or under the previous language, which is the same bank name
    /// in a different folder and so fails silently rather than loudly.
    /// </summary>
    private string BuildGamePathForStem(string stem)
    {
        if (_buildBankPathByStem is null || !ReferenceEquals(_buildBankPathFrom, Session.Provider))
        {
            _buildBankPathFrom = Session.Provider;
            _buildBankPathByStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Session.Provider.Files.Keys)
            {
                if (!path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)) continue;
                var key = Path.GetFileNameWithoutExtension(path);
                // A vo bank exists once per language; keep the active one, the same rule
                // BanksForMedia applies.
                var lang = GameSession.Languages.FirstOrDefault(l =>
                    path.Contains($"/{l}/", StringComparison.OrdinalIgnoreCase));
                if (_buildBankPathByStem.ContainsKey(key) && lang is not null &&
                    !lang.Equals(Session.Language, StringComparison.OrdinalIgnoreCase)) continue;
                _buildBankPathByStem[key] = path;
            }
        }
        return _buildBankPathByStem.GetValueOrDefault(stem);
    }

    // ---- volume -------------------------------------------------------------

    /// <summary>Set the level on the selected sounds, marking them as hand-set.</summary>
    private async Task SetVolumeAsync()
    {
        var rows = SelectedRows;
        if (rows.Count == 0) { Say("select one or more sounds first."); return; }

        var (applied, gain) = await AskMultiplier("Set volume",
            $"Multiplier for {rows.Count} selected sound(s):", rows[0].Volume ?? _bulkVolume,
            $"Empty gives the level back: the sound follows the bulk level ({_bulkVolume:0.0##}x) again.");
        if (!applied) return;

        // An empty box is how a hand-set level is given back: the sound follows the bulk
        // level again, which is what the old window's separate "clear" did.
        foreach (var r in rows) { r.Volume = gain; r.VolumeByHand = gain is not null; }
        RowStateChanged(rows);
        BuildRedraw();
        Say(gain is null
            ? $"{rows.Count} sound(s) back to following the bulk level ({_bulkVolume:0.0##}x)."
            : $"{rows.Count} sound(s) set to {gain:0.0##}x by hand -- a bulk change will now skip them.");
    }

    /// <summary>
    /// Set the level for everything that was not set by hand. This is the "make the
    /// whole mod quieter" control; sounds someone tuned individually keep their own.
    /// </summary>
    private async Task SetAllVolumesAsync()
    {
        if (Sounds is null || Sounds.Rows.Count == 0) { Say("open a skin or a .bnk first."); return; }

        // Every sound in the open skin, not just the ones the filter is showing: this is
        // the level the whole mod is built at, and a filter is about what is on screen.
        var byHand = Sounds.Rows.Count(r => r.VolumeByHand);
        var (applied, gain) = await AskMultiplier("Change all volumes",
            $"Multiplier for all {Sounds.Rows.Count:N0} sounds in the open skin" +
            $"{(byHand > 0 ? $", except the {byHand} set by hand" : "")}:",
            _bulkVolume,
            "Empty is the same as 1.0: everything back to the game's own level.");
        if (!applied) return;

        _bulkVolume = gain ?? 1.0;
        var moved = new List<SoundRow>();
        foreach (var r in Sounds.Rows.Where(r => !r.VolumeByHand))
        {
            // Null rather than 1.0 at the neutral level, so the row goes back to
            // following the bulk setting instead of pinning itself to it.
            r.Volume = Math.Abs(_bulkVolume - 1.0) < 0.001 ? null : _bulkVolume;
            moved.Add(r);
        }
        // The level itself is the project's too, so a reopened project builds at it.
        _project.BulkVolume = _bulkVolume;
        _project.Touch();
        RowStateChanged(moved);
        BuildRedraw();
        Say($"{moved.Count} sound(s) now at {_bulkVolume:0.0##}x" +
            (byHand > 0 ? $"; {byHand} hand-set left alone" : "") + ".");
    }

    /// <summary>
    /// Write banks with every level applied. The output folder carries the bulk level,
    /// so exporting at 1.0, 0.8 and 0.6 gives three folders rather than one overwritten
    /// three times.
    ///
    /// Each sound is scaled from the audio the game shipped, never from the last
    /// result, so setting a level twice does not compound two lossy round trips.
    /// </summary>
    private async Task BuildVolumesAsync()
    {
        if (Session?.Provider is null) { Say("load the game first."); return; }
        if (Sounds is null || Sounds.Rows.Count == 0) { Say("open a skin or a .bnk first."); return; }
        // A volume build scales a loaded mod's loose files as well as its banks, so both are asked about.
        if (LevelsForShot is null && await BuildOnModRefused(withLoose: true)) return;

        // Each level stands in for the bulk level once: what was set by hand keeps its own.
        var bulkLevels = await AskLevels(_bulkVolume);
        if (bulkLevels is null) return;
        var plans = bulkLevels.Select(bulk => (Bulk: bulk, Levels: Sounds.Rows
                .Where(r => Math.Abs(r.EffectiveVolume(bulk) - 1.0) > 0.001)
                .GroupBy(r => r.MediaId)
                .ToDictionary(g => g.Key, g => g.First().EffectiveVolume(bulk))))
            .ToList();
        var nothing = plans.Where(p => p.Levels.Count == 0).Select(p => VolumeTool.Label(p.Bulk)).ToList();
        plans.RemoveAll(p => p.Levels.Count == 0);
        if (plans.Count == 0)
        {
            Say("every sound is at 1.0 at those levels, so there is nothing to write -- " +
                "set a level on a selection, or give a level other than 1.0.");
            return;
        }

        var folder = BuildFolderForShot ?? await PickFolder("Output folder for the volume build");
        if (folder is null) return;
        Directory.CreateDirectory(folder);

        // Muting wins over any level: there is no useful gain to apply to silence.
        var muted = Sounds.Rows.Where(r => r.Muted).Select(r => r.MediaId).ToHashSet();
        var vgm = Config.VgmstreamPath;
        var context = Sounds;
        var baseBanks = BuildModBase();
        // The builder reads the loose files on its own threads; the window's set can change meanwhile.
        var looseNow = Mods.LooseSnapshot();
        var built = new List<string>();
        var legend = new List<VolumeBankBuilder.Row>();
        foreach (var (bulk, levels) in plans)
        {
            var banks = BanksForVolume(levels.Keys.ToList());
            // A sound no bank embeds is scaled as its loose file instead.
            var looseIds = levels.Keys.Concat(muted).Where(id => Session.BanksForMedia(id).Count == 0).Distinct().ToList();
            if (banks.Count == 0 && looseIds.Count == 0) { Say("none of those sounds are in a .bnk we can rewrite."); return; }
            var language = Session.Language;
            var label = VolumeTool.Label(bulk);
            var outputBase = Path.Combine(folder, ModBaseName(label));
            // The banks are written where nothing ships from; the .pak made from them is the mod.
            var work = Path.Combine(Path.GetTempPath(), "XzoundWave", "volume-" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = await Working($"{label}: scaling {levels.Count} sound(s) across {banks.Count} .bnk(s)", () =>
                {
                    var r = VolumeBankBuilder.Build(Session, banks, id => levels.GetValueOrDefault(id, 1.0), work,
                                                    context, vgm,
                                                    // The builder runs off the drawing thread, so its progress
                                                    // has to be handed back to that thread rather than set here.
                                                    m => Dispatcher.UIThread.Post(() => Say(m)),
                                                    muted, baseBanks, looseIds,
                                                    id => looseNow.LooseFor(id, language) is { Changed: true } l
                                                        ? new VolumeBankBuilder.LooseSource(l.MountedAt, l.Bytes) : null);
                    var files = Directory.GetFiles(work, "*", SearchOption.AllDirectories)
                                         .Select(f => Path.GetRelativePath(work, f))
                                         .Where(f => f.Replace('\\', '/').StartsWith("Marvel/", StringComparison.OrdinalIgnoreCase))
                                         .ToList();
                    var (ok, message) = ModPackager.PackagePak(work, files, outputBase);
                    if (!ok) throw new InvalidDataException(message);
                    // A bundle an earlier build left under this name would load beside the .pak.
                    foreach (var e in new[] { ".utoc", ".ucas" }) if (File.Exists(outputBase + e)) File.Delete(outputBase + e);
                    var legendFile = Path.Combine(work, "volume-legend.csv");
                    if (File.Exists(legendFile)) File.Copy(legendFile, outputBase + "-volume-legend.csv", true);
                    return r;
                });
                if (result is null) return;   // Working has already said why
                built.Add($"{Path.GetFileName(outputBase)}.pak ({result.Scaled} scaled{(result.Failed > 0 ? $", {result.Failed} failed" : "")})");
                legend.AddRange(result.Legend);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        Say($"volume build -> {folder}: {string.Join(", ", built)}" +
            (nothing.Count > 0 ? $"; {string.Join(", ", nothing)} skipped, every sound at 1.0 there" : "") +
            ". Copy the level you want into ~mods; the levels in <name>-volume-legend.csv.");

        // Clipping is the one thing that makes a louder mod sound broken rather than
        // louder, so it is put in front of someone rather than left in the log.
        var clipping = legend.Where(r => r.ClippedPercent > 0.1)
                             .OrderByDescending(r => r.ClippedPercent).ToList();
        if (clipping.Count > 0)
            await Tell("Some sounds clipped",
                $"{clipping.Count} sound(s) clipped at these levels. Loudest first:" +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, clipping.Take(12).Select(r =>
                    $"  {r.MediaId}  {r.Gain:0.0##}x  {r.ClippedPercent:0.0}% of samples  {r.Events}")) +
                (clipping.Count > 12 ? $"{Environment.NewLine}  … and {clipping.Count - 12} more" : "") +
                Environment.NewLine + Environment.NewLine +
                "Lower the level and build again; the originals are untouched, so nothing was lost.");
    }

    /// <summary>Banks embedding any of these ids -- the only ones worth rewriting.</summary>
    private List<string> BanksForVolume(IReadOnlyCollection<uint> ids)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
            foreach (var b in Session.BanksForMedia(id)) paths.Add(b);
        foreach (var b in BuildModBankPaths(ids)) paths.Add(b);
        // Without the global index, fall back to the picked skin's own banks.
        if (paths.Count == 0 && !Session.BankIndexReady && PickedSkin is not null)
            foreach (var b in PickedSkin.Banks) paths.Add(b.Path);
        return paths.ToList();
    }

    // ---- the two small dialogs this tab needs -------------------------------

    /// <summary>
    /// Ask for a multiplier. There is no input box in the toolkit, and a whole dialog
    /// file for one number is not worth it.
    /// </summary>
    /// <returns>
    /// Applied says whether they went through with it, so an empty box -- which means
    /// "follow the bulk level" -- can be told apart from a cancel. Both would otherwise
    /// be a null gain.
    /// </returns>
    /// <param name="emptyMeans">
    /// What leaving the box empty does HERE. It is not the same sentence in both
    /// dialogs -- on a selection it hands the level back to the bulk setting, and in
    /// the bulk dialog itself that reading is circular -- and a hint that is wrong
    /// about the one control it describes is worse than none.
    /// </param>
    /// <summary>
    /// The levels for a volume build, one .pak each, or null when cancelled. Separated by
    /// spaces or ';'. A comma separates too once any '.' is typed ("1.0, 0.8, 0.6"); without
    /// one it is a decimal comma ("0,8"), the way most of Europe writes a decimal.
    /// </summary>
    private async Task<List<double>> AskLevels(double start)
    {
        var box = new TextBox
        {
            Text = start.ToString("0.0##", CultureInfo.InvariantCulture),
            Margin = new Avalonia.Thickness(0, 8, 0, 4),
        };
        var hint = new TextBlock
        {
            Text = "One .pak per level, e.g. 1.0; 0.8; 0.6. Sounds whose level you set by hand keep it in " +
                   $"every one. Range {VolumeTool.MinGain:0.0##}-{VolumeTool.MaxGain:0.0#}; above 1.0 can clip, " +
                   "and the build says how much.",
            Foreground = Palette.Muted,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 420,
        };
        var apply = new Button { Content = "Build", Width = 100, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 100, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(16), Width = 430 };
        panel.Children.Add(new TextBlock { Text = "Build at which levels?", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Build at these volumes",
            Content = panel,
            SizeToContent = SizeToContent.Height,
            Width = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        var went = false;
        apply.Click += (_, _) => { went = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        if (LevelsForShot is { } scripted) { box.Text = scripted; went = true; }
        else await dialog.ShowDialog(this);
        if (!went) return null;

        var typed = (box.Text ?? "").Trim();
        if (typed.Contains('.')) typed = typed.Replace(',', ' ');
        else typed = typed.Replace(',', '.');
        var levels = new List<double>();
        foreach (var token in typed.Split([' ', ';', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var gain))
            {
                Say($"\"{token}\" is not a number.");
                return null;
            }
            gain = Math.Clamp(gain, VolumeTool.MinGain, VolumeTool.MaxGain);
            if (!levels.Any(l => Math.Abs(l - gain) < 0.0005)) levels.Add(gain);
        }
        if (levels.Count == 0) { Say("no level given."); return null; }
        return levels;
    }

    /// <summary>The render harness's answer to the levels prompt, since nobody is there to type it.</summary>
    internal string LevelsForShot;

    /// <summary>"Build at these volumes…", pressed by the render harness with these levels into this folder.</summary>
    internal Task BuildVolumesForShot(string folder, string levels)
    {
        BuildFolderForShot = folder;
        LevelsForShot = levels;
        return BuildVolumesAsync();
    }

    private async Task<(bool Applied, double? Gain)> AskMultiplier(string title, string message,
                                                                  double start, string emptyMeans)
    {
        var box = new TextBox
        {
            Text = start.ToString("0.0##", CultureInfo.InvariantCulture),
            Margin = new Avalonia.Thickness(0, 8, 0, 4),
        };
        var hint = new TextBlock
        {
            Text = $"1.0 leaves it alone. Range {VolumeTool.MinGain:0.0##}-{VolumeTool.MaxGain:0.0#}. " +
                   "Above 1.0 can clip, and the build says how much. " + emptyMeans,
            Foreground = Palette.Muted,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 420,
        };

        var apply = new Button { Content = "Apply", Width = 100, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 100, IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(16), Width = 430 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.Height,
            Width = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var went = false;
        apply.Click += (_, _) => { went = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await dialog.ShowDialog(this);
        if (!went) return (false, null);

        var typed = (box.Text ?? "").Trim();
        if (typed.Length == 0) return (true, null);

        // A comma is what a decimal point is in most of Europe, and refusing it would be
        // refusing the number they meant.
        if (!double.TryParse(typed.Replace(',', '.'), NumberStyles.Float,
                             CultureInfo.InvariantCulture, out var gain))
        {
            Say($"\"{typed}\" is not a number.");
            return (false, null);
        }
        return (true, Math.Clamp(gain, VolumeTool.MinGain, VolumeTool.MaxGain));
    }

    /// <summary>
    /// A question with two answers and a way out. Ask() gives yes or no, and the
    /// shared-media question has three honest outcomes: all of them, this skin only,
    /// or do not build at all.
    /// </summary>
    /// <returns>0 for the first answer, 1 for the second, -1 for cancel.</returns>
    private async Task<int> AskScope(string title, string body, string first, string second)
    {
        var choice = -1;
        var a = new Button { Content = first, MinWidth = 120 };
        var b = new Button { Content = second, MinWidth = 120 };
        var c = new Button { Content = "Cancel", MinWidth = 100, IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(a);
        buttons.Children.Add(b);
        buttons.Children.Add(c);

        var panel = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 560,
        });
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
        a.Click += (_, _) => { choice = 0; dialog.Close(); };
        b.Click += (_, _) => { choice = 1; dialog.Close(); };
        c.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return choice;
    }
}
