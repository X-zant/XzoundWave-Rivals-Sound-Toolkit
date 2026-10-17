using System.IO;

namespace MRAudioKit.UI;

/// <summary>
/// Right-click › Add a sound to this event…: a file of the author's that plays every time an
/// event of the game's does, beside what it already plays -- music under an ult, say -- with
/// nothing of the game's replaced (<see cref="EventLayer"/>).
///
/// An added sound is kept the way a new voice line is: the project lists it, its audio is staged
/// under its own media id (so it is saved, played and swapped like any staged file), the
/// "Added sounds" node shows one row each, and Build mod writes it into its bank, along with a
/// manifest entry that lets a later build or an update after a game patch find it again.
/// </summary>
public partial class MainWindow
{
    internal const string AddedOrigin = "added sound";

    /// <summary>A media id of the project's own (a new line's or an added sound's), which no game bank has.</summary>
    private bool IsNewMedia(uint id) =>
        _project.VoiceLines.Any(l => l.MediaId == id) || _project.Layers.Any(l => l.MediaId == id);

    // By media id, not by the row's Origin text: playing a staged row rewrites its Origin to say
    // where the bytes came from, and an added sound's audio is always staged.
    private bool IsAddedRow(SoundRow r) => _project.Layers.Any(l => l.MediaId == r.MediaId);
    private bool IsProjectRow(SoundRow r) => IsNewMedia(r.MediaId);

    /// <summary>What adding to an event found before a file is asked for.</summary>
    private sealed record LayerPlan(uint? Template, string Why, List<uint> Stops, HircBank Bank);

    /// <summary>The render harness: right-click › Add a sound… with the file and the answer given.</summary>
    internal Task AddLayerForShot(string audio, bool stops) => AddLayerAsync(audio, stops);

    /// <summary>The render harness: right-click › Remove added sound.</summary>
    internal void RemoveLayersForShot() => RemoveLayers();

    /// <summary>The render harness: the tree's roots, as drawn.</summary>
    internal IReadOnlyList<Node> RootsForShot => _roots;

    /// <summary>The render harness: "Added sounds" picked in the tree, as a click picks it.</summary>
    internal void RevealAddedForShot()
    {
        RefreshAddedNode();
        if (_roots.FirstOrDefault(n => n.What == Node.Kind.AddedSounds) is not { } node) return;
        Tree.SelectedItems.Clear();
        Tree.SelectedItems.Add(node);
    }

    private async Task AddLayerAsync(string audioForShot = null, bool? stopsForShot = null)
    {
        if (Session?.Provider is null) { Say("load the game first."); return; }
        if (SelectedRows.Count != 1 || CurrentRow is not { } row)
        {
            Say("select the one sound whose event the new sound should play with.");
            return;
        }
        if (IsProjectRow(row)) { Say("pick one of the game's sounds: the new one plays with the event that plays it."); return; }
        var bp = GameBankPathFor(row.Bank);
        if (bp is null) { Say($"{row.Bank} is not a .bnk this game has."); return; }
        var what = row.EventName ?? row.EventHash.ToString();
        var session = Session;
        var baseBanks = BuildModBase();

        // Asked of the bank before a file is asked for, so an event this cannot add to is said now
        // rather than after the author has gone looking for audio. The bank is the one a build
        // starts from: an open mod's copy, or the game's.
        var plan = await Working($"reading {what}", () =>
        {
            var bytes = baseBanks.Read(session, bp) ?? throw new InvalidDataException($"{bp} cannot be read");
            var bank = HircBank.Parse(Path.GetFileName(bp), bytes);
            if (!bank.Write().AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException($"{Path.GetFileName(bp)} does not read back as it was, so nothing can be added to it");
            var template = EventLayer.TemplateFor(bank, row.EventHash, row.MediaId, out var why);
            if (template is null) return new LayerPlan(null, why, [], null);
            var stops = EventLayer.StopsOf(bank, template.Value);
            // A dry run on a copy: every writer has to reproduce this bank's own objects first,
            // and an event one cannot is better refused here than at build time.
            EventLayer.Add(HircBank.Parse("dry run", bytes), row.EventHash, new byte[64], EventLayer.Ids.For("dry run"),
                           template, stops);
            return new LayerPlan(template, null, stops, bank);
        });
        if (plan is null) return;   // Working has already said why
        if (plan.Template is null) { Say($"can't add a sound to {what}: {plan.Why}."); return; }

        var path = audioForShot ?? (await PickFiles($"Sound to play with {what}", "Audio",
                                                    [.. AudioDecode.Extensions.Select(e => "*" + e)])).FirstOrDefault();
        if (path is null) return;
        var name = Path.GetFileName(path);
        var data = await Working($"reading {name}", () =>
        {
            var bytes = PendingWem.ToWemBytes(path, out var why, out var converted);
            return (bytes, why, converted);
        });
        if (data.bytes is null)
        {
            if (data.why is not null) Say(data.why);
            return;
        }

        // Where the game ends the sound it copies, the author decides whether it ends there too.
        var stopNames = plan.Stops.Select(e => session.EventName(e) ?? e.ToString()).ToList();
        var stopsWith = false;
        if (plan.Stops.Count > 0)
        {
            var listed = string.Join(", ", stopNames.Take(4)) + (stopNames.Count > 4 ? $" and {stopNames.Count - 4} more" : "");
            var answer = stopsForShot is { } s ? (s ? 0 : 1) : await AskScope("Add a sound to this event",
                $"{name} will play once every time {what} plays, beside what it already plays. Nothing of the game's " +
                "is replaced, and it comes out the way the sound you picked does: the same bus, level and position." +
                Environment.NewLine + Environment.NewLine +
                $"The game stops that sound with {listed}. Stop yours there too, with the same fade, or let it play to the end?",
                "Stop with it", "Play to the end");
            if (answer < 0) { Say("nothing added."); return; }
            stopsWith = answer == 0;
        }

        // Ids hashed from the event and the file, moved on past any the game, the bank or this
        // project already uses.
        var taken = _project.Layers.Select(l => l.MediaId)
                            .Concat(_project.VoiceLines.Select(l => l.MediaId))
                            .Concat(_pending.Select(p => p.MediaId)).ToHashSet();
        var layer = new ProjectLayer
        {
            Bank = bp,
            Event = row.EventHash,
            EventName = row.EventName,
            Template = plan.Template.Value,
            Stops = stopsWith,
            Name = LayerBuild.NameFor(plan.Bank, $"{what}/{Path.GetFileNameWithoutExtension(path)}",
                                      id => taken.Contains(id) || session.MediaBank.ContainsKey(id) || session.LooseMedia(id) is not null),
        };
        layer.MediaId = layer.Ids.MediaId;
        _project.Layers.Add(layer);
        _pending.Add(PendingWem.ForMedia(layer.MediaId, path, data.bytes, $"added to {what}", data.converted));
        MarkStagedRows();
        LayersChanged();
        Say($"{name} added to {what}" +
            (stopsWith ? $", stopped where the game stops the sound it copies ({stopNames.Count} event(s))"
             : plan.Stops.Count > 0 ? ", playing to the end" : ", playing to the end (nothing in this bank stops the sound it copies)") +
            ". Build mod puts it in; it is listed under \"Added sounds\" in the tree.");
    }

    /// <summary>Take the selected added sounds out of the project, and their staged audio with them.</summary>
    private void RemoveLayers()
    {
        var picked = SelectedRows.Where(IsAddedRow).Select(r => r.MediaId).ToHashSet();
        if (picked.Count == 0) { Say("select an added sound under \"Added sounds\" first."); return; }
        var n = _project.Layers.RemoveAll(l => picked.Contains(l.MediaId));
        foreach (var p in _pending.Where(p => picked.Contains(p.MediaId)).ToList()) _pending.Remove(p);
        // Its note goes with it: left behind under an id nothing plays any more, it would read as a
        // sound that moved, and follow the event onto the game's own row.
        _project.Notes.RemoveAll(r => picked.Contains(r.MediaId));
        LayersChanged();
        Say($"{n} added sound(s) removed.");
    }

    private void LayersChanged()
    {
        _project.Touch();
        RefreshProjectLabel();
        RefreshAddedNode();
        if (_picked.Any(n => n.What == Node.Kind.AddedSounds)) ShowRows();
    }

    // ---- the "Added sounds" node ---------------------------------------------------------

    /// <summary>A root while the project has added sounds, after "New lines"; absent otherwise.</summary>
    private void RefreshAddedNode()
    {
        if (_roots.Count == 0) return;
        var had = _roots.FirstOrDefault(n => n.What == Node.Kind.AddedSounds);
        var count = _project.Layers.Count;
        if (count == 0) { if (had is not null) _roots.Remove(had); return; }
        var staged = _project.Layers.Count(l => _pending.Any(p => p.MediaId == l.MediaId));
        var node = new Node
        {
            What = Node.Kind.AddedSounds,
            Label = $"Added sounds   ({staged}/{count} with audio)",
            Changed = count,
        };
        if (had is null) _roots.Insert(_roots[0].What == Node.Kind.NewLines ? 1 : 0, node);
        else if (had.Label != node.Label) _roots[_roots.IndexOf(had)] = node;
    }

    /// <summary>One row per added sound, under its own media id, so dropping audio on it swaps the file.</summary>
    private List<SoundRow> AddedRows()
    {
        var rows = new List<SoundRow>();
        foreach (var l in _project.Layers)
        {
            var staged = _pending.FirstOrDefault(p => p.MediaId == l.MediaId);
            var stem = Path.GetFileNameWithoutExtension(l.Bank);
            rows.Add(new SoundRow
            {
                Kind = stem.StartsWith("bnk_vo", StringComparison.OrdinalIgnoreCase) ? "VO" : "SFX",
                Bank = stem,
                EventHash = l.Event,
                EventName = l.EventName,
                MediaId = l.MediaId,
                Subtitle = staged?.FileName ?? "(no audio: drop a file on this row)",
                Seconds = staged?.Seconds,
                Category = l.Stops ? $"added; stops where Sound {l.Template} does" : "added; plays to the end",
                Origin = AddedOrigin,
            });
        }
        return rows;
    }

    // ---- the build -----------------------------------------------------------------------

    /// <summary>
    /// The project's added sounds, into the banks this build wrote (or the ones it starts from),
    /// with <paramref name="outputs"/> gaining any bank only they changed. Sounds an open mod
    /// added stay in its banks, as everything else it changed does.
    /// </summary>
    /// <param name="freshLog">no regular build ran, so build-log.txt is this build's own from here</param>
    /// <returns>what to add to the summary, or null when they could not be built (said)</returns>
    private async Task<string> AddLayersToBuild(string outRoot, string work, List<string> outputs, bool freshLog)
    {
        var layers = _project.Layers.ToList();
        if (layers.Count == 0) return "";
        var silent = layers.Where(l => !_pending.Any(p => p.MediaId == l.MediaId)).ToList();
        if (silent.Count > 0)
        {
            Say($"{silent.Count} added sound(s) have no audio -- drop a file on each under \"Added sounds\". " +
                $"First: the one on {silent[0].EventName ?? silent[0].Event.ToString()}.");
            return null;
        }
        var audio = _pending.Where(p => layers.Any(l => l.MediaId == p.MediaId)).ToDictionary(p => p.MediaId);
        var session = Session;
        var baseBanks = BuildModBase();
        var result = await Working($"adding {layers.Count} sound(s) to their events",
            () => LayerBuild.Apply(session, layers,
                                   id => audio.TryGetValue(id, out var p) ? p.Payload ?? File.ReadAllBytes(p.FullPath) : null,
                                   work, baseBanks));
        if (result is null) return null;   // Working has already said why
        foreach (var o in result.Outputs)
            if (!outputs.Contains(o, StringComparer.OrdinalIgnoreCase)) outputs.Add(o);
        if (result.Log.Count > 0)
        {
            var log = Path.Combine(outRoot, "build-log.txt");
            if (freshLog) File.WriteAllLines(log, result.Log); else File.AppendAllLines(log, result.Log);
        }
        return $"{layers.Count} added sound(s). ";
    }
}
