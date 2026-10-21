using System.IO;
using CUE4Parse.UE4.Wwise.Enums;
using CUE4Parse.UE4.Wwise.Enums.Flags;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

/// <summary>
/// A sound added to an event the game already has, beside what it plays rather than instead of
/// it: music under an ult, say, with every one of the ult's own sounds left as they are.
///
/// An event is a list of actions, and a Play action points at a Sound or a container. A layer is
/// one more of each: a copy of something the event already plays and the game lets you hear (so
/// it sits in the same mixer and comes out of the same bus at the same level), carrying its own
/// medium, and a Play action for it appended to the event. When the event plays such a Sound
/// directly, the layer is a copy of that Sound. When what you hear is inside a random container
/// (1023's ult start: the only Sound it plays directly is one the game has muted), the layer is a
/// one-child copy of that container holding a copy of one of its Sounds, so the container's own
/// level, aux sends and routing come along too. The event keeps its id, so the game's event
/// asset is untouched and the mod is the rebuilt .bnk alone. Optionally other events stop it:
/// copies of the Stops they already make on what was copied, fade and all.
///
/// The same pieces the voice-line builder adds a new line with (VoiceLineBuilder.AddLine), and
/// the same rule: every writer reproduces the game's own object before it writes a new one.
/// </summary>
public static class EventLayer
{
    /// <param name="Name">What the layer's ids are hashed from; one name, one layer.</param>
    public sealed record Ids(uint SoundId, uint PlayId, uint StopId, uint MediaId)
    {
        public static Ids For(string name) => new(
            VoiceLine.Fnv($"xzoundwave/layer/{name}/sound"),
            VoiceLine.Fnv($"xzoundwave/layer/{name}/play"),
            VoiceLine.Fnv($"xzoundwave/layer/{name}/stop"),
            VoiceLine.Fnv($"xzoundwave/layer/{name}/media"));

        /// <summary>The Stop put in the i-th stop event: the first is <see cref="StopId"/>.</summary>
        public uint StopAt(int i) => i == 0 ? StopId : VoiceLine.Fnv($"{StopId}/{i}");

        /// <summary>The layer's own copy of a random container, when it is made inside one.</summary>
        public uint ContainerId => VoiceLine.Fnv($"{SoundId}/container");
    }

    /// <summary>What was added, for a log and the bank's manifest.</summary>
    /// <param name="Template">what the event's Play pointed at and the layer copies: a Sound, or the random container holding the sound</param>
    /// <param name="MixerId">the mixer the layer sits in</param>
    /// <param name="Container">the layer's copy of that container; 0 when the layer is a Sound beside the template</param>
    public sealed record Added(Ids Ids, uint Template, uint MixerId, uint Container, IReadOnlyList<uint> StoppedBy, List<string> Log);

    /// <summary>
    /// What a layer on this event would copy: a Sound it plays directly that the game lets you
    /// hear, the one playing <paramref name="media"/> when it is one of them; otherwise a random
    /// container it plays whose Sounds are heard, the one holding <paramref name="media"/> when
    /// one does. Null, with the reason, when it plays neither.
    /// </summary>
    public static uint? TemplateFor(HircBank bank, uint startEvent, uint? media, out string reason)
    {
        reason = null;
        if (bank.Nodes.GetValueOrDefault(startEvent) is not HierarchyEvent ev)
        {
            reason = $"{bank.Name} has no event {startEvent}";
            return null;
        }
        var targets = ev.EventActionIds
            .Select(a => bank.Nodes.GetValueOrDefault(a))
            .OfType<HierarchyEventAction>()
            .Where(a => a.EventActionType == EAkActionType.Play)
            .Select(a => bank.Nodes.GetValueOrDefault(a.ReferencedId))
            .Where(n => n is not null).ToList();
        var heard = targets.OfType<HierarchySoundSfxVoice>().Where(s => Heard(bank, s)).ToList();
        if (heard.Count > 0) return (heard.FirstOrDefault(s => s.Source.SourceId == media) ?? heard[0]).Id;
        var rooms = targets.OfType<HierarchyRandomSequenceContainer>().Where(c => ContainerChild(bank, c, null, out _) is not null).ToList();
        if (rooms.Count > 0)
            return (rooms.FirstOrDefault(c => HeardChildren(bank, c).Any(s => s.Source.SourceId == media)) ?? rooms[0]).Id;

        var bits = new List<string>();
        var direct = targets.OfType<HierarchySoundSfxVoice>().ToList();
        if (direct.Any(s => !IsAudio(s))) bits.Add("a generated sound (a Wwise plugin, no audio)");
        if (direct.Any(s => IsAudio(s) && IsMuted(bank, s.Id))) bits.Add("a sound the game has muted");
        foreach (var c in targets.OfType<HierarchyRandomSequenceContainer>())
        {
            ContainerChild(bank, c, null, out var why);
            bits.Add(why);
        }
        if (targets.Any(t => t is HierarchyLayerContainer)) bits.Add("a layer container, which a sound cannot be added beside yet");
        if (targets.Any(t => t is HierarchySwitchContainer)) bits.Add("a switch container, which a sound cannot be added beside yet");
        if (targets.Any(t => t is not (HierarchySoundSfxVoice or HierarchyRandomSequenceContainer or HierarchyLayerContainer or HierarchySwitchContainer)))
            bits.Add("music or another kind of object");
        reason = bits.Count == 0 ? "this event plays nothing"
               : "nothing it plays can be copied: it plays " + string.Join("; ", bits.Distinct());
        return null;
    }

    /// <summary>
    /// A Sound that plays audio: its source is a codec's. A source plugin's (silence, a tone, a
    /// synth) has no medium to stand in for, and its in-memory flags say nothing about one.
    /// </summary>
    public static bool IsAudio(HierarchySoundSfxVoice s) => s.Source.Plugin.Type == EAkPluginType.Codec;

    /// <summary>A Sound with audio that the game lets you hear.</summary>
    public static bool Heard(HircBank bank, HierarchySoundSfxVoice s) => IsAudio(s) && !IsMuted(bank, s.Id);

    /// <summary>
    /// An object the game will never let anyone hear: its fixed level down the chain is at Wwise's
    /// floor -- volume and make-up gain, or for what goes to the output bus those and the
    /// output-bus volume, when no aux send carries it -- and no game parameter or state anywhere
    /// in the chain can raise it. 1023's ult start plays one (output bus at -96 dB on the Sound
    /// and again on its mixer), and a copy of it was silent in game. Only a certainty counts:
    /// anything that could raise the level leaves the object counted as heard.
    /// </summary>
    public static bool IsMuted(HircBank bank, uint id)
    {
        float level = 0, outputBus = 0;
        var aux = false;
        var seen = new HashSet<uint>();
        for (var cur = id; seen.Add(cur) && BaseOf(bank.Nodes.GetValueOrDefault(cur)) is { } bp; cur = bp.DirectParentId)
        {
            if ((bp.RtpcList ?? []).Length > 0 || (bp.StateGroups ?? []).Length > 0) return false;
            foreach (var p in bp.PropBundle.Props ?? [])
                if (p.Id is 0x00 or 0x06) level += p.Value.f32;
                else if (p.Id == 0x18) outputBus += p.Value.f32;
            if (bp.AuxParams is { } a && SendsToAux(a)) aux = true;
            if (bp.DirectParentId == 0) break;
        }
        return level <= -60 || (!aux && level + outputBus <= -60);
    }

    /// <summary>
    /// An object that sends to an aux bus besides its output bus: game-defined sends, or its own.
    /// For these bank versions CUE4Parse reads "use game-defined aux sends" into the flag it calls
    /// OverridePriority (bit 1 of the flags byte, where the bank format keeps it) and leaves its
    /// UseGameAuxSends false, so the bit is read here directly.
    /// </summary>
    public static bool SendsToAux(CUE4Parse.UE4.Wwise.Objects.AkAuxParams a) =>
        a.UseGameAuxSends || (a.AuxParams & EAuxParamsFlags.OverridePriority) != 0 || (a.AuxIds ?? []).Any(x => x != 0);

    private static BaseHierarchy BaseOf(AbstractHierarchy node) =>
        node?.GetType().GetField("BaseParams")?.GetValue(node) as BaseHierarchy;

    /// <summary>
    /// The Sounds a random or sequence container plays that the game lets you hear: its own
    /// children, those its playlist names (the playlist is what the container picks from, so a
    /// child it leaves out is never played).
    /// </summary>
    private static List<HierarchySoundSfxVoice> HeardChildren(HircBank bank, HierarchyRandomSequenceContainer c)
    {
        var played = (c.Playlist ?? []).Select(p => p.PlayId).ToHashSet();
        return (c.ChildIds ?? []).Where(played.Contains).Select(k => bank.Nodes.GetValueOrDefault(k)).OfType<HierarchySoundSfxVoice>()
                                 .Where(s => Heard(bank, s)).ToList();
    }

    /// <summary>
    /// The Sound a layer made inside this random container copies: one of its own that is heard,
    /// the one playing <paramref name="media"/> when it is among them. Null, with what the
    /// container is, when it sits in no mixer, plays its sounds in a loop, or holds none that is heard.
    /// </summary>
    private static HierarchySoundSfxVoice ContainerChild(HircBank bank, HierarchyRandomSequenceContainer c, uint? media, out string why)
    {
        why = null;
        if (bank.Nodes.GetValueOrDefault(c.BaseParams.DirectParentId) is not HierarchyActorMixer)
        {
            why = "a random container that sits in no mixer";
            return null;
        }
        if ((c.PlaylistFlags & EPlayListFlags.IsContinuous) != 0 && c.LoopCount != 1)
        {
            why = "a random container that plays its sounds in a loop";
            return null;
        }
        var kids = HeardChildren(bank, c);
        if (kids.Count == 0)
        {
            why = "a random container whose sounds are muted or generated";
            return null;
        }
        return kids.FirstOrDefault(s => s.Source.SourceId == media) ?? kids[0];
    }

    /// <summary>
    /// The events in this bank that can play a Sound with this medium, through containers of any
    /// depth: the event a row stands for may be one that only stops its sound.
    /// </summary>
    public static List<uint> PlayersOf(HircBank bank, uint media) =>
        bank.Objects.Where(o => o.Node is HierarchyEvent ev &&
                                ev.EventActionIds.Any(a => bank.Nodes.GetValueOrDefault(a) is HierarchyEventAction
                                                           { EventActionType: EAkActionType.Play } p && Reaches(bank, p.ReferencedId, media, [])))
                    .Select(o => o.Id).ToList();

    /// <summary>Whether playing this object can play a Sound with this medium, through its children at any depth.</summary>
    private static bool Reaches(HircBank bank, uint id, uint media, HashSet<uint> seen) =>
        seen.Add(id) && bank.Nodes.GetValueOrDefault(id) switch
        {
            HierarchySoundSfxVoice s => s.Source.SourceId == media,
            { } n when HircBank.ChildrenOf(n) is { } kids => kids.Any(k => Reaches(bank, k, media, seen)),
            _ => false,
        };

    /// <summary>
    /// What a layer leaves out of the Sound it copies: its Loop. The file plays once, to its end or
    /// until it is stopped; a copy of a looping ult sound that nothing stopped would never end.
    /// </summary>
    private static readonly HashSet<byte> PlaysOnce = [0x3A];

    /// <summary>The events in this bank with a Stop on <paramref name="target"/> (a Sound or a container): where the game ends it.</summary>
    public static List<uint> StopsOf(HircBank bank, uint target) =>
        bank.Objects.Where(o => o.Node is HierarchyEvent ev &&
                                ev.EventActionIds.Any(a => bank.Nodes.GetValueOrDefault(a) is HierarchyEventAction
                                                           { EventActionType: EAkActionType.Stop } s && s.ReferencedId == target))
                    .Select(o => o.Id).ToList();

    /// <summary>
    /// Add a layer to <paramref name="startEvent"/> in this bank.
    /// </summary>
    /// <param name="template">What to copy, by id: a Sound the event plays, or a random container it plays; null takes <see cref="TemplateFor"/>'s choice.</param>
    /// <param name="stopEvents">Events that stop the template, each to stop the layer too; none lets it play out.</param>
    public static Added Add(HircBank bank, uint startEvent, byte[] wem, Ids ids, uint? template = null, IReadOnlyList<uint> stopEvents = null)
    {
        var log = new List<string>();
        stopEvents ??= [];
        var target = template ?? TemplateFor(bank, startEvent, null, out var none)
                     ?? throw new InvalidDataException($"{bank.Name}: event {startEvent}: {none}");
        var inContainer = bank.Nodes.GetValueOrDefault(target) is HierarchyRandomSequenceContainer;
        var mine = new[] { ids.SoundId, ids.PlayId }.Concat(inContainer ? [ids.ContainerId] : Array.Empty<uint>())
                                                     .Concat(stopEvents.Select((_, i) => ids.StopAt(i)));
        foreach (var id in mine)
            if (bank.Nodes.ContainsKey(id)) throw new InvalidDataException($"{bank.Name}: id {id} is taken");
        if (bank.Media.Any(m => m.Id == ids.MediaId)) throw new InvalidDataException($"{bank.Name}: media {ids.MediaId} is taken");

        // ---- the event, and the Play it already makes that the layer copies --------------------
        var evObj = Event(bank, startEvent);
        var ev = (HierarchyEvent)evObj.Node;
        var playObj = bank.Objects.FirstOrDefault(o => ev.EventActionIds.Contains(o.Id) &&
                                                       o.Node is HierarchyEventAction { EventActionType: EAkActionType.Play } a &&
                                                       a.ReferencedId == target)
                      ?? throw new InvalidDataException($"{bank.Name}: event {startEvent} does not play {target}");
        var aSpan = HircLayout.Action(playObj.Body, bank.Version);
        Reproduces(HircWriter.Action(playObj.Body, aSpan, bank.Version, aSpan.Parsed.Id, aSpan.Parsed.ReferencedId), playObj.Body, $"play action {aSpan.Parsed.Id}");

        // ---- what it plays: the Sound to copy, and (inside a container) the container ---------
        HircObject tSndObj, cObj = null;
        HircLayout.ContainerSpan cSpan = null;
        uint mixerId;
        if (inContainer)
        {
            cObj = bank.Objects.Single(o => o.Id == target);
            var c = (HierarchyRandomSequenceContainer)cObj.Node;
            var child = ContainerChild(bank, c, null, out var why)
                        ?? throw new InvalidDataException($"{bank.Name}: event {startEvent} plays {why}; there is nothing to copy");
            cSpan = HircLayout.RandomContainer(cObj.Body, bank.Version);
            Reproduces(HircWriter.RandomContainer(cObj.Body, cSpan, bank.Version, c.Id, c.ChildIds,
                                                  [.. c.Playlist.Select(p => (p.PlayId, p.Weight))]), cObj.Body, $"container {c.Id}");
            tSndObj = bank.Objects.Single(o => o.Id == child.Id);
            mixerId = cSpan.Base.Parsed.DirectParentId;
        }
        else
        {
            tSndObj = bank.Objects.Single(o => o.Id == target);
            if (tSndObj.Node is not HierarchySoundSfxVoice ts)
                throw new InvalidDataException($"{bank.Name}: {target} is a {tSndObj.Type}, which a sound cannot be added beside yet");
            if (!IsAudio(ts))
                throw new InvalidDataException($"{bank.Name}: Sound {ts.Id} is a source plugin's, not audio; there is nothing to copy");
            if (IsMuted(bank, ts.Id))
                throw new InvalidDataException($"{bank.Name}: Sound {ts.Id} is muted in the game, so a copy of it would be silent too");
            mixerId = ts.BaseParams.DirectParentId;
        }
        var sSpan = HircLayout.Sound(tSndObj.Body, bank.Version);
        Reproduces(HircWriter.SoundAsIs(tSndObj.Body, sSpan, bank.Version), tSndObj.Body, $"sound {sSpan.Parsed.Id}");
        var mixerObj = bank.Objects.Single(o => o.Id == mixerId);
        if (mixerObj.Node is not HierarchyActorMixer)
            throw new InvalidDataException($"{bank.Name}: {(inContainer ? $"container {target}" : $"Sound {sSpan.Parsed.Id}")}'s parent is a {mixerObj.Type}, not a mixer; not supported yet");
        var mSpan = HircLayout.Mixer(mixerObj.Body, bank.Version);
        Reproduces(HircWriter.Mixer(mixerObj.Body, mSpan, bank.Version, mSpan.Parsed.ChildIds), mixerObj.Body, $"mixer {mixerId}");
        log.Add(inContainer
            ? $"copies random container {target} with one of its Sounds, {sSpan.Parsed.Id} (media {sSpan.Parsed.Source.SourceId}), in mixer {mixerId}, which event {startEvent} plays"
            : $"copies Sound {sSpan.Parsed.Id} (media {sSpan.Parsed.Source.SourceId}) in mixer {mixerId}, which event {startEvent} plays");

        // A playback limit above where the layer goes (1023's voice mixer: one sound per character,
        // the older one dropped) would count the layer as one of the game's own and cut the line it
        // plays beside -- in game, the music played and the voice line did not. The layer is left
        // out of that count with the flag Wwise has for it; only where such a limit exists, so a
        // layer anywhere else is written exactly as before.
        var limit = LimitAbove(bank, mixerId);
        if (limit is { } lim)
            log.Add($"mixer {lim.Id} plays at most {lim.Max} sound(s) at once per {(lim.Global ? "game" : "character")}; " +
                    "the added sound is left out of that count, so it does not cut off the game's own");

        // ---- the Stops, if asked for: one each stop event already makes on the template --------
        var stops = new List<(HircObject Event, HircObject Stop, HircLayout.ActionSpan Span)>();
        foreach (var se in stopEvents)
        {
            var stopEvObj = Event(bank, se);
            var sev = (HierarchyEvent)stopEvObj.Node;
            var stopObj = bank.Objects.FirstOrDefault(o => sev.EventActionIds.Contains(o.Id) &&
                                                           o.Node is HierarchyEventAction { EventActionType: EAkActionType.Stop } a &&
                                                           a.ReferencedId == target)
                          ?? throw new InvalidDataException($"{bank.Name}: event {se} does not stop {target}, so there is no Stop to copy");
            var stopSpan = HircLayout.Action(stopObj.Body, bank.Version);
            Reproduces(HircWriter.Action(stopObj.Body, stopSpan, bank.Version, stopSpan.Parsed.Id, stopSpan.Parsed.ReferencedId), stopObj.Body, $"stop action {stopSpan.Parsed.Id}");
            stops.Add((stopEvObj, stopObj, stopSpan));
            log.Add($"stops with event {se}, the way it stops {(inContainer ? "container" : "Sound")} {target}");
        }

        // ---- the new Sound: in memory, as the bank's own in-memory Sounds are flagged -----------
        var inMemory = bank.Nodes.Values.OfType<HierarchySoundSfxVoice>().FirstOrDefault(s => s.Source.SourceType == EAKBKSourceType.Data && IsAudio(s));
        var flags = inMemory is not null ? (byte)inMemory.Source.BankSourceFlags
                                         : (byte)(sSpan.Parsed.Source.BankSourceFlags & ~EBankSourceFlags.Prefetch);
        var sound = new HircObject { RawType = tSndObj.RawType, Type = tSndObj.Type,
            Body = HircWriter.Sound(tSndObj.Body, sSpan, bank.Version, ids.SoundId, EAKBKSourceType.Data, ids.MediaId, (uint)wem.Length, flags,
                                    directParentId: inContainer ? ids.ContainerId : null, dropProps: PlaysOnce,
                                    ignoreParentLimit: !inContainer && limit is not null) };
        sound.Node = HircBank.ReadExact(sound.Type, sound.Body, bank.Version, "layer sound");
        if (((HierarchySoundSfxVoice)sound.Node).BaseParams.PropBundle.Props?.Any(p => PlaysOnce.Contains(p.Id)) == true)
            throw new InvalidDataException($"{bank.Name}: the copy of Sound {sSpan.Parsed.Id} still loops");
        if (sSpan.Parsed.BaseParams.PropBundle.Props?.Any(p => PlaysOnce.Contains(p.Id)) == true)
            log.Add($"Sound {sSpan.Parsed.Id} loops; the added sound plays once");
        bank.AddMedia(ids.MediaId, wem);

        // ---- into the mixer: the Sound itself, or the container copy holding it ----------------
        var placed = inContainer ? ids.ContainerId : ids.SoundId;
        var kids = mSpan.Parsed.ChildIds.Append(placed).OrderBy(x => x).ToArray();
        mixerObj.Body = HircWriter.Mixer(mixerObj.Body, mSpan, bank.Version, kids);
        mixerObj.Node = HircBank.ReadExact(mixerObj.Type, mixerObj.Body, bank.Version, "mixer");
        bank.Nodes[mixerId] = mixerObj.Node;
        var next = kids.Where(k => k > placed).Cast<uint?>().FirstOrDefault();
        var at = next is { } n ? bank.SubtreeStart(n) : bank.Objects.IndexOf(mixerObj);
        bank.Objects.Insert(at, sound);
        bank.Nodes[ids.SoundId] = sound.Node;
        if (inContainer)
        {
            // One child, with the weight the copied Sound had among the original's, and everything
            // else -- level, aux sends, positioning, play mode -- the original container's own.
            var weight = cSpan.Parsed.Playlist.First(p => p.PlayId == tSndObj.Id).Weight;
            List<(uint, int)> playlist = [(ids.SoundId, weight)];
            var container = new HircObject { RawType = cObj.RawType, Type = cObj.Type,
                Body = HircWriter.RandomContainer(cObj.Body, cSpan, bank.Version, ids.ContainerId, [ids.SoundId], playlist,
                                                  ignoreParentLimit: limit is not null) };
            container.Node = HircBank.ReadExact(container.Type, container.Body, bank.Version, "layer container");
            // After its Sound: banks lay a subtree out children first, then the parent.
            bank.Objects.Insert(at + 1, container);
            bank.Nodes[ids.ContainerId] = container.Node;
        }

        // ---- its Play, appended to the event -----------------------------------------------------
        var play = new HircObject { RawType = playObj.RawType, Type = playObj.Type,
            Body = HircWriter.Action(playObj.Body, aSpan, bank.Version, ids.PlayId, placed) };
        play.Node = HircBank.ReadExact(play.Type, play.Body, bank.Version, "layer play");
        bank.Objects.Insert(bank.Objects.IndexOf(playObj) + 1, play);
        bank.Nodes[ids.PlayId] = play.Node;
        Rewrite(bank, evObj, [.. ev.EventActionIds, ids.PlayId]);

        // ---- and its Stops, one appended to each stop event --------------------------------------
        for (var i = 0; i < stops.Count; i++)
        {
            var (stopEvObj, stopObj, stopSpan) = stops[i];
            var stopId = ids.StopAt(i);
            var stop = new HircObject { RawType = stopObj.RawType, Type = stopObj.Type,
                Body = HircWriter.Action(stopObj.Body, stopSpan, bank.Version, stopId, placed) };
            stop.Node = HircBank.ReadExact(stop.Type, stop.Body, bank.Version, "layer stop");
            bank.Objects.Insert(bank.Objects.IndexOf(stopObj) + 1, stop);
            bank.Nodes[stopId] = stop.Node;
            var actions = ((HierarchyEvent)stopEvObj.Node).EventActionIds.ToList();
            if (ReferenceEquals(stopEvObj, evObj))
            {
                // The start event stops the template as well (a retrigger: Stop, then Play, so a new
                // ult cuts the last one off). The copy keeps the template's order around its own
                // Play: appended after it, the Stop would end the layer the moment it started.
                var stopFirst = Array.IndexOf(ev.EventActionIds, stopObj.Id) < Array.IndexOf(ev.EventActionIds, playObj.Id);
                actions.Insert(actions.IndexOf(ids.PlayId) + (stopFirst ? 0 : 1), stopId);
            }
            else actions.Add(stopId);
            Rewrite(bank, stopEvObj, [.. actions]);
        }

        log.Add($"added Sound {ids.SoundId}{(inContainer ? $" in container {ids.ContainerId}" : "")} with media {ids.MediaId} ({wem.Length:N0} B), Play {ids.PlayId}" +
                (stops.Count > 0 ? $", {stops.Count} Stop(s) from {ids.StopId}" : ""));
        return new Added(ids, target, mixerId, inContainer ? ids.ContainerId : 0, stopEvents, log);
    }

    /// <summary>The nearest object from <paramref name="id"/> upwards that limits how many of its sounds play at once.</summary>
    private static (uint Id, int Max, bool Global)? LimitAbove(HircBank bank, uint id)
    {
        var seen = new HashSet<uint>();
        for (var cur = id; seen.Add(cur) && BaseOf(bank.Nodes.GetValueOrDefault(cur)) is { } bp; cur = bp.DirectParentId)
        {
            if (bp.AdvSettingsParams.MaxNumInstance > 0) return (cur, bp.AdvSettingsParams.MaxNumInstance, bp.AdvSettingsParams.IsGlobalLimit);
            if (bp.DirectParentId == 0) break;
        }
        return null;
    }

    private static HircObject Event(HircBank bank, uint id)
    {
        var obj = bank.Objects.SingleOrDefault(o => o.Id == id && o.Type == EAKBKHircType.Event)
                  ?? throw new InvalidDataException($"{bank.Name}: no event {id}");
        var ev = (HierarchyEvent)obj.Node;
        Reproduces(HircWriter.Event(ev.Id, ev.EventActionIds, bank.Version), obj.Body, $"event {id}");
        return obj;
    }

    /// <summary>An event with these actions, in this order.</summary>
    internal static void Rewrite(HircBank bank, HircObject evObj, uint[] actions)
    {
        evObj.Body = HircWriter.Event(evObj.Id, actions, bank.Version);
        evObj.Node = HircBank.ReadExact(evObj.Type, evObj.Body, bank.Version, "event");
        bank.Nodes[evObj.Id] = evObj.Node;
    }

    private static void Reproduces(byte[] written, byte[] game, string what)
    {
        if (!written.AsSpan().SequenceEqual(game))
            throw new InvalidDataException($"the writer does not reproduce the game's {what}; refusing to write a new one from it");
    }
}
