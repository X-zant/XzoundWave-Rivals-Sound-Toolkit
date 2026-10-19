using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CUE4Parse.UE4.Wwise.Enums;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

/// <summary>
/// A sound added beside what an event already plays (<see cref="EventLayer"/>), as a project
/// keeps it. Its audio is not here: like a new voice line's, it is staged under the layer's own
/// <see cref="MediaId"/>, so it is saved, played and replaced the way every staged file is.
/// </summary>
public sealed class ProjectLayer
{
    /// <summary>The game path of the .bnk that holds the event.</summary>
    public string Bank { get; set; } = "";
    public uint Event { get; set; }
    /// <summary>For people: the event's name, when the game ships one.</summary>
    public string EventName { get; set; }
    /// <summary>
    /// What the layer copies, so it sits in the same mixer and comes out of the same bus: a Sound
    /// the event plays, or the random container holding the sound you hear.
    /// </summary>
    public uint Template { get; set; }
    /// <summary>Stopped wherever the game stops <see cref="Template"/>; otherwise it plays to the end.</summary>
    public bool Stops { get; set; }
    public uint MediaId { get; set; }
    /// <summary>What the layer's ids are hashed from (<see cref="EventLayer.Ids.For"/>).</summary>
    public string Name { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public EventLayer.Ids Ids => EventLayer.Ids.For(Name);
}

/// <summary>
/// The layers a bank carries, written into its manifest section (<see cref="VoiceLineBuilder.ManifestTag"/>)
/// beside the new voice lines, as "layers". The section is what makes a built mod readable again
/// without the project that made it: a build on top of an earlier one takes that one's layers
/// out instead of adding them twice, and an update after a game patch takes them out of the old
/// bank and adds them again to the patched one, anchored on the Sound each copies -- the id a
/// patch keeps (956 of 967 Sounds in one bank), where media and event ids move.
/// </summary>
public static class LayerManifest
{
    public const string Format = "xzoundwave.manifest";

    public sealed record Stop(uint EventId, uint StopId);

    /// <summary>One layer, by every id it added, and what it was made from.</summary>
    /// <param name="Container">its own copy of a random container, which holds <see cref="SoundId"/>; 0 when it is a Sound in the mixer</param>
    public sealed record Entry(string Name, string EventName, uint EventId, uint Template, uint Mixer, bool StopsWithTemplate,
                               uint SoundId, uint PlayId, uint MediaId, IReadOnlyList<Stop> Stops, uint Container = 0);

    /// <summary>A layer taken out of a bank, with its audio.</summary>
    public sealed record Taken(Entry Entry, byte[] Wem);

    /// <summary>
    /// The bank's manifest, or a new one when it has none. A section by the tag that is not a
    /// manifest (not JSON, or another tool's) is kept whole as "previous", the voice-line
    /// builder's rule, never parsed into a failed build.
    /// </summary>
    public static JsonObject Read(HircBank bank)
    {
        if (bank.Sections.FirstOrDefault(s => s.Tag == VoiceLineBuilder.ManifestTag) is not { } old)
            return New();
        var text = Encoding.UTF8.GetString(old.Body).TrimEnd(' ', '\0');
        JsonNode node = null;
        try { node = JsonNode.Parse(text); } catch (JsonException) { }
        if (node is JsonObject root && (root["format"]?.GetValueKind() == JsonValueKind.String
                                            ? (string)root["format"] == Format : root["lines"] is JsonArray))
            return root;
        var fresh = New();
        fresh["previous"] = node ?? JsonValue.Create(text);
        return fresh;
    }

    private static JsonObject New() => new() { ["format"] = Format, ["version"] = 1 };

    /// <summary>The layers a manifest names; an entry missing any id is not one, and is skipped.</summary>
    public static List<Entry> Entries(JsonObject manifest)
    {
        var list = new List<Entry>();
        if (manifest?["layers"] is not JsonArray layers) return list;
        foreach (var n in layers)
        {
            if (n is not JsonObject o) continue;
            bool U(string k, out uint v)
            {
                v = 0;
                return o[k] is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number && jv.TryGetValue(out v);
            }
            if (!U("eventId", out var ev) || !U("template", out var t) || !U("mixer", out var mx) ||
                !U("soundId", out var s) || !U("playId", out var p) || !U("mediaId", out var m)) continue;
            var stops = new List<Stop>();
            if (o["stops"] is JsonArray sa)
                foreach (var sn in sa)
                    if (sn is JsonObject so && so["eventId"] is JsonValue a && a.TryGetValue(out uint se) &&
                        so["stopId"] is JsonValue b && b.TryGetValue(out uint si))
                        stops.Add(new Stop(se, si));
            list.Add(new Entry((string)o["name"] ?? "", (string)o["event"], ev, t, mx,
                               o["stopsWithTemplate"] is JsonValue w && w.TryGetValue(out bool wb) && wb, s, p, m, stops,
                               U("container", out var cn) ? cn : 0));
        }
        return list;
    }

    private static JsonObject ToJson(Entry e)
    {
        var o = new JsonObject
        {
            ["name"] = e.Name, ["event"] = e.EventName, ["eventId"] = e.EventId,
            ["template"] = e.Template, ["mixer"] = e.Mixer, ["stopsWithTemplate"] = e.StopsWithTemplate,
            ["soundId"] = e.SoundId, ["playId"] = e.PlayId, ["mediaId"] = e.MediaId,
            ["stops"] = new JsonArray([.. e.Stops.Select(s => (JsonNode)new JsonObject { ["eventId"] = s.EventId, ["stopId"] = s.StopId })]),
        };
        if (e.Container != 0) o["container"] = e.Container;
        return o;
    }

    /// <summary>
    /// The manifest back into the bank with these layers as its "layers" (none removes the key),
    /// everything else in it -- the voice lines, "previous" -- as it was. Padded with spaces to a
    /// four-byte length, as the voice-line builder writes it; dropped when nothing is left in it.
    /// </summary>
    /// <param name="game">the game's bank, named as what was built on</param>
    public static void Write(HircBank bank, JsonObject manifest, IReadOnlyList<Entry> layers, string bankPath, byte[] game)
    {
        manifest.Remove("layers");
        if (layers.Count > 0) manifest["layers"] = new JsonArray([.. layers.Select(e => (JsonNode)ToJson(e))]);
        if (game is not null && bankPath is not null)
            manifest["builtOn"] = new JsonObject
            {
                ["bank"] = Path.GetFileNameWithoutExtension(bankPath), ["path"] = bankPath,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(game)).ToLowerInvariant(),
            };
        bank.Sections.RemoveAll(s => s.Tag == VoiceLineBuilder.ManifestTag);
        var carries = manifest["layers"] is not null || manifest["lines"] is JsonArray { Count: > 0 } || manifest["previous"] is not null;
        if (!carries) return;
        var json = Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var body = new byte[(json.Length + 3) & ~3];
        Array.Fill(body, (byte)' ');
        json.CopyTo(body, 0);
        bank.Sections.Add(new BnkBuilder.Section(VoiceLineBuilder.ManifestTag, body));
    }

    /// <summary>
    /// The layers the manifest names (those <paramref name="which"/> picks, or all), taken out of
    /// the bank with their audio kept: the Sound (and its place in its mixer), its Play and Stops
    /// (and their places in their events), its medium. Each object goes only when it is the kind
    /// the entry says and belongs to that layer's Sound, so an id that means something else in this
    /// bank is left alone.
    /// </summary>
    public static List<Taken> TakeOut(HircBank bank, JsonObject manifest, Func<Entry, bool> which = null)
    {
        var taken = new List<Taken>();
        foreach (var e in Entries(manifest).Where(e => which is null || which(e)))
        {
            var sound = bank.Objects.FirstOrDefault(o => o.Id == e.SoundId && o.Node is HierarchySoundSfxVoice s && s.Source.SourceId == e.MediaId);
            if (sound is null) continue;
            var wem = bank.Media.Any(m => m.Id == e.MediaId) ? bank.MediaBytes(e.MediaId) : null;
            // Its own container copy, only when it is one holding this layer's Sound.
            var container = e.Container == 0 ? null
                : bank.Objects.FirstOrDefault(o => o.Id == e.Container && o.Node is HierarchyRandomSequenceContainer c &&
                                                   (c.ChildIds ?? []).Contains(e.SoundId));
            // What the mixer holds and the event's actions point at: the container copy, or the Sound.
            var placed = container?.Id ?? e.SoundId;

            if (bank.Objects.FirstOrDefault(o => o.Id == e.Mixer && o.Node is HierarchyActorMixer) is { } mixerObj)
            {
                var span = HircLayout.Mixer(mixerObj.Body, bank.Version);
                if (span.Parsed.ChildIds.Contains(placed))
                {
                    mixerObj.Body = HircWriter.Mixer(mixerObj.Body, span, bank.Version, span.Parsed.ChildIds.Where(c => c != placed).ToArray());
                    mixerObj.Node = HircBank.ReadExact(mixerObj.Type, mixerObj.Body, bank.Version, "mixer");
                    bank.Nodes[mixerObj.Id] = mixerObj.Node;
                }
            }
            foreach (var (actionId, kind) in e.Stops.Select(s => (s.StopId, EAkActionType.Stop)).Prepend((e.PlayId, EAkActionType.Play)))
            {
                var act = bank.Objects.FirstOrDefault(o => o.Id == actionId &&
                                                           o.Node is HierarchyEventAction a && a.EventActionType == kind && a.ReferencedId == placed);
                if (act is null) continue;
                foreach (var evObj in bank.Objects.Where(o => o.Node is HierarchyEvent ev && ev.EventActionIds.Contains(actionId)).ToList())
                    EventLayer.Rewrite(bank, evObj, [.. ((HierarchyEvent)evObj.Node).EventActionIds.Where(x => x != actionId)]);
                bank.Objects.Remove(act);
                bank.Nodes.Remove(actionId);
            }
            if (container is not null)
            {
                bank.Objects.Remove(container);
                bank.Nodes.Remove(container.Id);
            }
            bank.Objects.Remove(sound);
            bank.Nodes.Remove(e.SoundId);
            if (wem is not null) bank.RemoveMedia(e.MediaId);
            taken.Add(new Taken(e, wem));
        }
        return taken;
    }

    /// <summary>The entry for a layer just added.</summary>
    public static Entry Of(string name, string eventName, uint eventId, EventLayer.Added added, bool stopsWithTemplate) =>
        new(name, eventName, eventId, added.Template, added.MixerId, stopsWithTemplate,
            added.Ids.SoundId, added.Ids.PlayId, added.Ids.MediaId,
            [.. added.StoppedBy.Select((ev, i) => new Stop(ev, added.Ids.StopAt(i)))], added.Container);
}

/// <summary>The project's layers, applied to the banks a build writes.</summary>
public static class LayerBuild
{
    /// <summary>What a template id is, for a person: a container or a Sound.</summary>
    private static string Kind(HircBank bank, uint id) =>
        bank.Nodes.GetValueOrDefault(id) is HierarchyRandomSequenceContainer ? $"container {id}" : $"Sound {id}";

    /// <summary>
    /// The template a layer copies in this bank: its own, unless that is a Sound the game has
    /// muted -- which v1.1.0 could not tell, and copied silent -- in which case what the event
    /// plays that is heard (<paramref name="note"/> says so). Null, with why, when nothing is.
    /// </summary>
    private static uint? Heard(HircBank bank, uint ev, uint template, out string note)
    {
        note = null;
        if (bank.Nodes.GetValueOrDefault(template) is not HierarchySoundSfxVoice s || EventLayer.Heard(bank, s)) return template;
        var now = EventLayer.TemplateFor(bank, ev, null, out var why);
        note = now is null ? $"the sound it copied ({template}) is muted in the game, and {why}"
                           : $"the sound it copied ({template}) is muted in the game; it now copies {Kind(bank, now.Value)}";
        return now;
    }

    public sealed record Result(List<string> Outputs, List<string> Log);

    /// <summary>
    /// The name a new layer's ids are hashed from: <paramref name="basis"/>, moved on past any id
    /// the bank already holds or <paramref name="mediaTaken"/> says is used (a media id is the
    /// game's as well as the bank's).
    /// </summary>
    public static string NameFor(HircBank bank, string basis, Func<uint, bool> mediaTaken)
    {
        bool Free(EventLayer.Ids ids) =>
            !mediaTaken(ids.MediaId) && !bank.Media.Any(m => m.Id == ids.MediaId) &&
            !bank.Nodes.ContainsKey(ids.SoundId) && !bank.Nodes.ContainsKey(ids.PlayId) && !bank.Nodes.ContainsKey(ids.StopId) &&
            !bank.Nodes.ContainsKey(ids.ContainerId);
        var name = basis;
        for (var n = 1; !Free(EventLayer.Ids.For(name)); n++) name = $"{basis}#{n}";
        return name;
    }

    /// <summary>
    /// The event a layer goes on in this bank: the one it was made on, by name (an event's id is
    /// the hash of its name) or else by id, while that still plays the Sound the layer copies;
    /// otherwise the one event that does (<paramref name="moved"/>), which is where a game patch
    /// that renamed the event put it. Null, with the reason, when the Sound is gone, or when
    /// several events play it and none is the one it was on: that is not guessed at.
    /// </summary>
    public static uint? EventFor(HircBank bank, string eventName, uint eventId, uint template, out string why, out bool moved)
    {
        why = null;
        moved = false;
        if (bank.Nodes.GetValueOrDefault(template) is not (HierarchySoundSfxVoice or HierarchyRandomSequenceContainer))
        {
            why = $"the game no longer has {template}, the Sound or container it copied";
            return null;
        }
        var what = Kind(bank, template);
        bool PlaysIt(uint ev) => bank.Nodes.GetValueOrDefault(ev) is HierarchyEvent he &&
            he.EventActionIds.Any(a => bank.Nodes.GetValueOrDefault(a) is HierarchyEventAction
                                       { EventActionType: EAkActionType.Play } p && p.ReferencedId == template);
        if (eventName is not null && PlaysIt(VoiceLine.Fnv(eventName))) return VoiceLine.Fnv(eventName);
        if (PlaysIt(eventId)) return eventId;
        var players = bank.Objects.Where(o => o.Type == EAKBKHircType.Event && PlaysIt(o.Id)).Select(o => o.Id).ToList();
        if (players.Count == 1)
        {
            moved = true;
            return players[0];
        }
        why = players.Count == 0
            ? $"no event plays {what} directly any more"
            : $"{eventName ?? eventId.ToString()} no longer plays {what}, and {players.Count} other events do, so which one is not known";
        return null;
    }

    /// <summary>
    /// Each layer added to its bank: the copy this build already wrote when there is one (a
    /// staged swap and a layer in one bank both make it), else the bank the build starts from
    /// (an open mod's or the game's). A copy of one of the project's own layers that an earlier
    /// build left there (an open mod built from this project) is taken out first, so it is not
    /// added twice. Every other layer the bank already has -- one an open mod added -- stays, as
    /// everything else an open mod changed does. The banks are written back under
    /// <paramref name="work"/> at their game paths; the paths written are returned relative to
    /// it, the way ModBuilder lists its own.
    /// </summary>
    /// <param name="wemOf">the layer's staged audio, as a wem</param>
    public static Result Apply(GameSession session, IReadOnlyList<ProjectLayer> layers, Func<uint, byte[]> wemOf,
                               string work, BankBase baseBanks)
    {
        var outputs = new List<string>();
        var log = new List<string>();
        foreach (var group in layers.GroupBy(l => l.Bank, StringComparer.OrdinalIgnoreCase))
        {
            var bp = group.Key;
            var dest = Path.Combine(work, bp.Replace('/', Path.DirectorySeparatorChar));
            var rebuilt = File.Exists(dest);
            var src = rebuilt ? File.ReadAllBytes(dest)
                    : (baseBanks ?? BankBase.None).Read(session, bp)
                      ?? throw new InvalidDataException($"{bp} is not in this game, so its added sound(s) cannot be built");
            var bank = HircBank.Parse(Path.GetFileName(bp), src);
            if (!bank.Write().AsSpan().SequenceEqual(src))
                throw new InvalidDataException($"{bp} does not read back as it was; refusing to add to it");
            log.Add($"LAYER {Path.GetFileName(bp)}{(rebuilt ? " (on top of this build's copy)" : "")}:");

            var manifest = LayerManifest.Read(bank);
            // The project's own, by the name its ids are hashed from or its media id.
            bool Ours(LayerManifest.Entry e) => group.Any(l => l.MediaId == e.MediaId || l.Name == e.Name);
            foreach (var old in LayerManifest.TakeOut(bank, manifest, Ours))
                log.Add($"  took out this project's sound on {old.Entry.EventName ?? old.Entry.EventId.ToString()} from an earlier build, to build it again");
            var kept = LayerManifest.Entries(manifest)
                                    .Where(e => !Ours(e) && bank.Nodes.GetValueOrDefault(e.SoundId) is HierarchySoundSfxVoice).ToList();
            if (kept.Count > 0) log.Add($"  kept the {kept.Count} sound(s) the open mod added to this bank");

            var entries = new List<LayerManifest.Entry>();
            foreach (var l in group)
            {
                var ids = l.Ids;
                var what = l.EventName ?? l.Event.ToString();
                if (ids.MediaId != l.MediaId)
                    throw new InvalidDataException($"the added sound on {what} names media {l.MediaId}, " +
                                                   $"but its ids say {ids.MediaId}; remove it and add it again");
                var wem = wemOf(l.MediaId) ?? throw new InvalidDataException($"the added sound on {what} has no audio staged");
                var ev = EventFor(bank, l.EventName, l.Event, l.Template, out var why, out var moved)
                         ?? throw new InvalidDataException($"the added sound on {what} cannot be built: {why}");
                var now = moved ? session.EventName(ev) ?? ev.ToString() : what;
                var template = Heard(bank, ev, l.Template, out var note)
                               ?? throw new InvalidDataException($"the added sound on {what} cannot be built: {note}; remove it");
                var stops = l.Stops ? EventLayer.StopsOf(bank, template) : [];
                var added = EventLayer.Add(bank, ev, wem, ids, template, stops);
                entries.Add(LayerManifest.Of(l.Name, moved ? session.EventName(ev) : l.EventName, ev, added, l.Stops));
                log.Add($"  {what}:");
                if (moved) log.Add($"    no longer plays {Kind(bank, l.Template)}; {now} is the one event that does, and plays it instead");
                if (note is not null) log.Add("    " + note);
                log.AddRange(added.Log.Select(x => "    " + x));
                if (l.Stops && stops.Count == 0)
                    log.Add($"    nothing in this bank stops {Kind(bank, template)} any more, so it plays to the end");
            }
            LayerManifest.Write(bank, manifest, [.. kept, .. entries], bp, session.Bank(bp)?.Read());

            // Read back as the game will: every event ends in its layer's Play, and that plays it,
            // and the manifest names each one.
            var bytes = bank.Write();
            var check = HircBank.Parse("check", bytes);
            var named = LayerManifest.Entries(LayerManifest.Read(check)).Select(e => e.MediaId).ToHashSet();
            foreach (var e in entries)
            {
                var ok = check.Nodes.GetValueOrDefault(e.EventId) is HierarchyEvent ev && ev.EventActionIds.Contains(e.PlayId) &&
                         check.Nodes.ContainsKey(e.SoundId) && check.Media.Any(m => m.Id == e.MediaId) && named.Contains(e.MediaId);
                if (!ok) throw new InvalidDataException($"{bp}: the added sound on {e.EventName ?? e.EventId.ToString()} did not read back");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            outputs.Add(Path.GetRelativePath(work, dest));
            log.Add($"  {src.Length:N0} B -> {bytes.Length:N0} B");
        }
        return new Result(outputs, log);
    }

    /// <summary>What putting an old mod's layers back onto a patched bank came to.</summary>
    public sealed record Readded(byte[] Bytes, List<string> Log, int Added, int Refused);

    /// <summary>
    /// For an update after a game patch: layers taken out of the old mod's bank, added again to
    /// <paramref name="onto"/> (the patched bank, with the update's swaps already in it). Each is
    /// anchored on the Sound it copied. Its event is the one it was on, by name (an event's id is
    /// the hash of its name) or else by id; where that event no longer plays the Sound, the one
    /// event that does is taken, and said. A Sound the patch removed, or one that several events
    /// play without the old one among them, is refused with the reason, never guessed at.
    /// </summary>
    /// <param name="game">the patched game bank, named in the manifest as what was built on</param>
    public static Readded Readd(byte[] onto, byte[] game, string bankPath, IReadOnlyList<LayerManifest.Taken> layers,
                                Func<uint, bool> mediaTaken, Func<uint, string> eventName)
    {
        // Whatever goes wrong here is this bank's added sounds refused, said, and the update goes
        // on: the rebase's own result for the bank is already written.
        try { return ReaddCore(onto, game, bankPath, layers, mediaTaken, eventName); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Readded(null, [$"  ADDED SOUNDS: none of the {layers.Count} put back, {ex.Message}"], 0, layers.Count);
        }
    }

    private static Readded ReaddCore(byte[] onto, byte[] game, string bankPath, IReadOnlyList<LayerManifest.Taken> layers,
                                     Func<uint, bool> mediaTaken, Func<uint, string> eventName)
    {
        var log = new List<string>();
        var bank = HircBank.Parse(Path.GetFileName(bankPath), onto);
        if (!bank.Write().AsSpan().SequenceEqual(onto))
            return new Readded(null, [$"  ADDED SOUNDS: {bankPath} does not read back as it was; none of the {layers.Count} put back"], 0, layers.Count);
        var manifest = LayerManifest.Read(bank);
        var entries = new List<LayerManifest.Entry>();
        int added = 0, refused = 0;
        foreach (var t in layers)
        {
            var e = t.Entry;
            var what = e.EventName ?? e.EventId.ToString();
            string Refuse(string why) { refused++; return $"  ADDED SOUND on {what}: NOT put back, {why}"; }
            if (t.Wem is null) { log.Add(Refuse("its audio is not in the mod's bank")); continue; }
            var ev = EventFor(bank, e.EventName, e.EventId, e.Template, out var why, out var moved);
            if (ev is null) { log.Add(Refuse(why)); continue; }
            var how = moved ? $" (on {eventName(ev.Value) ?? ev.Value.ToString()}, the one event that now plays the Sound it copies)" : "";
            var name = NameFor(bank, e.Name.Length > 0 ? e.Name : $"{what}/{e.MediaId}", mediaTaken);
            // A layer an older version made from a Sound the game has muted copies what is heard instead.
            var template = Heard(bank, ev.Value, e.Template, out var note);
            if (template is null) { log.Add(Refuse(note)); continue; }
            if (note is not null) how += $" ({note})";
            // Stopped where the game stops what it copies, found again in the patched bank; or, for
            // one made with a named stop event (--addlayer --stop), by those events, while they
            // still stop it.
            var stops = e.StopsWithTemplate ? EventLayer.StopsOf(bank, template.Value)
                      : e.Stops.Select(s => s.EventId).Distinct().Where(se => EventLayer.StopsOf(bank, template.Value).Contains(se)).ToList();
            if (!e.StopsWithTemplate && stops.Count < e.Stops.Select(s => s.EventId).Distinct().Count())
                log.Add($"  ADDED SOUND on {what}: {e.Stops.Count - stops.Count} of its stop event(s) no longer stop {Kind(bank, template.Value)}");
            try
            {
                var a = EventLayer.Add(bank, ev.Value, t.Wem, EventLayer.Ids.For(name), template.Value, stops);
                entries.Add(LayerManifest.Of(name, moved ? eventName(ev.Value) : e.EventName, ev.Value, a, e.StopsWithTemplate));
                added++;
                log.Add($"  ADDED SOUND on {what}: put back{how}, copying {Kind(bank, template.Value)}" +
                        (stops.Count > 0 ? $", stopped by {stops.Count} event(s)" : e.StopsWithTemplate ? ", nothing stops that Sound now, so it plays to the end" : ""));
            }
            catch (InvalidDataException ex) { log.Add(Refuse(ex.Message)); }
        }
        if (added == 0) return new Readded(null, log, 0, refused);
        LayerManifest.Write(bank, manifest, entries, bankPath, game);
        return new Readded(bank.Write(), log, added, refused);
    }
}
