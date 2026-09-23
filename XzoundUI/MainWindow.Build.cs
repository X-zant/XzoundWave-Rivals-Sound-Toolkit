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
    /// </summary>
    private readonly List<PendingWem> _pending = [];

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
            new("Build modded .bnk", () => _ = BuildModAsync(), Key: true,
                Hint: "Rebuild every .bnk that embeds a staged or muted sound, into a folder you can hand to repak."),
            new("Set replacement…", () => _ = SetReplacementAsync(),
                Hint: "Stage audio. With rows selected it goes to those sounds; otherwise the filenames say where it goes."),
            new("Clear staged", () => _ = ClearStagedAsync(),
                Hint: "Unstage. Everything by default; with staged rows selected it offers those alone."),
            new("Set volume…", () => _ = SetVolumeAsync(),
                Hint: "A level for the selected sounds, kept against them so a bulk change leaves them alone."),
            new("Change all volumes…", () => _ = SetAllVolumesAsync(),
                Hint: "One level for every sound in the open skin -- not just the filtered ones -- except those set by hand."),
            new("Build at these volumes…", () => _ = BuildVolumesAsync(),
                Hint: "Write banks with every level applied, into a folder named after the bulk level."),
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

        foreach (var row in ids)
        {
            _pending.RemoveAll(x => x.MediaId == row.MediaId);
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
        var files = new List<string>();
        foreach (var p in paths)
        {
            // Folders are expanded here rather than at the call site so a drop target,
            // when the new window grows one, can hand this whatever was dropped.
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                                        .Where(AudioDecode.CanDecode));
            else if (AudioDecode.CanDecode(p))
                files.Add(p);
        }
        if (files.Count == 0) { Say("nothing to stage -- no audio files there."); return; }

        // Converting an mp3 means decoding and re-encoding it, which is far too slow to
        // do on the drawing thread for a folder full of them.
        var results = await Working($"staging {files.Count} file(s)", () => files
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(f => { var item = PendingWem.FromFile(f, out var why); return (item, why); })
            .ToList());
        if (results is null) return;

        var added = 0; var replaced = 0;
        var rejected = new List<string>();
        foreach (var (item, why) in results)
        {
            if (item is null) { rejected.Add(why); continue; }

            var existing = _pending.FirstOrDefault(x => x.MediaId == item.MediaId);
            if (existing is not null) { _pending.Remove(existing); replaced++; } else added++;
            _pending.Add(item);
        }

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
        if (problems.Count > 0)
            await Tell("Check these files",
                string.Join(Environment.NewLine, problems.Take(30)) +
                (problems.Count > 30 ? $"{Environment.NewLine}… and {problems.Count - 30} more" : ""));
    }

    private void Reorder()
    {
        var sorted = _pending.OrderBy(p => p.MediaId).ToList();
        _pending.Clear();
        _pending.AddRange(sorted);
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
    /// The old window could also drop ONE staged file, from the pending grid beside the
    /// drop pane. That grid is not ported and the ability went with it, which left
    /// staging onto the wrong row undoable except by clearing the lot. So when the
    /// selection holds staged sounds the question carries both scopes: the row is where
    /// a mis-stage is visible, and it is the natural place to take one back.
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
                _pending.RemoveAll(p => picked.Contains(p.MediaId));
                MarkStagedRows();
                BuildRedraw();
                Say($"{pickedStaged} replacement(s) cleared -- {_pending.Count} still staged. {StagedScope()}");
                return;
            }
        }
        else if (_pending.Count > 1 &&
                 !await Ask("Clear staged audio",
                            $"{_pending.Count} files are staged. Unstage all of them?\n\n" +
                            "The files on disk are untouched; only the assignments go.",
                            "Unstage all", "Keep them"))
        {
            Say("kept.");
            return;
        }

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

        // A mute is a build instruction, not a test-build one: silencing a line you do
        // not want is a thing people ship.
        var mutedIds = Sounds?.Rows.Where(r => r.Muted).Select(r => r.MediaId).ToHashSet() ?? [];

        // Picking a skin is only needed when there is nothing staged to build FROM. A
        // staged file carries its own media id and the id says which .bnk it belongs in,
        // so a folder of named wems may even span characters -- which no single skin
        // selection could express.
        if (_pending.Count == 0 && mutedIds.Count == 0)
        {
            Say(PickedSkin is null
                ? "nothing to build -- stage audio with \"Set replacement…\", or pick a skin and mute something."
                : "nothing to build -- stage audio with \"Set replacement…\", or mute a sound.");
            return;
        }
        if (_pending.Count > 0 && PickedSkin is null && !Session.BankIndexReady)
        {
            Say("still reading .bnk files -- give it a moment, or pick a skin.");
            return;
        }

        var outRoot = await PickFolder("Output folder for the mod");
        if (outRoot is null) return;

        // Which banks actually embed something staged? Ask the global index rather than
        // assuming the picked skin -- a mod can span skins, and a silently unpatched
        // bank is the worst possible outcome.
        var bankPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _pending)
            foreach (var bp in Session.BanksForMedia(p.MediaId)) bankPaths.Add(bp);
        // Muted sounds need their .bnk rebuilt too, even with nothing staged.
        foreach (var id in mutedIds)
            foreach (var bp in Session.BanksForMedia(id)) bankPaths.Add(bp);
        // A loaded mod can hold ids the shipped game does not.
        foreach (var bp in BuildModBankPaths(_pending.Select(x => x.MediaId).Concat(mutedIds)))
            bankPaths.Add(bp);
        var skin = PickedSkin;
        if (!Session.BankIndexReady && skin is not null)
            foreach (var b in skin.Banks) bankPaths.Add(b.Path);

        if (bankPaths.Count == 0)
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
            var shared = _pending.Select(p => p.MediaId).Concat(mutedIds).Distinct()
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
        var staged = _pending.ToList();
        var built = await Working($"building {bankPaths.Count} .bnk(s)", () =>
            ModBuilder.Build(Session, skin, staged, outRoot, bankPaths,
                             prefetchMode: false, muted: mutedIds, baseBanks: BuildModBase()));
        if (built is null) return;   // Working has already said why

        Say($"built {built.Banks} .bnk(s) and {built.Loose} audio file(s) into {outRoot} -- " +
            (built.Muted > 0 ? $"{built.Muted} muted. " : "") +
            (built.Loose == 0 ? "the .bnk is the whole mod. " : "ship the whole folder to repak. ") +
            scopeNote + " Details in build-log.txt.");
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
    private async Task<bool> BuildOnModRefused()
    {
        if (!Mods.Any) return false;

        // The literal key is the one the old window stored, so an author who silenced
        // this warning there is not asked again here. Never reuse a key for a different
        // message: someone who silenced the old meaning did not agree to the new one.
        if (Config?.SuppressedWarnings?.Contains("build-uses-a-loaded-mod-as-its-base",
                                                 StringComparer.OrdinalIgnoreCase) == true)
            return false;

        var names = Mods.All.Select(e => e.BankName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        var go = await Ask("Building on a loaded mod",
            "These .bnk files come from a mod and will be the base for this build:" +
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
        var moved = 0;
        foreach (var r in Sounds.Rows.Where(r => !r.VolumeByHand))
        {
            // Null rather than 1.0 at the neutral level, so the row goes back to
            // following the bulk setting instead of pinning itself to it.
            r.Volume = Math.Abs(_bulkVolume - 1.0) < 0.001 ? null : _bulkVolume;
            moved++;
        }
        BuildRedraw();
        Say($"{moved} sound(s) now at {_bulkVolume:0.0##}x" +
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
        if (await BuildOnModRefused()) return;

        var levels = Sounds.Rows
            .Where(r => Math.Abs(r.EffectiveVolume(_bulkVolume) - 1.0) > 0.001)
            .GroupBy(r => r.MediaId)
            .ToDictionary(g => g.Key, g => g.First().EffectiveVolume(_bulkVolume));
        if (levels.Count == 0)
        {
            Say("every sound is at 1.0, so there is nothing to write -- " +
                "set a level on a selection, or use \"Change all volumes…\".");
            return;
        }

        var folder = await PickFolder("Output folder for the volume build");
        if (folder is null) return;
        var outRoot = Path.Combine(folder, VolumeTool.Label(_bulkVolume));

        var banks = BanksForVolume(levels.Keys.ToList());
        if (banks.Count == 0) { Say("none of those sounds are in a .bnk we can rewrite."); return; }

        // Muting wins over any level: there is no useful gain to apply to silence.
        var muted = Sounds.Rows.Where(r => r.Muted).Select(r => r.MediaId).ToHashSet();
        var vgm = Config.VgmstreamPath;
        var context = Sounds;
        var baseBanks = BuildModBase();

        var result = await Working($"scaling {levels.Count} sound(s) across {banks.Count} .bnk(s)", () =>
            VolumeBankBuilder.Build(Session, banks, id => levels.GetValueOrDefault(id, 1.0), outRoot,
                                    context, vgm,
                                    // The builder runs off the drawing thread, so its progress
                                    // has to be handed back to that thread rather than set here.
                                    m => Dispatcher.UIThread.Post(() => Say(m)),
                                    muted, baseBanks));
        if (result is null) return;

        Say($"volume build: {result.Scaled} scaled, {result.Failed} failed, " +
            $"{result.Banks} .bnk(s) -> {outRoot}. Levels in volume-legend.csv.");

        // Clipping is the one thing that makes a louder mod sound broken rather than
        // louder, so it is put in front of someone rather than left in the log.
        var clipping = result.Legend.Where(r => r.ClippedPercent > 0.1)
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
        if (paths.Count == 0 && PickedSkin is not null)
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
