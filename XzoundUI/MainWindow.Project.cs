using System.ComponentModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace MRAudioKit.UI;

/// <summary>
/// The project: what is staged, the new voice lines, and each sound's note, mute, level and
/// test number, saved as one .xzw file -- the same format and the same rules the WPF window
/// used (paths stored relative to the project and with forward slashes, a dirty flag, an
/// offer to save before anything replaces it). The window lost this in the port; new voice
/// lines need it, because a line's VoiceID is allocated once and has to survive a restart.
///
/// Also here: the "New lines" node, which lists every new voice line as a row so that its
/// audio is staged exactly the way any sound's is, keyed by the line's own media id.
/// </summary>
public partial class MainWindow
{
    private Project _project = Project.New();
    private bool _closingConfirmed;
    private List<VoiceSet> _voiceSets;
    private GameAssetExtractor _extractor;
    private int _extractorGeneration;
    private VoiceLinesWindow _voiceLinesWindow;

    private void HookProject()
    {
        BtnProject.Click += (_, _) => ShowProjectMenu();
        BtnVoiceLines.Click += (_, _) => OpenVoiceLines();
        _pending.CollectionChanged += (_, _) =>
        {
            _project.Touch();
            RefreshProjectLabel();
            RefreshNewLinesNode();
            RefreshAddedNode();
            _voiceLinesWindow?.AudioChanged();
        };
        Closing += OnClosingAskToSave;
        RefreshProjectLabel();
    }

    /// <summary>Voice sets, read once per mounted game.</summary>
    internal List<VoiceSet> VoiceSetsOrNull()
    {
        if (_session?.Provider is null) return null;
        return _voiceSets ??= VoiceSets.Discover(_session);
    }

    internal Project CurrentProject => _project;
    internal GameSession CurrentSession => _session;
    internal IReadOnlyCollection<PendingWem> StagedFiles => _pending;

    /// <summary>Called by the voice-lines window when it changes a line.</summary>
    internal void VoiceLinesChanged()
    {
        _project.Touch();
        RefreshProjectLabel();
        RefreshNewLinesNode();
        // A removed line takes its staged audio with it: that file was for nothing else.
        var live = _project.VoiceLines.Select(l => l.MediaId).ToHashSet();
        foreach (var orphan in _pending.Where(p => _orphanCheck.Contains(p.MediaId) && !live.Contains(p.MediaId)).ToList())
            _pending.Remove(orphan);
        _orphanCheck = live;
        if (_picked.Any(n => n.What == Node.Kind.NewLines)) ShowRows();
    }
    private HashSet<uint> _orphanCheck = [];

    /// <summary>The render harness's way in: the window, opened as the button opens it.</summary>
    internal VoiceLinesWindow OpenVoiceLinesForShot()
    {
        OpenVoiceLines();
        return _voiceLinesWindow;
    }

    private void OpenVoiceLines()
    {
        if (VoiceSetsOrNull() is null) { Say("load the game first."); return; }
        if (_voiceLinesWindow is { IsVisible: true }) { _voiceLinesWindow.Activate(); return; }
        _orphanCheck = _project.VoiceLines.Select(l => l.MediaId).ToHashSet();
        _voiceLinesWindow = new VoiceLinesWindow(this);
        _voiceLinesWindow.Show(this);
    }

    // ---- the "New lines" node ---------------------------------------------------------

    /// <summary>The voice-lines window's way here: "New lines" picked, so a file can be dropped on a line.</summary>
    internal void RevealNewLines()
    {
        RefreshNewLinesNode();
        var node = _roots.FirstOrDefault(n => n.What == Node.Kind.NewLines);
        if (node is null) return;
        Tree.SelectedItems.Clear();
        Tree.SelectedItems.Add(node);
        Activate();
    }

    /// <summary>The first root while the project has new lines; absent otherwise.</summary>
    private void RefreshNewLinesNode()
    {
        if (_roots.Count == 0) return;
        var had = _roots.FirstOrDefault(n => n.What == Node.Kind.NewLines);
        var count = _project.VoiceLines.Count;
        var staged = _project.VoiceLines.Count(l => _pending.Any(p => p.MediaId == l.MediaId));
        if (count == 0) { if (had is not null) _roots.Remove(had); return; }
        var node = new Node
        {
            What = Node.Kind.NewLines,
            Label = $"New lines   ({staged}/{count} with audio)",
            Changed = count,
        };
        if (had is null) _roots.Insert(0, node);
        else if (had.Label != node.Label) _roots[_roots.IndexOf(had)] = node;
    }

    /// <summary>One row per new voice line: its own media id, so dropping audio on it stages it.</summary>
    private List<SoundRow> NewLineRows()
    {
        var sets = VoiceSetsOrNull() ?? [];
        var rows = new List<SoundRow>();
        foreach (var l in _project.VoiceLines)
        {
            var set = sets.FirstOrDefault(s => s.Key == l.Set);
            var trigger = VoiceTriggers.Of(l);
            rows.Add(new SoundRow
            {
                Kind = "VO",
                // Where the line lands: the hero's voice bank, which every set of that hero
                // plays from (a skin's own set included).
                Bank = set is null ? "" : $"bnk_vo_{set.HeroId}001",
                EventHash = l.EventId,
                EventName = l.EventName,
                MediaId = l.MediaId,
                Subtitle = l.Subtitle,
                Category = $"{set?.Label ?? "?"}: {trigger?.Name ?? l.Target}",
                Origin = "new line",
            });
        }
        return rows;
    }

    // ---- the project menu ---------------------------------------------------------------

    private void ShowProjectMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var where = new MenuItem { Header = _project.IsSaved ? _project.Path : "(not saved yet)", IsEnabled = false };
        menu.Items.Add(where);
        menu.Items.Add(new Separator());
        void Item(string header, Func<Task> run)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await run();
            menu.Items.Add(item);
        }
        Item("New", NewProject);
        Item("Open…", OpenProjectPicked);
        Item("Save", () => SaveProject(false));
        Item("Save as…", () => SaveProject(true));
        menu.ShowAt(BtnProject);
    }

    private void RefreshProjectLabel()
    {
        BtnProject.Content = $"{_project.Title} ▾";
        ToolTip.SetTip(BtnProject, _project.IsSaved ? _project.Path : "Project not saved yet");
        Title = $"XzoundWave — {_project.Title}";
    }

    // media id -> where it sat when it was staged (bank, Sound), worked out once per id.
    private readonly Dictionary<uint, (string Bank, uint Sound)> _anchors = [];

    /// <summary>
    /// The anchors of every staged file not yet worked out: the bank that embeds (or, for a
    /// loose file, streams) the medium, and the Sound in it that plays it. Off the drawing
    /// thread; ids the game cannot place now (the bank index still being read, a music
    /// track) are simply left for a later save.
    /// </summary>
    private void FindAnchors(IReadOnlyCollection<uint> ids)
    {
        var shapes = new Dictionary<string, ModUpdate.Shape>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            lock (_anchors) if (_anchors.ContainsKey(id)) continue;
            try
            {
                // The streaming walk reads every bank once: not for a new voice line's id or an added
                // sound's, which no game bank has, nor before the bank index is in, when every id would.
                var bank = _session.BanksForMedia(id).FirstOrDefault()
                           ?? (_session.BankIndexReady && !IsNewMedia(id)
                               ? _session.StreamingBank(id) : null);
                if (bank is null || _session.Bank(bank) is not { } file) continue;
                if (!shapes.TryGetValue(bank, out var shape)) shapes[bank] = shape = ModUpdate.Read(file.Read());
                if (shape.Players(id).FirstOrDefault() is var sound and not 0)
                    lock (_anchors) _anchors[id] = (bank, sound);
            }
            catch { /* unplaceable now; a later save tries again */ }
        }
    }

    /// <summary>
    /// After a game update: each staged file whose anchored Sound now plays another medium is
    /// moved to that medium. Returns old id -> new id for those that moved. Off the drawing
    /// thread; a bank or Sound the game no longer has leaves its file where it was.
    /// </summary>
    private Dictionary<uint, uint> FollowAnchors(IEnumerable<ProjectReplacement> replacements)
    {
        var moved = new Dictionary<uint, uint>();
        if (_session?.Provider is null) return moved;
        foreach (var group in replacements.Where(r => r.Bank is not null && r.Sound is not null).GroupBy(r => r.Bank, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (_session.Bank(group.Key) is not { } file) continue;
                var shape = ModUpdate.Read(file.Read());
                foreach (var r in group)
                    if (shape.SoundSource.TryGetValue(r.Sound.Value, out var now) && now != 0 && now != r.MediaId)
                        moved[r.MediaId] = now;
            }
            catch { /* a bank that cannot be read moves nothing */ }
        }
        return moved;
    }

    // ---- a sound's own state: its note, mute, level and test number ---------------------

    /// <summary>
    /// These sounds' note, mute, level or test number changed: into the project, so a save
    /// keeps them. The old window did this from every place that changed a row; the port had
    /// lost it, so every one of them came back blank after a restart.
    ///
    /// The state belongs to the media, not to the row. The project holds one record per media
    /// id (with the event and bank beside it, which is what lets it follow the sound to a new id
    /// after a game update), and a build mutes or levels a media id, not an event. One clip is
    /// often played by several events, so several rows share an id; a change to one is copied
    /// onto every row of that id the session has read, and only then written once. Written row
    /// by row instead, whichever sibling came last decided the record, and an untouched sibling
    /// with no note could wipe a note typed a moment before.
    /// </summary>
    private void RowStateChanged(IEnumerable<SoundRow> rows)
    {
        var changed = rows.GroupBy(r => r.MediaId).Select(g => g.Last()).ToList();
        if (changed.Count == 0) return;
        var ids = changed.Select(r => r.MediaId).ToHashSet();
        var siblings = KnownRows().Where(r => ids.Contains(r.MediaId)).GroupBy(r => r.MediaId)
                                  .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var source in changed)
        {
            foreach (var r in siblings.GetValueOrDefault(source.MediaId) ?? [])
                if (!ReferenceEquals(r, source)) CopyState(source, r);
            _project.SetNote(source);
        }
        RefreshProjectLabel();
    }

    private static void CopyState(SoundRow from, SoundRow to)
    {
        to.Notes = from.Notes;
        to.ExtraNotes = from.ExtraNotes;
        to.Tags = [.. from.Tags ?? []];
        to.TestNumber = from.TestNumber;
        to.Muted = from.Muted;
        to.Volume = from.Volume;
        to.VolumeByHand = from.VolumeByHand;
    }

    /// <summary>
    /// Every row this session has read: each skin's, each loaded mod's, and the ones on screen
    /// (a new voice line's row is made fresh for the screen). Rows outlive a project, so this is
    /// what a project change has to clear and what a change to one row's media has to reach.
    /// </summary>
    private IEnumerable<SoundRow> KnownRows() =>
        _skinCache.Values.SelectMany(s => s.Rows)
                  .Concat(_mods.All.SelectMany(e => e.Sounds?.Rows ?? []))
                  .Concat(_sounds?.Rows ?? [])
                  .Distinct();

    /// <summary>
    /// The project's notes, mutes, levels and test numbers onto rows just put on screen; how
    /// many records moved to a new media id, for the caller to say. Runs every time a selection
    /// is shown, because a mod's rows and a new line's are only met when they are listed, and a
    /// record can only follow its sound to a new id once that sound's row is listed.
    ///
    /// A record moves only when the game no longer has its media at all: a mod's bank playing
    /// an event from another id than the game's copy is not a game update, and the record still
    /// belongs to the game's id.
    /// </summary>
    private int ApplyProjectToRows(IReadOnlyList<SoundRow> rows)
    {
        var session = _session;
        // The project's own media are in no game bank by design: an added sound shares its event
        // with the sound it copies, and its note must not follow that event onto the game's row.
        var relinked = _project.ApplyNotes(rows, gone: id =>
            session?.Provider is not null && !session.MediaBank.ContainsKey(id) && session.LooseMedia(id) is null &&
            !IsNewMedia(id), mayTake: r => !IsProjectRow(r));
        if (relinked > 0) RefreshProjectLabel();
        return relinked;
    }

    private static string RelinkedNote(int relinked) => relinked == 0 ? "" :
        $" {relinked} note(s) followed their sound to a new media id after a game update; save the project to keep it.";

    /// <summary>
    /// Every row read this session back to having no note, mute, level or test number, before
    /// another project's are put on them. Rows outlive a project (they are kept per skin and per
    /// loaded mod), so without this a New or an Open would inherit the last project's mutes.
    /// </summary>
    private void ClearRowState()
    {
        foreach (var r in KnownRows())
        {
            r.Notes = "";
            r.ExtraNotes = "";
            r.Tags = [];
            r.TestNumber = null;
            r.Muted = false;
            r.Volume = null;
            r.VolumeByHand = false;
        }
    }

    /// <summary>The window's state into the project, just before it is written.</summary>
    private void CaptureIntoProject()
    {
        _project.BulkVolume = _bulkVolume;
        // The mods open now, in the order they were opened, stored the way staged files are; or,
        // with no game loaded yet, the ones waiting for it, which are still this project's.
        _project.Mods = (_modsAfterLoad ?? _modOpens).Select(m => new ProjectMod
        {
            File = _project.ToStoredPath(m.Path),
            Banks = [.. m.Banks ?? []],
        }).ToList();
        var first = _picked.FirstOrDefault(n => n.Skin is not null);
        _project.Character = first?.CharId ?? _project.Character;
        _project.Skin = first?.Skin?.SkinId ?? _project.Skin;
        _project.Bank = string.Join(";", _picked.Where(n => n.Bank is not null).Select(n => n.Bank));
        _project.Language = _settings.Language;
        // An anchor worked out now wins; otherwise the one the file already had is kept, so a
        // project saved before a patch still carries where its sounds were.
        var before = _project.Replacements.GroupBy(r => r.MediaId).ToDictionary(g => g.Key, g => g.First());
        _project.Replacements = _pending.Select(p =>
        {
            (string Bank, uint Sound) anchor;
            bool have;
            lock (_anchors) have = _anchors.TryGetValue(p.MediaId, out anchor);
            var old = before.GetValueOrDefault(p.MediaId);
            return new ProjectReplacement
            {
                MediaId = p.MediaId,
                File = _project.ToStoredPath(p.FullPath),
                Note = p.Note,
                Bank = have ? anchor.Bank : old?.Bank,
                Sound = have ? anchor.Sound : old?.Sound,
            };
        }).ToList();
    }

    /// <summary>
    /// A project's staged files back into the window; files that moved are listed. Every file
    /// is read first and the staged list replaced in one step, so nothing on screen is half old
    /// and half new while files load. The project is marked clean only if nothing was edited
    /// in the meantime (a voice line added while the files were read stays unsaved, and says so).
    /// </summary>
    private async Task RestoreFromProject()
    {
        var project = _project;
        var changes = project.Changes;
        var missing = new List<string>();
        var loaded = new Dictionary<uint, PendingWem>();
        // A game update may have given a staged sound a new media id: its file follows the Sound.
        var moved = await Task.Run(() => FollowAnchors(project.Replacements));
        foreach (var r in project.Replacements)
            if (moved.TryGetValue(r.MediaId, out var now)) r.MediaId = now;
        foreach (var r in project.Replacements)
        {
            var full = project.ToFullPath(r.File);
            byte[] bytes = null;
            var converted = "";
            if (File.Exists(full)) bytes = await Task.Run(() => PendingWem.ToWemBytes(full, out _, out converted));
            if (bytes is null) { missing.Add(r.File); continue; }
            // By the id the project stored, not one read from the file name: a file staged
            // onto a row (or a new line's slot) is named however its author named it. One per id.
            loaded[r.MediaId] = PendingWem.ForMedia(r.MediaId, full, bytes, r.Note, converted);
        }
        if (!ReferenceEquals(project, _project)) return;   // another project was opened meanwhile
        var edited = project.Changes != changes;
        _pending.Clear();
        foreach (var p in loaded.Values) _pending.Add(p);
        _orphanCheck = project.VoiceLines.Select(l => l.MediaId).ToHashSet();
        Reorder();
        MarkStagedRows();
        BuildRedraw();
        RefreshNewLinesNode();
        RefreshAddedNode();
        if (edited || moved.Count > 0) project.Touch(); else project.MarkClean();
        RefreshProjectLabel();
        if (moved.Count > 0)
            Say($"{moved.Count} staged file(s) followed their sound to a new media id after a game update: " +
                string.Join(", ", moved.Take(6).Select(m => $"{m.Key} -> {m.Value}")) + (moved.Count > 6 ? ", …" : "") +
                ". Save the project to keep it.");
        if (missing.Count > 0)
            await Tell("Missing files", "These staged files could not be found and were left out:" +
                       Environment.NewLine + string.Join(Environment.NewLine, missing.Take(15)));
    }

    /// <summary>False when they cancel out of the offer to save first.</summary>
    private async Task<bool> ConfirmDiscard()
    {
        if (!_project.IsDirty) return true;
        var answer = await AskScope("Unsaved changes", $"\"{_project.Name}\" has unsaved changes. Save it first?",
                                    "Save", "Don't save");
        if (answer < 0) return false;
        return answer == 1 || await SaveProject(false);
    }

    /// <summary>
    /// True, and said, while a project's mods are reopening. Saving then would write only the
    /// ones reopened so far; opening another project, or a mod, would land in the middle of
    /// it; so each of those waits the few seconds it takes.
    /// </summary>
    private bool StillReopeningMods()
    {
        if (_restoringMods == 0) return false;
        Say("still reopening the project's mods — give it a moment.");
        return true;
    }

    private async Task<bool> SaveProject(bool forcePrompt)
    {
        if (StillReopeningMods()) return false;
        // Where each staged file's sound sits, so the project can follow it through a game update.
        var ids = _pending.Select(p => p.MediaId).ToList();
        if (_session?.Provider is not null && ids.Count > 0)
            await Working("noting where the staged sounds sit", () => { FindAnchors(ids); return true; });
        CaptureIntoProject();
        try
        {
            if (forcePrompt || !_project.IsSaved)
            {
                var path = await SaveFile("Save project",
                    (string.IsNullOrWhiteSpace(_project.Name) ? "project" : _project.Name) + Project.Extension,
                    "XzoundWave project", "*" + Project.Extension);
                if (path is null) return false;
                var name = _project.Name;
                _project.Name = Path.GetFileNameWithoutExtension(path);
                try { _project.Save(path); }
                catch { _project.Name = name; throw; }
                // Again, now that there is a folder for the staged paths to be relative to.
                CaptureIntoProject();
            }
            _project.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unsaved, and said so: a save that throws out of a menu or the close prompt would
            // take the window down with exactly the work it was trying to keep.
            // Unsaved, whatever an earlier write in this same save managed.
            _project.Touch();
            RefreshProjectLabel();
            Say("could not save the project: " + ex.Message);
            await Tell("Could not save the project", ex.Message);
            return false;
        }
        // The real window only: a render or a self-test saving its scratch project must not become
        // the project the app reopens on launch.
        if (RemembersLayout) _settings.NoteRecentProject(_project.Path);
        _settings.Save();
        RefreshProjectLabel();
        Say($"saved {_project.Path}");
        return true;
    }

    private async Task NewProject()
    {
        if (StillReopeningMods() || !await ConfirmDiscard()) return;
        _project = Project.New();
        _pending.Clear();
        ClearRowState();
        _bulkVolume = 1.0;
        MarkStagedRows();
        BuildRedraw();
        RefreshNewLinesNode();
        RefreshAddedNode();
        // Clearing the staged list touched it; nothing has been done in it yet.
        _project.MarkClean();
        RefreshProjectLabel();
        _voiceLinesWindow?.ProjectReplaced();
        Say("new project.");
    }

    private async Task OpenProjectPicked()
    {
        if (StillReopeningMods() || !await ConfirmDiscard()) return;
        var path = await PickFile("Open project", "XzoundWave project", "*" + Project.Extension, "*" + Project.LegacyExtension);
        if (path is not null) await OpenProject(path, announce: true);
    }

    private async Task OpenProject(string path, bool announce)
    {
        Project project;
        try { _project = project = Project.Load(path); }
        catch (Exception ex)
        {
            Say("could not open the project: " + ex.Message);
            if (announce) await Tell("Could not open the project", ex.Message);
            return;
        }
        if (RemembersLayout) _settings.NoteRecentProject(path);
        _settings.Save();
        // The last project's notes, mutes and levels off every row, and this one's level back.
        ClearRowState();
        _bulkVolume = _project.BulkVolume;
        // Before the files are read: from here on the window edits the project being opened.
        _voiceLinesWindow?.ProjectReplaced();
        await RestoreFromProject();
        _voiceLinesWindow?.ProjectReplaced();
        // Another project opened while this one's files were read: the rest is that one's to do.
        if (!ReferenceEquals(project, _project)) return;

        // The mods it had open, as they were, after the staged files: a build sits on top of
        // them. A project written before mods were saved lists none and leaves the open ones
        // alone. With no game loaded yet they wait for it (LoadGame). Whatever an earlier
        // project parked for the game is not this one's, so it goes first.
        _modsAfterLoad = null;
        ModRestored restored = null;
        if (project.Mods is { } mods)
        {
            var opens = mods.Select(m => new ModOpen(project.ToFullPath(m.File), m.Banks ?? [])).ToList();
            if (!GameReadyForMods) _modsAfterLoad = opens;
            else restored = await ModRestoreAsync(opens, () => ReferenceEquals(project, _project));
            if (!ReferenceEquals(project, _project)) return;
        }

        // After the restore, which marks the project clean: a note relinked to a new media id
        // here leaves it unsaved, and says so.
        var relinked = _sounds is null ? 0 : ApplyProjectToRows(_sounds.Rows);
        BuildRedraw();
        var modsNote = project.Mods is not { Count: > 0 } ? ""
            : restored is null ? $" Its {project.Mods.Count} mod(s) open once the game has loaded."
            : $" {restored.Opened} of its {project.Mods.Count} mod(s) reopened.";
        if (announce)
            Say($"opened \"{project.Name}\": {project.Replacements.Count} staged file(s), " +
                $"{project.VoiceLines.Count} new voice line(s), " +
                (project.Layers.Count > 0 ? $"{project.Layers.Count} added sound(s), " : "") +
                $"{project.Notes.Count(n => !n.IsEmpty)} sound(s) with a note, mute, level or test number." +
                modsNote + RelinkedNote(relinked));
        else if (relinked > 0)
            Say(RelinkedNote(relinked).Trim());
        if (restored is { Clean: false })
            await Tell("Missing mods", "The project had these open, and they did not come back:" +
                       Environment.NewLine + string.Join(Environment.NewLine, restored.Lines.Take(15)) +
                       Environment.NewLine + Environment.NewLine +
                       "Everything else opened. Saving the project now leaves them out.");
    }

    /// <summary>
    /// Mods to reopen once the game is loaded: a project's, opened before the game was, or the
    /// ones a reload of the game closed (SetupLoadAsync). LoadGame reopens them; a save before
    /// then writes these, since they are still the project's (CaptureIntoProject).
    /// </summary>
    private List<ModOpen> _modsAfterLoad;

    /// <summary>
    /// Whether a mod can be read against the game now: mounted, done mounting, and with banks in
    /// it. Mount sets the provider on its first line, so the provider alone says yes while it is
    /// still being read, and after a load that failed.
    /// </summary>
    private bool GameReadyForMods => Session?.Provider is not null && !_mounting && _roots.Count > 0;

    /// <summary>True from the start of a game load until it has mounted and reopened the last project.</summary>
    private bool _mounting;

    /// <summary>What reopening <see cref="_modsAfterLoad"/> came to, for the load's own report.</summary>
    private string _modsAfterLoadNote;

    /// <summary>Reopen <see cref="_modsAfterLoad"/>, now that there is a game to read them against.</summary>
    private async Task ReopenModsAfterLoad()
    {
        // Only into a game that loaded something: a load that found no banks keeps them
        // parked for the next one, and does not dress itself up as a success with their
        // stand-in nodes (SetupLoadAsync reads an empty tree as the failure it is).
        if (_modsAfterLoad is not { } opens || !GameReadyForMods) return;
        var project = _project;
        var restored = await ModRestoreAsync(opens, () => ReferenceEquals(project, _project));
        var trouble = restored.Missing.Concat(restored.Failed).Select(Path.GetFileName).Concat(restored.BanksGone).ToList();
        _modsAfterLoadNote = $"  {restored.Opened} mod file(s) reopened" +
                             (trouble.Count > 0 ? $"; did not come back: {string.Join(", ", trouble.Take(5))}" : "") + ".";
    }

    /// <summary>
    /// Reopen the project worked on last, quietly; nothing is lost if it moved. Once per run,
    /// on the first load, and only over an empty, untouched project: Load game is pressed
    /// again after a language or mappings change, and that must never swap the work in hand
    /// for the last file saved.
    /// </summary>
    private async Task ReopenLastProject()
    {
        if (_reopenTried) return;
        _reopenTried = true;
        if (_project.IsDirty || _project.IsSaved || _project.VoiceLines.Count > 0 || _project.Layers.Count > 0 ||
            _pending.Count > 0) return;
        if (!string.IsNullOrEmpty(_settings.LastProject) && File.Exists(_settings.LastProject))
            await OpenProject(_settings.LastProject, announce: false);
    }
    private bool _reopenTried;

    /// <summary>The render harness: a project opened from a file, as Open… does.</summary>
    internal Task OpenProjectForShot(string path) => OpenProject(path, announce: false);

    /// <summary>The render harness: New, as the project menu does it.</summary>
    internal Task NewProjectForShot() => NewProject();

    /// <summary>The render harness: a row's level set the way Set volume… sets it, without its dialog.</summary>
    internal void RowStateChangedForShot(IEnumerable<SoundRow> rows) => RowStateChanged(rows);

    /// <summary>The render harness: the project saved to a file, as Save as… would.</summary>
    internal async Task SaveProjectForShot(string path)
    {
        _project.Save(path);
        await SaveProject(false);
    }

    private async void OnClosingAskToSave(object sender, CancelEventArgs e)
    {
        if (_closingConfirmed || !_project.IsDirty) return;
        e.Cancel = true;
        if (!await ConfirmDiscard()) return;
        _closingConfirmed = true;
        Close();
    }

    // ---- building new lines ------------------------------------------------------------

    /// <summary>
    /// The project's new voice lines, built on top of what the regular build just wrote into
    /// <paramref name="outRoot"/> and packaged as the IoStore bundle the game loads (the
    /// cooked assets a new line needs can only travel that way). Returns false and says why
    /// when it cannot.
    /// </summary>
    /// <param name="regularOutputs">
    /// The files the regular build wrote THIS time, relative to <paramref name="outRoot"/>:
    /// the starting point, and the only one. Anything else in the folder is left over from an
    /// earlier build -- a swap since removed -- and would ride along into the bundle.
    /// </param>
    /// <summary>
    /// The mod's file name, without extension, from the project's name: a file name whatever the
    /// project is called (a project from another system can carry a ':', which on Windows writes
    /// into hidden streams of another file, or a slash), loading as a patch (_P).
    /// </summary>
    /// <param name="tag">said between the name and the patch suffix, as in MyMod_Vol-0.8_9999999_P</param>
    private string ModBaseName(string tag = null)
    {
        var name = string.IsNullOrWhiteSpace(_project.Name) || _project.Name == "Untitled" ? "XzoundMod" : _project.Name;
        name = ModSafeName(name).Replace(':', '_').Replace('\\', '_').Replace('/', '_').Replace(' ', '_').Trim('_', '.');
        return (name.Length == 0 ? "XzoundMod" : name) + (string.IsNullOrEmpty(tag) ? "" : "_" + ModSafeName(tag)) + "_9999999_P";
    }

    /// <param name="regularRoot">where the regular build wrote <paramref name="regularOutputs"/></param>
    /// <param name="only">these lines rather than all of the project's (one character's, in a split build)</param>
    /// <param name="outputBase">the bundle's path without extension; the project's mod name by default</param>
    private async Task<bool> BuildVoiceLines(string outRoot, string regularRoot, IReadOnlyCollection<string> regularOutputs,
                                             IReadOnlyList<VoiceLine> only = null, string outputBase = null)
    {
        // A copy: the build runs off this thread while the window stays editable.
        var lines = (only ?? _project.VoiceLines).ToList();
        var silent = lines.Where(l => !_pending.Any(p => p.MediaId == l.MediaId)).ToList();
        if (silent.Count > 0)
        {
            Say($"{silent.Count} new line(s) have no audio yet -- stage a file on each under \"New lines\". " +
                $"First: \"{silent[0].Subtitle}\".");
            return false;
        }
        var sets = VoiceSetsOrNull();
        // One per mount (its containers are that mount's), and taken into a local: a remount
        // while this build runs must not pull it out from under the build.
        if (_extractor is null || _extractorGeneration != _session.Generation)
        {
            _extractor = new GameAssetExtractor(_settings, _session);
            _extractorGeneration = _session.Generation;
        }
        var extractor = _extractor;
        outputBase ??= Path.Combine(outRoot, ModBaseName());
        // A stage of this build's own, so a second build (or a second window) cannot empty it
        // while this one is packaging.
        var stage = Path.Combine(Path.GetTempPath(), "XzoundWave", "voicelines-stage-" + Guid.NewGuid().ToString("N"));
        // The loaded mods' banks, by the game path each replaces: "Build on it" promises the mod
        // stays in, so a bank the regular build did not rewrite starts from the mod's copy. By
        // full path, language folder included: an English voice pack is not the base for the
        // Chinese bank of the same name. Read here, on this thread, before the build starts.
        // A bank that never said its language folder (a bare .bnk) is taken as the active
        // language's, the same rule the regular build applies to it.
        var modBanks = new Dictionary<string, (byte[] Bytes, string From)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Mods.All)
            if ((e.Bank.MountedAt ?? BuildGamePathForStem(e.Stem)) is { } at) modBanks[at] = (e.Bank.Bytes, e.SourceName ?? e.BankName);
        var fromMods = new List<string>();
        var audio = _pending.Where(p => lines.Any(l => l.MediaId == p.MediaId))
                            .ToDictionary(p => p.MediaId, p => new VoiceLineBuilder.Audio(p.Payload ?? File.ReadAllBytes(p.FullPath), p.Seconds ?? 0));

        var result = await Working($"building {lines.Count} new voice line(s)", () =>
        {
          try
          {
            // What the regular build wrote (banks, loose media) is the starting point, so a
            // swap and a new line in the same bank both make it into the mod.
            foreach (var rel in regularOutputs)
            {
                var dest = Path.Combine(stage, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(Path.Combine(regularRoot, rel), dest, true);
            }
            var log = new List<string>();
            // Two voice sets of one hero share his voice bank: the second set's step finds the
            // first's lines already in it, and keeps them.
            var built = new HashSet<int>();
            foreach (var group in lines.GroupBy(l => l.Set))
            {
                var set = sets.FirstOrDefault(s => s.Key == group.Key)
                          ?? throw new InvalidDataException($"a line's voice set is not in this game ({group.Key})");
                VoiceLineBuilder.Result r;
                try
                {
                    r = VoiceLineBuilder.Build(_session, _settings, extractor, set, group.ToList(),
                        id => audio.GetValueOrDefault(id), stage, null,
                        bankSource: bp =>
                        {
                            if (File.Exists(Path.Combine(stage, bp))) return File.ReadAllBytes(Path.Combine(stage, bp));
                            if (!modBanks.TryGetValue(bp, out var mod)) return null;
                            fromMods.Add($"{bp} from {mod.From}");
                            return mod.Bytes;
                        },
                        keep: built);
                }
                catch (Exception ex) when (fromMods.Count > 0)
                {
                    // Name the loaded mod: it is the likely cause, and closing it is the way out.
                    throw new InvalidDataException($"{ex.Message} (built on a loaded mod's bank: {string.Join("; ", fromMods)}; " +
                                                   "close that mod to build on the game's own)", ex);
                }
                log.AddRange(r.Log);
                built.UnionWith(group.Select(l => l.VoiceId));
            }
            log.AddRange(fromMods.Distinct().Select(m => "built on a loaded mod: " + m));
            // Packaged beside the stage and moved into place only when whole: a failed build
            // must not leave a broken bundle under the name of the last good one.
            var packed = Path.Combine(stage + "-out", Path.GetFileName(outputBase));
            var (ok, message) = ModPackager.Package(stage, packed);
            if (!ok) throw new InvalidDataException(message);
            Directory.CreateDirectory(Path.GetDirectoryName(outputBase)!);
            foreach (var ext in new[] { ".utoc", ".ucas", ".pak" })
                File.Move(packed + ext, outputBase + ext, overwrite: true);
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(outputBase)!, "voicelines-log.txt"), log);
            return message;
          }
          finally
          {
              foreach (var dir in new[] { stage, stage + "-out" })
                  try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                  catch (IOException) { } catch (UnauthorizedAccessException) { }
          }
        });
        if (result is null) return false;
        _lastBuilt.AddRange(new[] { ".utoc", ".ucas", ".pak" }.Select(e => outputBase + e));
        Say($"new voice lines built: {Path.GetFileName(outputBase)}.utoc/.ucas/.pak in {Path.GetDirectoryName(outputBase)} -- " +
            "copy those three into ~mods. They carry everything this build made. Details in voicelines-log.txt.");
        return true;
    }
}
