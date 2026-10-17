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
/// An event is a list of actions, and a Play action points at a Sound. A layer is one more of
/// each: a Sound made from one the event already plays (so it sits in the same mixer and comes
/// out of the same bus at the same level), carrying its own medium, and a Play action for it
/// appended to the event. The event keeps its id, so the game's event asset is untouched and
/// the mod is the rebuilt .bnk alone. Optionally a second event stops it: a copy of a Stop that
/// event already makes on the template Sound, fade and all, so the layer ends when the sound it
/// was made from does.
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
    }

    /// <summary>What was added, for a log.</summary>
    public sealed record Added(Ids Ids, uint TemplateSoundId, uint MixerId, IReadOnlyList<uint> StoppedBy, List<string> Log);

    /// <summary>
    /// The Sound a layer on this event would copy: the one playing <paramref name="media"/> when
    /// the event plays it directly, else the first Sound it plays. Null when it plays none
    /// directly (a random or switch container, music), with the reason.
    /// </summary>
    public static uint? TemplateFor(HircBank bank, uint startEvent, uint? media, out string reason)
    {
        reason = null;
        if (bank.Nodes.GetValueOrDefault(startEvent) is not HierarchyEvent ev)
        {
            reason = $"{bank.Name} has no event {startEvent}";
            return null;
        }
        var sounds = ev.EventActionIds
            .Select(a => bank.Nodes.GetValueOrDefault(a))
            .OfType<HierarchyEventAction>()
            .Where(a => a.EventActionType == EAkActionType.Play)
            .Select(a => bank.Nodes.GetValueOrDefault(a.ReferencedId))
            .OfType<HierarchySoundSfxVoice>().Where(IsAudio).ToList();
        if (sounds.Count == 0)
        {
            reason = "this event plays its sounds through a container (random, switch or music), " +
                     "and a sound can only be added beside one it plays directly";
            return null;
        }
        return (sounds.FirstOrDefault(s => s.Source.SourceId == media) ?? sounds[0]).Id;
    }

    /// <summary>
    /// A Sound that plays audio: its source is a codec's. A source plugin's (silence, a tone, a
    /// synth) has no medium to stand in for, and its in-memory flags say nothing about one.
    /// </summary>
    public static bool IsAudio(HierarchySoundSfxVoice s) => s.Source.Plugin.Type == EAkPluginType.Codec;

    /// <summary>
    /// What a layer leaves out of the Sound it copies: its Loop. The file plays once, to its end or
    /// until it is stopped; a copy of a looping ult sound that nothing stopped would never end.
    /// </summary>
    private static readonly HashSet<byte> PlaysOnce = [0x3A];

    /// <summary>The events in this bank with a Stop on <paramref name="sound"/>: where the game ends it.</summary>
    public static List<uint> StopsOf(HircBank bank, uint sound) =>
        bank.Objects.Where(o => o.Node is HierarchyEvent ev &&
                                ev.EventActionIds.Any(a => bank.Nodes.GetValueOrDefault(a) is HierarchyEventAction
                                                           { EventActionType: EAkActionType.Stop } s && s.ReferencedId == sound))
                    .Select(o => o.Id).ToList();

    /// <summary>
    /// Add a layer to <paramref name="startEvent"/> in this bank.
    /// </summary>
    /// <param name="template">The Sound to copy, by id; null takes the first Sound the event plays.</param>
    /// <param name="stopEvents">Events that stop the template Sound, each to stop the layer too; none lets it play out.</param>
    public static Added Add(HircBank bank, uint startEvent, byte[] wem, Ids ids, uint? template = null, IReadOnlyList<uint> stopEvents = null)
    {
        var log = new List<string>();
        stopEvents ??= [];
        foreach (var id in new[] { ids.SoundId, ids.PlayId }.Concat(stopEvents.Select((_, i) => ids.StopAt(i))))
            if (bank.Nodes.ContainsKey(id)) throw new InvalidDataException($"{bank.Name}: id {id} is taken");
        if (bank.Media.Any(m => m.Id == ids.MediaId)) throw new InvalidDataException($"{bank.Name}: media {ids.MediaId} is taken");

        // ---- the event, and the Play it already makes that the layer copies --------------------
        var evObj = Event(bank, startEvent);
        var ev = (HierarchyEvent)evObj.Node;
        var plays = bank.Objects.Where(o => ev.EventActionIds.Contains(o.Id) &&
                                            o.Node is HierarchyEventAction { EventActionType: EAkActionType.Play } a &&
                                            bank.Nodes.GetValueOrDefault(a.ReferencedId) is HierarchySoundSfxVoice).ToList();
        var playObj = template is { } want
            ? plays.FirstOrDefault(o => ((HierarchyEventAction)o.Node).ReferencedId == want)
              ?? throw new InvalidDataException($"{bank.Name}: event {startEvent} plays no Sound {want}")
            : plays.FirstOrDefault() ?? throw new InvalidDataException($"{bank.Name}: event {startEvent} plays no Sound to copy");
        var aSpan = HircLayout.Action(playObj.Body, bank.Version);
        Reproduces(HircWriter.Action(playObj.Body, aSpan, bank.Version, aSpan.Parsed.Id, aSpan.Parsed.ReferencedId), playObj.Body, $"play action {aSpan.Parsed.Id}");

        // ---- the Sound it plays, and the mixer the copy goes into -------------------------------
        var tSndObj = bank.Objects.Single(o => o.Id == aSpan.Parsed.ReferencedId);
        if (!IsAudio((HierarchySoundSfxVoice)tSndObj.Node))
            throw new InvalidDataException($"{bank.Name}: Sound {tSndObj.Id} is a source plugin's, not audio; there is nothing to copy");
        var sSpan = HircLayout.Sound(tSndObj.Body, bank.Version);
        Reproduces(HircWriter.SoundAsIs(tSndObj.Body, sSpan, bank.Version), tSndObj.Body, $"sound {sSpan.Parsed.Id}");
        var mixerId = sSpan.Parsed.BaseParams.DirectParentId;
        var mixerObj = bank.Objects.Single(o => o.Id == mixerId);
        if (mixerObj.Node is not HierarchyActorMixer)
            throw new InvalidDataException($"{bank.Name}: Sound {sSpan.Parsed.Id}'s parent is a {mixerObj.Type}, not a mixer; not supported yet");
        var mSpan = HircLayout.Mixer(mixerObj.Body, bank.Version);
        Reproduces(HircWriter.Mixer(mixerObj.Body, mSpan, bank.Version, mSpan.Parsed.ChildIds), mixerObj.Body, $"mixer {mixerId}");
        log.Add($"copies Sound {sSpan.Parsed.Id} (media {sSpan.Parsed.Source.SourceId}) in mixer {mixerId}, which event {startEvent} plays");

        // ---- the Stops, if asked for: one each stop event already makes on the template --------
        var stops = new List<(HircObject Event, HircObject Stop, HircLayout.ActionSpan Span)>();
        foreach (var se in stopEvents)
        {
            var stopEvObj = Event(bank, se);
            var sev = (HierarchyEvent)stopEvObj.Node;
            var stopObj = bank.Objects.FirstOrDefault(o => sev.EventActionIds.Contains(o.Id) &&
                                                           o.Node is HierarchyEventAction { EventActionType: EAkActionType.Stop } a &&
                                                           a.ReferencedId == sSpan.Parsed.Id)
                          ?? throw new InvalidDataException($"{bank.Name}: event {se} does not stop Sound {sSpan.Parsed.Id}, so there is no Stop to copy");
            var stopSpan = HircLayout.Action(stopObj.Body, bank.Version);
            Reproduces(HircWriter.Action(stopObj.Body, stopSpan, bank.Version, stopSpan.Parsed.Id, stopSpan.Parsed.ReferencedId), stopObj.Body, $"stop action {stopSpan.Parsed.Id}");
            stops.Add((stopEvObj, stopObj, stopSpan));
            log.Add($"stops with event {se}, the way it stops Sound {sSpan.Parsed.Id}");
        }

        // ---- the new Sound: in memory, as the bank's own in-memory Sounds are flagged -----------
        var inMemory = bank.Nodes.Values.OfType<HierarchySoundSfxVoice>().FirstOrDefault(s => s.Source.SourceType == EAKBKSourceType.Data && IsAudio(s));
        var flags = inMemory is not null ? (byte)inMemory.Source.BankSourceFlags
                                         : (byte)(sSpan.Parsed.Source.BankSourceFlags & ~EBankSourceFlags.Prefetch);
        var sound = new HircObject { RawType = tSndObj.RawType, Type = tSndObj.Type,
            Body = HircWriter.Sound(tSndObj.Body, sSpan, bank.Version, ids.SoundId, EAKBKSourceType.Data, ids.MediaId, (uint)wem.Length, flags,
                                    dropProps: PlaysOnce) };
        sound.Node = HircBank.ReadExact(sound.Type, sound.Body, bank.Version, "layer sound");
        if (((HierarchySoundSfxVoice)sound.Node).BaseParams.PropBundle.Props?.Any(p => PlaysOnce.Contains(p.Id)) == true)
            throw new InvalidDataException($"{bank.Name}: the copy of Sound {sSpan.Parsed.Id} still loops");
        if (sSpan.Parsed.BaseParams.PropBundle.Props?.Any(p => PlaysOnce.Contains(p.Id)) == true)
            log.Add($"Sound {sSpan.Parsed.Id} loops; the added sound plays once");
        bank.AddMedia(ids.MediaId, wem);
        var kids = mSpan.Parsed.ChildIds.Append(ids.SoundId).OrderBy(x => x).ToArray();
        mixerObj.Body = HircWriter.Mixer(mixerObj.Body, mSpan, bank.Version, kids);
        mixerObj.Node = HircBank.ReadExact(mixerObj.Type, mixerObj.Body, bank.Version, "mixer");
        var next = kids.Where(k => k > ids.SoundId).Cast<uint?>().FirstOrDefault();
        bank.Objects.Insert(next is { } n ? bank.SubtreeStart(n) : bank.Objects.IndexOf(mixerObj), sound);
        bank.Nodes[ids.SoundId] = sound.Node;

        // ---- its Play, appended to the event -----------------------------------------------------
        var play = new HircObject { RawType = playObj.RawType, Type = playObj.Type,
            Body = HircWriter.Action(playObj.Body, aSpan, bank.Version, ids.PlayId, ids.SoundId) };
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
                Body = HircWriter.Action(stopObj.Body, stopSpan, bank.Version, stopId, ids.SoundId) };
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

        log.Add($"added Sound {ids.SoundId} with media {ids.MediaId} ({wem.Length:N0} B), Play {ids.PlayId}" +
                (stops.Count > 0 ? $", {stops.Count} Stop(s) from {ids.StopId}" : ""));
        return new Added(ids, sSpan.Parsed.Id, mixerId, stopEvents, log);
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
