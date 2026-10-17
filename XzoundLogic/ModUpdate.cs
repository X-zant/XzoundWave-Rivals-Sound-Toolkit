using System.Buffers.Binary;
using System.IO;
using System.Text;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.UE4.Wwise.Enums;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

/// <summary>
/// Brings a mod built against an older patch onto the game's current banks.
///
/// A mod that ships a whole .bnk replaces the game's bank at that path, so after an update
/// it puts the OLD bank back. Every event the game added since is hidden behind it, and
/// where the game renamed its events the old bank answers none of the ids the game now asks
/// for and plays nothing at all. Measured on one legacy pak, its bnk_vo_system shared 0 of
/// its 163 event ids with the current bank while 159 of its 164 media were still there.
///
/// So the mod is not patched: the CURRENT bank is, with only the audio the mod changed.
/// Every case below reduces to replacing media by id in the current bank, which is what
/// BnkBuilder already does for every other build, so nothing here writes HIRC and the
/// output answers exactly the events the game asks for today.
///
/// Which medium a line lands on is decided by the Sound object that played it, never by the
/// medium's id. Across one patch an event's id changed with its name (all 163 in one bank
/// were renamed) and a medium's id changed with its export (408 of 450 in one voice bank),
/// while a Sound's id held: 956 of 967 in that voice bank and 311 of 316 in bnk_vo_system.
/// The game also hands old media ids to other lines, so an id alone can put a line on the
/// wrong slot without anything in the output looking broken. That is why every carry records
/// the anchor that placed it, and why the written output is walked again afterwards to check
/// each line landed on the slot it was written for.
/// </summary>
public static class ModUpdate
{
    // ---- what a bank holds -------------------------------------------------------

    private const byte HircSound = 0x02;
    private const byte HircEvent = 0x04;
    private const byte HircMusicTrack = 0x0B;

    /// <summary>
    /// One bank, read as far as the rebase needs it: its events, its embedded media, and
    /// which medium each Sound plays.
    ///
    /// Read by walking the HIRC object headers rather than through the full hierarchy
    /// parser. A Sound names its medium in the first fields of its body (plugin, stream
    /// type, the source id, then the size it declares for that medium), which is all this
    /// needs, and read that way 433 of the
    /// 433 Sounds in the game's bnk_vo_system resolve to a medium that bank embeds. It also
    /// lets the self-test build its banks from those few fields instead of forging every
    /// property a full parse would insist on.
    /// </summary>
    public sealed class Shape
    {
        public uint Version { get; set; }
        public HashSet<uint> Events { get; } = [];
        public Dictionary<uint, byte[]> Media { get; } = [];
        public Dictionary<uint, uint> SoundSource { get; } = [];

        /// <summary>The rest of each Sound's opening fields, by Sound id.</summary>
        public Dictionary<uint, SoundBody> SoundBodies { get; } = [];

        /// <summary>
        /// Music tracks also play media, and this does not read their sources. Counted so a
        /// medium reported as played by nothing can say whether a track might be playing it.
        /// </summary>
        public int MusicTracks { get; set; }

        /// <summary>
        /// Whether the walk read every object the HIRC says it holds. A walk that stopped early
        /// has not seen the objects after the stop, so only a whole one can say a Sound is not
        /// in the bank, as opposed to not read.
        /// </summary>
        public bool EveryObjectRead { get; set; }

        private Dictionary<uint, List<uint>> _players;

        /// <summary>Every Sound that plays this medium, in id order.</summary>
        public IReadOnlyList<uint> Players(uint media)
        {
            if (_players is null)
            {
                _players = [];
                foreach (var (sound, source) in SoundSource.OrderBy(kv => kv.Key))
                {
                    if (!_players.TryGetValue(source, out var list)) _players[source] = list = [];
                    list.Add(sound);
                }
            }
            return _players.TryGetValue(media, out var found) ? found : [];
        }

        /// <summary>Whether the bank knows this medium at all: embeds it, or has a Sound that plays it.</summary>
        public bool Knows(uint media) => Media.ContainsKey(media) || Players(media).Count > 0;
    }

    /// <param name="Plugin">The plugin that makes the Sound's audio.</param>
    /// <param name="StreamType">0 when the audio is all in a bank, 1 when the bank holds its start and the rest streams, 2 when all of it streams.</param>
    /// <param name="Declared">The in-memory media size the Sound declares, or null when its body is too short to hold one.</param>
    public readonly record struct SoundBody(uint Plugin, byte StreamType, uint? Declared)
    {
        /// <summary>
        /// Whether this Sound plays a medium at all. The low four bits of a plugin id are its
        /// type, and only a codec (type 1) decodes a medium. A source plugin (type 2) makes its
        /// audio itself, as Silence (0x00650002) and the game's own 0x01990002 do, so the source
        /// id in its body names no medium and finding none for it is not a misread.
        /// </summary>
        public bool PlaysMedia => (Plugin & 0x0F) == 1;
    }

    public static Shape Read(byte[] bnk) => Read(bnk, withMedia: true);

    /// <summary>The same walk without copying out the embedded media, for a pass over every bank.</summary>
    public static Shape ReadStructure(byte[] bnk) => Read(bnk, withMedia: false);

    /// <param name="withMedia">
    /// False to skip copying out the embedded media, for a walk that only needs to know
    /// which Sounds play what across every bank in the game.
    /// </param>
    private static Shape Read(byte[] bnk, bool withMedia)
    {
        var shape = new Shape();
        var sections = BnkBuilder.ReadSections(bnk);

        var bkhd = sections.FirstOrDefault(s => s.Tag == "BKHD");
        if (bkhd is not null && bkhd.Body.Length >= 4)
            shape.Version = BinaryPrimitives.ReadUInt32LittleEndian(bkhd.Body);

        var didx = sections.FirstOrDefault(s => s.Tag == "DIDX");
        var data = sections.FirstOrDefault(s => s.Tag == "DATA");
        if (withMedia && didx is not null && data is not null)
            foreach (var e in BnkBuilder.ReadDidx(didx.Body))
                if (e.Offset + (long)e.Size <= data.Body.Length)
                    shape.Media[e.Id] = data.Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();

        var hirc = sections.FirstOrDefault(s => s.Tag == "HIRC");
        if (hirc is null || hirc.Body.Length < 4) return shape;

        var body = hirc.Body;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var p = 4;
        var read = 0u;
        for (; read < count && p + 9 <= body.Length; read++)
        {
            var type = body[p];
            var len = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p + 1, 4));
            if (len < 4 || len > (uint)(body.Length - p - 5)) break;   // truncated, stop rather than misread
            var id = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p + 5, 4));
            switch (type)
            {
                case HircEvent:
                    shape.Events.Add(id);
                    break;
                // CAkSound: id(4) plugin(4) stream type(1) source id(4) in-memory size(4), then the rest.
                case HircSound when len >= 13:
                    shape.SoundSource[id] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p + 14, 4));
                    shape.SoundBodies[id] = new SoundBody(
                        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p + 9, 4)),
                        body[p + 13],
                        len >= 17 ? BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p + 18, 4)) : null);
                    break;
                case HircMusicTrack:
                    shape.MusicTracks++;
                    break;
            }
            p += 5 + (int)len;
        }
        shape.EveryObjectRead = read == count;
        return shape;
    }

    // ---- which line a Sound is ---------------------------------------------------------

    /// <summary>
    /// What sits around the Sounds of one bank: the events that reach each one, the container
    /// each one sits in, and the media each music track plays.
    ///
    /// Read through CUE4Parse's full hierarchy parser and <see cref="WwiseProps.SoundsUnder"/>,
    /// which is the walk the index builds its rows with (SoundIndex.Build), so "which line"
    /// means the same thing in the list a person picks from, in the update that moves a line,
    /// and in the audit that checks where it went. The header walk in <see cref="Read(byte[])"/>
    /// stays the reader for media and for each Sound's own opening fields; this one is asked
    /// only what that walk cannot see.
    /// </summary>
    public sealed class Lines
    {
        /// <summary>Whether the parser produced a hierarchy at all.</summary>
        public bool Parsed { get; private set; }

        /// <summary>
        /// Objects of a kind this relies on that the parser could not read and kept only as
        /// raw bytes. Their links are unknown, so a walk with any of them cannot say a match is
        /// the only one, which is the one thing a fallback below has to be able to say.
        /// </summary>
        public int Unreadable { get; private set; }

        /// <summary>A hierarchy was read and nothing of a kind this relies on was left unread.</summary>
        public bool Whole => Parsed && Unreadable == 0;

        public Dictionary<uint, AbstractHierarchy> Nodes { get; } = [];

        /// <summary>The medium each Sound plays, by Sound id.</summary>
        public Dictionary<uint, uint> SoundMedia { get; } = [];

        /// <summary>
        /// The container each Sound, and each container, sits in directly, by its id; 0 for none.
        /// Containers are recorded too so a walk can climb from a Sound to every container above it.
        /// </summary>
        public Dictionary<uint, uint> Parent { get; } = [];

        /// <summary>Each container's children, in the order the bank lists them, which is by id.</summary>
        public Dictionary<uint, uint[]> Children { get; } = [];

        /// <summary>The media each music track plays, by track id.</summary>
        public Dictionary<uint, uint[]> TrackMedia { get; } = [];

        /// <summary>Every Sound an event reaches, by event id.</summary>
        public Dictionary<uint, HashSet<uint>> EventSounds { get; } = [];

        /// <summary>Every event that reaches a Sound, by Sound id.</summary>
        public Dictionary<uint, SortedSet<uint>> SoundEvents { get; } = [];

        /// <summary>
        /// Every Sound an event reaches through a Play action, by event id; an event with no Play
        /// action that reaches a Sound has no entry. A Stop, Pause, Seek or volume action names the
        /// same objects a Play does, so the two maps above count it too, but it is not where a line
        /// is heard: on a loop of bnk_sfx_1053303, sfx_1053303_left_end_play reaches the very Sound
        /// sfx_1053303_left_loop_play plays, through an action that is not a Play. So only this map
        /// is evidence of which line a Sound is, and only it names the event a line is heard under.
        /// </summary>
        public Dictionary<uint, HashSet<uint>> PlaySounds { get; } = [];

        /// <summary>Every event that reaches a Sound through a Play action, by Sound id.</summary>
        public Dictionary<uint, SortedSet<uint>> PlayEvents { get; } = [];

        private Dictionary<uint, List<uint>> _players;

        public bool Has(uint id) => Nodes.ContainsKey(id);

        /// <summary>Every Sound at or below an object, by id, the way an event reaches them.</summary>
        public List<uint> SoundsUnder(uint node) =>
            WwiseProps.SoundsUnder(node, Nodes).Select(x => x.Id).Distinct().OrderBy(x => x).ToList();

        /// <summary>
        /// Every switch container above an object, innermost first, each with the switch states
        /// whose package holds the branch the object sits in. A switch container plays a child
        /// for the states its packages assign it, not for its place in the child list, so two
        /// Sounds are the same slot only where this reads the same for both. Empty when no
        /// switch container is above it.
        /// </summary>
        public IReadOnlyList<string> SwitchChain(uint node)
        {
            var chain = new List<string>();
            var seen = new HashSet<uint>();
            for (var below = node; Parent.TryGetValue(below, out var up) && up != 0 && seen.Add(up); below = up)
            {
                if (Nodes.GetValueOrDefault(up) is not HierarchySwitchContainer sw) continue;
                var states = (sw.SwitchPackages ?? [])
                    .Where(p => (p.NodeIds ?? []).Contains(below)).Select(p => p.SwitchId).Distinct().OrderBy(x => x).ToList();
                chain.Add($"switch container {sw.Id} (group {sw.GroupId}) on " +
                          (states.Count == 0 ? "no state" : $"state{(states.Count == 1 ? "" : "s")} {string.Join(", ", states)}"));
            }
            return chain;
        }

        /// <summary>A switch chain in words, for a reason a person reads.</summary>
        public static string Said(IReadOnlyList<string> chain) =>
            chain.Count == 0 ? "no switch container" : string.Join(", within ", chain);

        /// <summary>Every music track that plays this medium, in id order.</summary>
        public IReadOnlyList<uint> Tracks(uint media) =>
            TrackMedia.Where(kv => kv.Value.Contains(media)).Select(kv => kv.Key).OrderBy(x => x).ToList();

        /// <summary>Every Sound and music track that plays this medium, in id order.</summary>
        public IReadOnlyList<uint> Players(uint media)
        {
            if (_players is null)
            {
                _players = [];
                var all = SoundMedia.Select(kv => (kv.Key, kv.Value))
                    .Concat(TrackMedia.SelectMany(kv => kv.Value.Select(v => (kv.Key, v))));
                foreach (var (who, plays) in all.OrderBy(x => x.Item1))
                {
                    if (!_players.TryGetValue(plays, out var list)) _players[plays] = list = [];
                    if (!list.Contains(who)) list.Add(who);
                }
            }
            return _players.TryGetValue(media, out var found) ? found : [];
        }

        /// <summary>The lines of the bank in these bytes. A bank the parser cannot read gives an empty, unparsed walk.</summary>
        public static Lines Of(byte[] bnk, string name)
        {
            var lines = new Lines();
            WwiseReader reader;
            try { reader = new WwiseReader(new FWwiseArchive(name, bnk), new WwiseArchiveSource()); }
            catch { return lines; }
            if (reader.Hierarchies is null) return lines;
            lines.Parsed = true;

            foreach (var h in reader.Hierarchies)
            {
                if (h.Data is null) continue;
                if (h.Data is HierarchyGeneric)
                {
                    if (h.Type is EAKBKHircType.SoundSfxVoice or EAKBKHircType.Event or EAKBKHircType.EventAction
                        or EAKBKHircType.RandomSequenceContainer or EAKBKHircType.SwitchContainer
                        or EAKBKHircType.LayerContainer or EAKBKHircType.ActorMixer or EAKBKHircType.MusicTrack)
                        lines.Unreadable++;
                    continue;
                }
                lines.Nodes[h.Data.Id] = h.Data;
                switch (h.Data)
                {
                    case HierarchySoundSfxVoice s:
                        lines.SoundMedia[s.Id] = s.Source.SourceId != 0 ? s.Source.SourceId : s.Source.FileId;
                        lines.Parent[s.Id] = s.BaseParams?.DirectParentId ?? 0;
                        break;
                    case HierarchyRandomSequenceContainer r:
                        lines.Children[r.Id] = r.ChildIds ?? [];
                        lines.Parent[r.Id] = r.BaseParams?.DirectParentId ?? 0;
                        break;
                    case HierarchySwitchContainer w:
                        lines.Children[w.Id] = w.ChildIds ?? [];
                        lines.Parent[w.Id] = w.BaseParams?.DirectParentId ?? 0;
                        break;
                    case HierarchyLayerContainer l:
                        lines.Children[l.Id] = l.ChildIds ?? [];
                        lines.Parent[l.Id] = l.BaseParams?.DirectParentId ?? 0;
                        break;
                    case HierarchyActorMixer a:
                        lines.Children[a.Id] = a.ChildIds ?? [];
                        lines.Parent[a.Id] = a.BaseParams?.DirectParentId ?? 0;
                        break;
                    case HierarchyMusicTrack t:
                        // The same reading MusicIndex gives a track's sources.
                        lines.TrackMedia[t.Id] = (t.Sources ?? [])
                            .Select(x => x.SourceId != 0 ? x.SourceId : x.FileId)
                            .Where(x => x != 0).Distinct().ToArray();
                        break;
                }
            }

            // Every action of an event, whatever it does, and every Sound at or below what it
            // names, exactly as SoundIndex.Build walks them, so an event reaches here the Sounds
            // it has rows for in the list. The ones it reaches through a Play action are kept
            // apart as well, because those are the only ones it plays.
            foreach (var ev in lines.Nodes.Values.OfType<HierarchyEvent>())
            {
                var reached = lines.EventSounds[ev.Id] = [];
                foreach (var actionId in ev.EventActionIds ?? [])
                {
                    if (lines.Nodes.GetValueOrDefault(actionId) is not HierarchyEventAction act) continue;
                    var plays = act.EventActionType == EAkActionType.Play;
                    foreach (var snd in WwiseProps.SoundsUnder(act.ReferencedId, lines.Nodes))
                    {
                        reached.Add(snd.Id);
                        if (!lines.SoundEvents.TryGetValue(snd.Id, out var by)) lines.SoundEvents[snd.Id] = by = [];
                        by.Add(ev.Id);
                        if (!plays) continue;
                        if (!lines.PlaySounds.TryGetValue(ev.Id, out var played)) lines.PlaySounds[ev.Id] = played = [];
                        played.Add(snd.Id);
                        if (!lines.PlayEvents.TryGetValue(snd.Id, out var playedBy)) lines.PlayEvents[snd.Id] = playedBy = [];
                        playedBy.Add(ev.Id);
                    }
                }
            }
            return lines;
        }
    }

    // ---- the rebase itself ----------------------------------------------------------

    /// <summary>The game's loose copy of a medium: where it sits, and its bytes on demand.</summary>
    public sealed record Streamed(string Path, Func<byte[]> Read)
    {
        /// <summary>
        /// Every OTHER bank in the game whose Sounds stream this very file, or null when
        /// nothing was asked. Asked on demand, because answering it reads every bank the
        /// game has and most updates never write a loose file at all.
        /// </summary>
        public Func<IReadOnlyList<string>> OtherBanks { get; init; }
    }

    /// <summary>
    /// What the game install ships under one media id, across every container it mounts. The
    /// base pak keeps older copies of banks the patches replaced beside them: bnk_sfx_1053303 is
    /// in pakchunkWwise-Windows.pak with media 477152591 at 27,842 B and in a patch pak at
    /// 24,194 B. So a medium the mod's bank built itself that is byte for byte one of those
    /// copies is not merely read as the game's own older audio, it is shown to be.
    /// </summary>
    /// <param name="Ships">Some container ships a medium of this id, embedded in a bank or as a loose file.</param>
    /// <param name="Container">The container that ships these very bytes under this id, or null when none does.</param>
    /// <param name="At">The file in that container that holds them: a bank, or a loose file.</param>
    public sealed record GameCopy(bool Ships, string Container = null, string At = null)
    {
        /// <summary>A byte for byte copy was found, so the audio is provably the game's own.</summary>
        public bool Same => Container is not null;

        /// <summary>The media id the copy was found under, set by whoever asked, for a report to name.</summary>
        public uint Media { get; init; }
    }

    /// <summary>
    /// The opt-in that carries audio the mod's bank built itself. It exists because a mod
    /// author who rebuilt their bank in the Wwise authoring tool writes audio that reads the
    /// same way as the game's own, and nothing in the bank tells the two apart.
    /// </summary>
    public const string KeepWwiseBuiltFlag = "--keep-wwise-built";

    /// <summary>The refusal for audio the mod's bank built itself.</summary>
    public const string WwiseBuiltKind = "built by Wwise";

    /// <summary>The refusal for a loose file that other banks read too.</summary>
    public const string OtherBanksKind = "other banks stream it";

    /// <summary>The refusal for a line whose every Sound the current game no longer has a slot for.</summary>
    public const string NoHomeKind = "gone";

    // What placed a carry where it landed. Every carry names one, so a person can see why a
    // line sits where it does, and the audit can check that reason against the written bank.

    /// <summary>The Sound is in both banks and still plays the medium of the same id.</summary>
    public const string SameMediaSameSound = "same media and same sound";

    /// <summary>The Sound is in both banks and plays a different medium today, which the line follows.</summary>
    public const string SameSound = "same sound object";

    /// <summary>
    /// The game removed the Sound, one event that played it has exactly one new Sound in its place,
    /// and that Sound declares the size the removed one declared. Inferred, never verified: no
    /// object is the same, so a person should check it by ear.
    /// </summary>
    public const string EventFallback = "event fallback";

    /// <summary>
    /// The game removed the Sound, its parent container swapped exactly that one child for one new
    /// object, and exactly one Sound there declares the size the removed one declared. Inferred,
    /// never verified, for the same reason as the event fallback.
    /// </summary>
    public const string ContainerFallback = "parent container fallback";

    /// <summary>A medium no Sound plays: the music track that played it is in both banks and still plays it.</summary>
    public const string SameTrack = "same media and same music track";

    /// <summary>
    /// One object of the mod's bank that plays a line, and the object in the current bank that
    /// is the same slot today.
    /// </summary>
    /// <param name="Was">The Sound, or music track, in the mod's bank.</param>
    /// <param name="Now">The Sound, or music track, in the current bank that is its slot.</param>
    /// <param name="Anchor">What makes them the same slot: one of the anchor names above.</param>
    /// <param name="Event">For an event fallback, the event whose change made the match unique.</param>
    /// <param name="Detail">How a fallback found the match, in words; null when the anchor says it all.</param>
    public sealed record Placement(uint Was, uint Now, string Anchor, uint Event = 0, string Detail = null)
    {
        public bool IsFallback => Anchor is EventFallback or ContainerFallback;
    }

    /// <summary>A Sound of the mod's bank the current game has no slot for, and why.</summary>
    public sealed record NoHome(uint Sound, string Why);

    /// <param name="Target">The medium in the CURRENT bank whose audio is replaced.</param>
    /// <param name="Source">The mod's medium the audio comes from; the same id for an in-place change.</param>
    /// <param name="How">In place, or repointed, and whether it lands in the bank or beside it.</param>
    /// <param name="Bytes">What is written.</param>
    /// <param name="Was">What the game has there now, which is what the report's sizes and lengths compare against.</param>
    /// <param name="Sounds">The Sounds in the current bank that will play it.</param>
    /// <param name="LoosePath">
    /// The game's path for it when the current bank streams this medium rather than embedding
    /// it, so it ships as a loose file there; null when it goes into the bank.
    /// </param>
    public sealed record Carried(uint Target, uint Source, string How, byte[] Bytes, byte[] Was,
                                 IReadOnlyList<uint> Sounds, string LoosePath)
    {
        /// <summary>
        /// Carried only because the person asked for audio the mod's bank built itself, so
        /// the report can say this one may be the game's own older audio.
        /// </summary>
        public bool BuiltByWwise { get; init; }

        /// <summary>
        /// Of a carry marked <see cref="BuiltByWwise"/>, one whose media id the game ships nowhere,
        /// so the report says nothing shows it is the game's own audio, and names the id.
        /// </summary>
        public bool Foreign { get; init; }

        /// <summary>
        /// Written into a slot the current bank plays only the start of, streaming the rest from a
        /// loose file the game does not ship. What is written is the same either way; this only
        /// lets the report say so, since the game has nothing to stream after it.
        /// </summary>
        public bool PrefetchNoFile { get; init; }

        /// <summary>
        /// Sounds that play the target today but are not in the mod's bank at all, so the game
        /// added them after the mod was made. They play the same medium the mod's Sounds were
        /// repointed onto, so the carry changes them too, and the report names them for that.
        /// Empty when there are none.
        /// </summary>
        public IReadOnlyList<uint> GameAdded { get; init; } = [];

        /// <summary>
        /// Each of the mod's Sounds behind this carry, the Sound it lands on, and the anchor
        /// that makes the two the same slot.
        /// </summary>
        public IReadOnlyList<Placement> Placements { get; init; } = [];

        /// <summary>
        /// Sounds the mod also played this line from that have no slot in the current game.
        /// The line still lands where its other Sounds are; these are named so a person knows
        /// the rest of what the author wrote it for is gone from the game.
        /// </summary>
        public IReadOnlyList<NoHome> Homeless { get; init; } = [];

        /// <summary>The weakest anchor behind the carry, which is the one a person should weigh.</summary>
        public string Anchor =>
            Placements.Any(p => p.Anchor == EventFallback) ? EventFallback
            : Placements.Any(p => p.Anchor == ContainerFallback) ? ContainerFallback
            : Placements.Any(p => p.Anchor == SameSound) ? SameSound
            : Placements.Any(p => p.Anchor == SameTrack) ? SameTrack
            : SameMediaSameSound;
    }

    /// <param name="Kind">A short word for the reason, so a report can group and count them.</param>
    /// <param name="Target">The current bank's medium it would have landed on, or 0 when there is none.</param>
    /// <param name="Source">The mod's medium, or 0 when the refusal concerns the target alone.</param>
    /// <param name="Sounds">The Sounds the decision was about.</param>
    /// <param name="Why">The reason, in words a person can act on.</param>
    public sealed record Refused(string Kind, uint Target, uint Source, IReadOnlyList<uint> Sounds, string Why)
    {
        /// <summary>
        /// Audio the mod's bank built itself whose Sounds the current game has no slot for, so
        /// <see cref="KeepWwiseBuiltFlag"/> would not carry it either and the report must not
        /// say it would.
        /// </summary>
        public bool NoSlot { get; init; }

        /// <summary>
        /// Audio the mod's bank built itself that a container of the game install ships byte for
        /// byte under the same media id, named here, so it is provably the game's own older audio
        /// and <see cref="KeepWwiseBuiltFlag"/> does not carry it either. Null when none does.
        /// </summary>
        public GameCopy ShippedIn { get; init; }

        /// <summary>
        /// Audio the mod's bank built itself under a media id the game ships nowhere, so the report
        /// must not call it the game's own audio.
        /// </summary>
        public bool Foreign { get; init; }

        /// <summary>Whether <see cref="KeepWwiseBuiltFlag"/> would carry this refusal: built by the bank, with a slot, and not provably the game's.</summary>
        public bool Flaggable => Kind == WwiseBuiltKind && !NoSlot && ShippedIn is null;
    }

    /// <summary>
    /// A medium where the mod's copy is audio the current bank already plays there.
    /// </summary>
    /// <param name="Target">The current bank's medium.</param>
    /// <param name="Source">The mod's medium; the same id when the two banks share it.</param>
    public sealed record Unchanged(uint Target, uint Source, IReadOnlyList<uint> Sounds);

    /// <param name="Stock">
    /// Media where the mod's copy is the game's own audio: the same bytes the current bank
    /// already plays for those Sounds, or the start of the file the game streams for them.
    /// The game cuts a streamed sound's opening into the bank and plays the rest from the
    /// loose file, so a prefix of that file is the game's audio cut at another length. And
    /// the game renames media under unchanged Sounds: on one real mod, all 21 of its voice
    /// bank's repointed Sounds held exactly the bytes the current bank plays for them. None
    /// of these is anything the mod did, so they are neither carried nor put to a person.
    /// </param>
    public sealed record Plan(Shape Mod, Shape Current, List<Carried> Carried, List<Refused> Refused,
                              List<Unchanged> Stock, List<uint> EventsGone, List<uint> EventsGained,
                              List<uint> MediaGained)
    {
        /// <summary>What goes into the current bank, by media id.</summary>
        public Dictionary<uint, byte[]> InBank =>
            Carried.Where(c => c.LoosePath is null).ToDictionary(c => c.Target, c => c.Bytes);

        /// <summary>
        /// The loose files the mod ships that this bank's Sounds play, by media id, which the plan
        /// placed as this bank's own media. Empty when the mod ships none for it.
        /// </summary>
        public IReadOnlyDictionary<uint, byte[]> ModLoose { get; init; } = new Dictionary<uint, byte[]>();

        /// <summary>The current bank's hierarchy as the plan read it, so the report names a line by the event that plays it without reading the bank again.</summary>
        public Lines CurrentLines { get; init; }
    }

    /// <summary>
    /// Decide what of a mod's bank carries onto the game's current copy of it.
    ///
    /// Every line is placed by the object that played it, in three steps. First each Sound of
    /// the mod's bank is found in the current bank: by its own id where the game kept it, and
    /// where the game removed it only by a fallback whose match is the only one possible.
    /// Then every medium those Sounds play today is decided once, from all the Sounds that
    /// land on it together, because a swap there changes every Sound that plays it. Last, what
    /// is left of the mod's media is media no Sound placed, which is reported and never carried
    /// on its id alone.
    /// </summary>
    /// <param name="streamedAt">
    /// The game's loose copy of a medium as the current bank would stream it, or null when
    /// the game streams no such file. Asked rather than looked up here so the decision can
    /// be tested without a game mounted.
    /// </param>
    /// <param name="keepWwiseBuilt">
    /// Carry audio the mod's bank built itself as well, which is otherwise refused as the
    /// game's own older audio. See <see cref="KeepWwiseBuiltFlag"/>.
    /// </param>
    /// <param name="loose">
    /// The loose files the mod ships for media this bank's Sounds play, by media id, as
    /// <see cref="LooseFor"/> picks them. Each is placed as this bank's own medium for that id,
    /// so it follows the Sound that played it and never lands by its path, which is only an id.
    /// </param>
    /// <param name="shipped">
    /// What the game install ships under a media id across every container, and which container
    /// ships these very bytes under it, as <see cref="GameCopy"/> says; null when no game is there
    /// to ask, and then only the current bank and its loose files say what the game has. Asked
    /// only of audio the mod's bank built itself, to tell the game's own older audio from a line
    /// of the author's.
    /// </param>
    public static Plan Rebase(byte[] mod, byte[] current, Func<uint, Streamed> streamedAt,
                              bool keepWwiseBuilt = false, IReadOnlyDictionary<uint, byte[]> loose = null,
                              Func<uint, byte[], GameCopy> shipped = null)
    {
        var m = Read(mod);
        var c = Read(current);
        var lm = Lines.Of(mod, "mod");
        var lc = Lines.Of(current, "current");
        streamedAt ??= _ => null;
        loose ??= new Dictionary<uint, byte[]>();

        // The mod's audio for a line. A loose file it ships is the whole line, where its bank
        // may hold only the start the game reads before streaming the rest, so the file is what
        // the line is judged by; without one, it is what the bank embeds, or nothing.
        byte[] Audio(uint line) => loose.TryGetValue(line, out var file) ? file : m.Media.GetValueOrDefault(line);

        var carried = new List<Carried>();
        var refused = new List<Refused>();
        var stock = new List<Unchanged>();
        var landed = new HashSet<uint>();      // current media already given the mod's audio
        var decided = new HashSet<uint>();     // mod media a decision below has already accounted for

        // Whether these bytes are audio the current bank already plays on this medium:
        // exactly its bytes, or the start of the file the game streams for it.
        bool GameAudio(byte[] bytes, uint target) =>
            (c.Media.TryGetValue(target, out var now) && bytes.AsSpan().SequenceEqual(now)) ||
            (streamedAt(target) is { } file && IsStartOf(bytes, file.Read()));

        // The game's audio on a medium today, for a refusal to set the mod's copy against.
        byte[] Today(uint target) => c.Media.TryGetValue(target, out var b) ? b : streamedAt(target)?.Read();

        // Whether Wwise built this medium into the mod's bank itself. Wwise writes each
        // Sound's declared in-memory size as exactly the size of the medium it embeds, and
        // all 4,551 embedded Sounds in the game's current banks agree. A tool that swaps
        // audio into a bank by hand replaces the medium and leaves that field as it was, so
        // a medium the mod author really changed still declares the size the game's audio
        // had. When every Sound that plays the mod's copy declares its exact size, then, the
        // copy is audio Wwise put there: the game's own, as it was when the mod was made. On
        // one real mod, three explosion clips that read this way were the game's own clips
        // from before a patch trimmed them, the same samples up to the new cut. A medium no
        // Sound plays, or a Sound too short to declare a size, is no evidence either way.
        bool BuiltByWwise(uint media, byte[] bytes)
        {
            var players = m.Players(media)
                .Where(s => m.SoundBodies.TryGetValue(s, out var b) && b.PlaysMedia)
                .ToList();
            return players.Count > 0 && players.All(s => m.SoundBodies[s].Declared == (uint)bytes.Length);
        }

        // Whether the game ships a medium of this id anywhere: the current bank plays or embeds
        // it, the game streams it as a loose file, or some container of the install holds it.
        // Audio the mod's bank built itself under an id the game ships nowhere cannot be shown to
        // be the game's own audio, whatever its Sounds declare, so no refusal may call it that.
        // Nor can it be called the author's: in Vergil's sfx bank, media 330263551 is the game's
        // own clip under an id the game has since renumbered to 897421445, and no container ships
        // 330263551 any more. So a refusal for it says what is known and claims neither.
        bool Ships(uint id, byte[] bytes) =>
            c.Knows(id) || streamedAt(id) is not null || shipped?.Invoke(id, bytes).Ships == true;

        // Whether the mod's bank shares not one Sound that plays a medium with the current bank,
        // which is how a bank built in its author's own Wwise project reads: every Sound and medium
        // there gets an id of the project's own, where a bank of the game's, however old, keeps most
        // of its Sound ids from one patch to the next.
        var ownIds = m.SoundSource.Keys.Where(s => m.SoundBodies[s].PlaysMedia).All(s => !c.SoundSource.ContainsKey(s));

        // A copy of these very bytes that the game install ships, under the mod's id for the line
        // or under the id of the medium it would land on, since either is the game's own audio for
        // that slot. Null when no container ships one, or when no game was there to ask.
        GameCopy Proof(uint line, byte[] bytes, uint target)
        {
            if (shipped is null) return null;
            if (shipped(line, bytes) is { Same: true } own) return own with { Media = line };
            return target != 0 && target != line && shipped(target, bytes) is { Same: true } there
                ? there with { Media = target } : null;
        }

        // What makes a medium read as built by the mod's bank, said the same way in every refusal.
        static string Built(uint source, byte[] mine) =>
            $"the mod's bank built this audio itself: every Sound in it that plays media {source} " +
            $"declares exactly the {mine.Length:N0} bytes the mod holds, which is how Wwise writes audio " +
            "it built, and a hand swap leaves that size as it was";

        // The refusal for it, said in full, because the edge case is real: a mod author who
        // rebuilt their bank in the Wwise authoring tool writes their own audio the same way,
        // and only they can say which it is. So it is never dropped quietly, and it names the
        // switch that carries it anyway. It is called the game's own only as far as that is
        // known: proven where a container ships the very bytes, which the switch then does not
        // carry either, and never where the game ships no medium of the id at all, which is said
        // as it is.
        Refused GameOlderAudio(uint target, uint source, byte[] mine, byte[] now, IReadOnlyList<uint> sounds)
        {
            if (Proof(source, mine, target) is { } proof)
                return new(WwiseBuiltKind, target, source, sounds,
                    $"{Built(source, mine)}. And {proof.Container} ships these very bytes as media {proof.Media}, in " +
                    $"{proof.At}, so it is provably the game's own older audio, found in {proof.Container}: the mod's " +
                    $"{SizeOf(mine)} against the game's {SizeOf(now)} on media {target} today. Carrying it would undo a " +
                    $"game update, so it is not carried, with {KeepWwiseBuiltFlag} or without")
                { ShippedIn = proof };
            if (!Ships(source, mine))
                return new(WwiseBuiltKind, target, source, sounds,
                    $"{Built(source, mine)}. But the game ships no media {source} anywhere today, so nothing shows it is " +
                    "the game's own audio: it may be your own, built in the Wwise authoring tool or given that size by a " +
                    "bank editor, or a line of the game's under an id it has since dropped. It would land on media " +
                    $"{target} (the mod's {SizeOf(mine)} against the game's {SizeOf(now)} today). If it is yours, run " +
                    $"again with {KeepWwiseBuiltFlag} to carry it")
                { Foreign = true };
            return new(WwiseBuiltKind, target, source, sounds,
                $"{Built(source, mine)}. So it is the game's own older version of media {target} (the mod's " +
                $"{SizeOf(mine)} against the game's {SizeOf(now)} today), and carrying it would undo a game update" +
                (shipped is null ? "" : "; no container of the game install ships these exact bytes, so this rests on " +
                                        "the size its Sounds declare") +
                ". If you built this bank in the Wwise authoring " +
                $"tool, this may be your own audio: run again with {KeepWwiseBuiltFlag} to carry it");
        }

        // The same audio when every Sound that played it is gone from the game. It is still the
        // game's own older audio where the game ships its id, which is what a person most needs
        // to know about it, but the switch would not help: with no slot to land on it would not
        // be carried either, and the refusal says so rather than send someone to run it again for
        // nothing. Under an id the game ships nowhere it is not shown to be the game's at all. Where
        // the bank also shares no Sound with the current one, it was built in its own Wwise project,
        // with ids of its own, and that is what is said, without a word on whose audio it holds: a
        // bank built that way can hold the game's own audio under its new ids as well as the
        // author's.
        Refused GameOlderAudioNoSlot(uint source, byte[] mine, IReadOnlyList<uint> sounds, string why)
        {
            if (Proof(source, mine, 0) is { } proof)
                return new(WwiseBuiltKind, 0, source, sounds,
                    $"{Built(source, mine)}. And {proof.Container} ships these very bytes as media {source}, in " +
                    $"{proof.At}, so it is provably the game's own older audio, found in {proof.Container}, and not a " +
                    $"line of the mod's. It has no slot in the current game either ({why}), so it is not carried, with " +
                    $"{KeepWwiseBuiltFlag} or without")
                { NoSlot = true, ShippedIn = proof };
            if (!Ships(source, mine))
                return new(WwiseBuiltKind, 0, source, sounds,
                    $"{Built(source, mine)}. " +
                    (ownIds
                        ? $"None of the bank's Sounds is one the current game has and the game ships no media {source} " +
                          "anywhere, so this bank was built in Wwise with ids of its own. "
                        : $"The game ships no media {source} anywhere today, so nothing shows it is the game's own audio: " +
                          "it may be your own, or a line of the game's under an id it has since dropped. ") +
                    $"None of the Sound objects that play it matches one in the current game ({why}), so nothing says " +
                    $"which of the game's lines it replaces and it cannot be placed; {KeepWwiseBuiltFlag} would not carry " +
                    "it either")
                { NoSlot = true, Foreign = true };
            return new(WwiseBuiltKind, 0, source, sounds,
                $"{Built(source, mine)}. So it is the game's own older audio and not a line of the mod's. It has no " +
                $"slot in the current game either ({why}), so {KeepWwiseBuiltFlag} would not carry it")
            { NoSlot = true };
        }

        // A loose file belongs to the game, not to this bank: every bank whose Sounds stream
        // the id reads the one file, and 1,895 loose media in the game are streamed by Sounds
        // in more than one bank. The shared check below only sees this bank's Sounds, so a
        // carry onto such a file would also change banks the mod never touched. Checking those
        // banks' Sounds as this one's are checked would need to know what the mod meant for
        // banks it does not ship, which nothing says, so the carry is refused and the banks
        // are named instead.
        Refused StreamedElsewhere(uint target, uint source, Streamed stream, IReadOnlyList<uint> sounds)
        {
            var others = stream.OtherBanks?.Invoke() ?? [];
            if (others.Count == 0) return null;
            var named = string.Join(", ", others.Take(3).Select(b => b.Split('/')[^1])) +
                        (others.Count > 3 ? $" and {others.Count - 3} more" : "");
            return new Refused(OtherBanksKind, target, source, sounds,
                $"the game streams media {target} from the loose file {stream.Path}, and Sounds in " +
                $"{others.Count} other bank(s) stream that same file ({named}), so writing it would " +
                "change sounds in banks the mod never touched");
        }

        // A Sound the game removed has no id to follow, so no object says where its line went. It
        // is placed only where the bank's structure leaves exactly one candidate AND that candidate
        // carries the line's own fingerprint, and every kind of evidence is asked so that one
        // cannot place a line another contradicts. Even then the placement is inferred and never
        // verified, and the audit lists it for a person to check by ear. Every "no" says why,
        // because a line left without a home is reported with the reason and a person may know
        // better.
        (Placement Found, string Why) Fallback(uint sound, uint line)
        {
            // Only a walk that read every object can say a Sound is the only new one, or that a
            // container lost nothing else. A walk that stopped early has merely not seen the rest.
            if (!lm.Whole || !c.EveryObjectRead || !lc.Whole || !m.EveryObjectRead)
                return (null, "the game removed it, and the hierarchy of " +
                              (!lm.Whole || !m.EveryObjectRead ? "the mod's bank" : "the current bank") +
                              " could not be read in full, so no match for it can be shown to be the only one");

            // CONTENT. Wwise writes each Sound's declared in-memory size from the audio it built the
            // Sound with, and a tool that swaps audio in by hand leaves that field as it was. So in
            // the mod's bank the removed Sound still declares the size of the game's line it played,
            // which is the one thing in the bytes that says which line it was. Structure only says
            // where a line could be, so a fallback places one only on the candidate that declares
            // that same size, and only when no other candidate does, because two different lines
            // can be the same size. Never by the removed Sound's old media id, which the game hands
            // to other lines. A Sound that declares no size has no fingerprint, and nothing can
            // place its line.
            var declared = m.SoundBodies.TryGetValue(sound, out var body) ? body.Declared : null;
            if (declared is not > 0)
                return (null, "the game removed it, and it declares no in-memory size in the mod's bank, so no " +
                              "Sound of the current game can be shown to carry its line");
            var size = $"{declared:N0} B";
            List<uint> Sized(IEnumerable<uint> pool) =>
                pool.Where(x => c.SoundBodies.TryGetValue(x, out var b) && b.PlaysMedia && b.Declared == declared)
                    .Distinct().OrderBy(x => x).ToList();
            string Declares(uint x) =>
                c.SoundBodies.TryGetValue(x, out var b) && b.Declared is { } d ? $"{d:N0} B" : "no size";

            // SWITCH. A switch container plays a child for the switch states its packages assign
            // it, so its child list is not a list of slots: a Sound that arrived under another
            // state, or under another switch, is another slot however unique it is.
            var states = lm.SwitchChain(sound);
            string OtherState(uint x) => lc.SwitchChain(x).SequenceEqual(states) ? null
                : $"Sound {x} plays under {Lines.Said(lc.SwitchChain(x))} where Sound {sound} played under {Lines.Said(states)}";

            // OWN PARENT. Where the container the removed Sound sat in is still in the game, the
            // line's slot is still there: one of that container's own children, or in the one object
            // the container gained. A new Sound an event reaches anywhere else is another slot of
            // that event, as a new pool beside the line's own one is.
            var parent = lm.Parent.GetValueOrDefault(sound);
            uint[] now = null, had = null;
            var stillThere = parent != 0 && lc.Children.TryGetValue(parent, out now) && lm.Children.TryGetValue(parent, out had);
            List<uint> removed = stillThere ? had.Except(now).ToList() : [];
            List<uint> added = stillThere ? now.Except(had).ToList() : [];
            string Elsewhere(uint x) =>
                !stillThere || lc.Parent.GetValueOrDefault(x) == parent ||
                (added.Count == 1 && (added[0] == x || lc.SoundsUnder(added[0]).Contains(x)))
                    ? null
                    : $"its own container {parent} is still in the game, and Sound {x} sits neither in it nor in a single " +
                      "object it gained";

            // EVENT. An event's id is a hash of its name, so one with the same id is the same
            // event. Where it still exists, the Sounds it reaches now that the mod's bank did not
            // have at all are what the game put in place of the ones it removed. The match is
            // unique only when one line left under it and one Sound arrived: with two lines gone
            // the one new Sound could be either's, and with two new Sounds the line could be on
            // either. Every surviving event that reached the Sound must reach the match too, or
            // the events disagree about where the line went. Only what an event reaches through a
            // Play action counts, in both banks: a Stop, a Pause, a Seek or a volume change names
            // the objects it acts on, which says nothing about which line is heard there, and an
            // event that only stops a Sound is no witness that two Sounds are the same line.
            static IEnumerable<uint> Played(Lines l, uint e) => l.PlaySounds.TryGetValue(e, out var s) ? s : [];
            HashSet<uint> New(uint e) => Played(lc, e).Where(x => !lm.Has(x)).ToHashSet();
            HashSet<uint> LinesGone(uint e) =>
                Played(lm, e).Where(x => !lc.Has(x)).Select(x => lm.SoundMedia[x]).ToHashSet();

            var reached = lm.PlayEvents.TryGetValue(sound, out var evs) ? evs.ToList() : [];
            var surviving = reached.Where(lc.EventSounds.ContainsKey).ToList();
            HashSet<uint> candidates = null;
            uint byEvent = 0, witness = 0;
            string eventWhy;
            if (reached.Count == 0) eventWhy = "no event in the mod's bank plays it";
            else if (surviving.Count == 0)
                eventWhy = $"none of the {reached.Count} event(s) that played it is in the current game";
            else
            {
                candidates = surviving.Select(New).Aggregate((a, b) => a.Intersect(b).ToHashSet());
                witness = surviving.FirstOrDefault(e => New(e).Count == 1 && LinesGone(e).SetEquals([line]));
                var first = surviving[0];
                if (candidates.Count == 1 && witness != 0)
                {
                    var only = candidates.First();
                    if (Sized(candidates).Count != 1)
                        eventWhy = $"event {witness} pairs it with Sound {only}, which declares {Declares(only)} where " +
                                   $"Sound {sound} declared {size} in the mod's bank, so nothing in the audio says it is " +
                                   "the same line";
                    else if (OtherState(only) is { } other)
                        eventWhy = $"event {witness} pairs it with Sound {only}, but {other}, so it is another slot";
                    else if (Elsewhere(only) is { } outside)
                        eventWhy = $"event {witness} pairs it with Sound {only}, but {outside}, so it is another slot " +
                                   "of that event";
                    else
                    {
                        byEvent = only;
                        eventWhy = null;
                    }
                }
                else if (candidates.Count == 0)
                    eventWhy = surviving.Count == 1
                        ? $"event {first} still exists but plays no Sound the mod's bank did not have"
                        : $"no Sound the mod's bank did not have is played by all {surviving.Count} events that played it";
                else if (candidates.Count > 1)
                    eventWhy = $"event {first} now plays {candidates.Count} Sounds the mod's bank did not have " +
                               $"({string.Join(", ", candidates.OrderBy(x => x).Take(6))}" +
                               (candidates.Count > 6 ? ", ..." : "") + "), so which one is its slot is not unique";
                else
                    eventWhy = $"event {first} plays one new Sound, {candidates.First()}, but other lines left " +
                               "with this one, so that Sound could be the slot of any of them";
            }

            // PARENT CONTAINER. Children that persisted match themselves, so a container that
            // lost exactly this Sound and gained exactly one object the mod's bank did not have
            // swapped the one for the other. Matched by that difference and never by position:
            // a container lists its children by id, not in any order the author chose, so a
            // position moves whenever an id changes. The new object is the Sound itself or a
            // container whose Sounds are the candidates, and of those only the one that declares
            // the removed Sound's size, when no other does, is the line's continuation.
            uint byParent = 0;
            string parentWhy = null, parentDetail = null;
            if (parent == 0) parentWhy = "it sits in no container";
            else if (!stillThere)
                parentWhy = $"its parent container {parent} is not in the current game";
            else if (removed.Count != 1 || removed[0] != sound || added.Count != 1)
                parentWhy = $"its parent container {parent} lost {removed.Count} and gained {added.Count} children, " +
                            "so they do not pair one to one";
            else if (lm.Has(added[0]))
                parentWhy = $"the one child its parent container {parent} gained, {added[0]}, was already in the " +
                            "mod's bank, so it is not what replaced this Sound";
            else
            {
                var single = lc.SoundMedia.ContainsKey(added[0]);
                List<uint> pool = single ? [added[0]] : lc.SoundsUnder(added[0]);
                var hits = Sized(pool);
                var mixer = lm.Nodes.GetValueOrDefault(parent) is HierarchyActorMixer ||
                            lc.Nodes.GetValueOrDefault(parent) is HierarchyActorMixer;
                if (hits.Count != 1)
                    parentWhy = single
                        ? $"its parent container {parent} swapped it for Sound {added[0]}, which declares " +
                          $"{Declares(added[0])} where Sound {sound} declared {size} in the mod's bank, so nothing in " +
                          "the audio says it is the same line"
                        : $"its parent container {parent} swapped it for container {added[0]}, which holds {pool.Count} " +
                          $"Sounds, {hits.Count} of them declaring the {size} Sound {sound} declared in the mod's bank, " +
                          "so which one is its slot is not unique";
                else if (OtherState(hits[0]) is { } other)
                    parentWhy = $"its parent container {parent} pairs it with Sound {hits[0]}, but {other}, so it is " +
                                "another slot";
                // An actor-mixer is a folder that groups unrelated Sounds for the settings they share,
                // not a container that plays its children as one sound: 850 of the 967 Sounds in one
                // voice bank sit directly in one. A line gone from a folder and a line new in it are
                // not evidence of the same line, as when a patch drops one of a hero's voice lines
                // and adds another, so there an event that played the removed Sound must play the
                // new one as well. One that only stops both is not that event.
                else if (mixer && (candidates is null || !candidates.Contains(hits[0])))
                    parentWhy = $"its parent {parent} is an actor-mixer, a folder that groups unrelated sounds, and " +
                                (candidates is null
                                    ? "no event that played it is in the current game"
                                    : $"the event(s) that played it do not play Sound {hits[0]}") +
                                ", so one child out and one in does not make them the same line";
                else
                {
                    byParent = hits[0];
                    parentDetail =
                        (single
                            ? $"Sound {sound} was the only child container {parent} lost, and Sound {byParent} the only " +
                              "one it gained; it declares"
                            : $"Sound {sound} was the only child container {parent} lost, and the game put container " +
                              $"{added[0]} there instead; Sound {byParent} is the only one of its {pool.Count} Sounds " +
                              "that declares") +
                        $" the {size} Sound {sound} declared in the mod's bank" +
                        (mixer
                            ? $"; event {surviving.First(e => Played(lc, e).Contains(byParent))} reached Sound {sound} " +
                              $"and reaches Sound {byParent}"
                            : "");
                }
            }

            if (byEvent != 0)
            {
                if (byParent != 0 && byParent != byEvent)
                    return (null, $"event {witness} pairs it with Sound {byEvent} but its parent container pairs it " +
                                  $"with Sound {byParent}, so the two disagree");
                return (new Placement(sound, byEvent, EventFallback, witness,
                                      $"Sound {sound} is gone; under that event it was the only line that left and " +
                                      $"Sound {byEvent} the only Sound that arrived, and it declares the {size} Sound " +
                                      $"{sound} declared in the mod's bank"), null);
            }
            if (byParent != 0)
            {
                if (candidates is not null && !candidates.Contains(byParent))
                    return (null, $"its parent container pairs it with Sound {byParent}, but the event(s) that played " +
                                  "it do not all play that Sound, so the two disagree");
                return (new Placement(sound, byParent, ContainerFallback, 0, parentDetail), null);
            }
            return (null, $"{eventWhy}; {parentWhy}");
        }

        // ---- 1. Where each of the mod's Sounds is today.
        //
        // A Sound the current bank still has is its own slot, whatever medium it plays now. A
        // Sound that makes its audio with a source plugin plays no medium, so there is no line
        // of it to carry and it is left out here.
        var home = new Dictionary<uint, Placement>();
        var homeless = new Dictionary<uint, string>();
        foreach (var (sound, line) in m.SoundSource.OrderBy(kv => kv.Key))
        {
            if (!m.SoundBodies[sound].PlaysMedia) continue;
            if (c.SoundSource.TryGetValue(sound, out var now))
            {
                if (c.SoundBodies[sound].PlaysMedia)
                    home[sound] = new Placement(sound, sound, now == line ? SameMediaSameSound : SameSound);
                else
                    homeless[sound] = "the current game makes this Sound's audio with a source plugin, so it plays " +
                                      "no medium to put the line on";
                continue;
            }
            var (found, why) = Fallback(sound, line);
            if (found is not null && c.SoundBodies.TryGetValue(found.Now, out var body) && body.PlaysMedia)
                home[sound] = found;
            else
                homeless[sound] = why ?? $"the Sound it was matched with, {found?.Now}, plays no medium";
        }

        IReadOnlyList<uint> SoundsOf(IEnumerable<Placement> ps) => ps.Select(p => p.Now).Distinct().OrderBy(x => x).ToList();

        // ---- 2. Every medium the current bank plays for those Sounds, decided once.
        //
        // Decided from every placed Sound that lands on it together, because a swap there
        // changes every Sound that plays it and is only safe when all of them agree. A medium
        // is carried IN PLACE only through Sounds that played it in the mod's bank and still
        // play it now, never because the two banks happen to share its id: the game hands old
        // ids to other lines, as media 228693237 and 350422206 of bnk_vo_1053001 now belong to
        // different lines of 1053's interact voice than they did when the mod was made.
        void Decide(uint target, List<Placement> members, Func<Placement, uint> lineOf)
        {
            c.Media.TryGetValue(target, out var was);
            var same = new List<Placement>();     // the mod's audio for it is the game's own today
            var older = new List<Placement>();    // audio the mod's bank built itself: the game's own, older
            var custom = new List<Placement>();   // what the mod's author put there
            foreach (var p in members)
            {
                var line = lineOf(p);
                decided.Add(line);
                if (Audio(line) is not { } bytes)
                {
                    // The mod holds no audio for this Sound, in its bank or as a loose file. On the
                    // medium it already played, that is a Sound the mod left streaming the game's
                    // own file; anywhere else it is a repoint with nothing to carry, which the
                    // refusal below names.
                    (line == target ? same : custom).Add(p);
                    continue;
                }
                if (GameAudio(bytes, target)) same.Add(p);
                // Audio the mod's bank built itself is the game's own older audio unless the person
                // says otherwise, and even then where a container of the game ships these very bytes,
                // since carrying those would undo a game update the author never touched. Only where
                // every Sound landing here plays this one line, though. A line the mod split off this
                // medium onto one of its own, while the medium's other Sounds stay on the game's audio,
                // is a repoint whose editor wrote the new size, and the shared and split refusals below
                // say what it is: read as the game's own audio, one was called the game's older version
                // of media 17004347 under an id, 2125266945, the game never had.
                else if (BuiltByWwise(line, bytes) && members.All(q => lineOf(q) == line) &&
                         (!keepWwiseBuilt || Proof(line, bytes, target) is not null))
                    older.Add(p);
                else custom.Add(p);
            }

            // What the mod left as the game has it. A copy that is the game's audio under the
            // very id it already had is no change at all; one the game renamed, or cut at
            // another length, is listed as not a change. Neither is put to a person.
            foreach (var line in same.Select(lineOf).Distinct().OrderBy(x => x))
            {
                if (Audio(line) is not { } bytes) continue;
                if (line == target && was is not null && bytes.AsSpan().SequenceEqual(was)) continue;
                stock.Add(new Unchanged(target, line, SoundsOf(same.Where(p => lineOf(p) == line))));
            }
            foreach (var line in older.Select(lineOf).Distinct().OrderBy(x => x))
                refused.Add(GameOlderAudio(target, line, Audio(line), Today(target),
                                           SoundsOf(older.Where(p => lineOf(p) == line))));
            if (custom.Count == 0) return;

            var sounds = SoundsOf(custom);
            var sources = custom.Select(lineOf).Distinct().OrderBy(x => x).ToList();
            if (sources.Count > 1)
            {
                refused.Add(new Refused("split", target, 0, sounds,
                    $"the mod plays these Sounds from {sources.Count} different media " +
                    $"({string.Join(", ", sources)}), and the current bank plays all of them from " +
                    "this one, so no single replacement matches what the mod did"));
                return;
            }

            var source = sources[0];
            if (Audio(source) is not { } mine)
            {
                refused.Add(new Refused("not in the bank", target, source, sounds,
                    $"the mod's bank does not embed media {source} and the mod ships no loose file for it, so it " +
                    "holds no audio to carry"));
                return;
            }
            // Pointed at another of the game's own media, as the game has that medium today.
            // Asked of the bytes and not of the id alone: an id the current bank also has is,
            // as often as not, one the game since gave to another line, and then the mod's copy
            // under it is the author's own audio and not a choice between two of the game's.
            if (source != target && c.Knows(source) && GameAudio(mine, source))
            {
                refused.Add(new Refused("existing medium", target, source, sounds,
                    $"the mod plays these Sounds from media {source}, which the current bank also " +
                    "has, so this is a choice between two of the game's own media rather than new audio"));
                return;
            }

            // A swap on the target changes every Sound and track that plays it, so it is refused
            // when one of them is a sound the mod's author could have pointed at this audio and
            // did not: one the mod's bank has, left on something else or on the game's own audio.
            // A Sound the mod's bank does not have at all is not that. The game added it after
            // the mod was made, and nobody can have chosen to leave alone a sound that did not
            // exist yet. It plays the very medium the mod's Sounds now land on, so no carry can
            // change one without the other, and the only two outcomes are carrying, so the
            // author's line plays both where the mod put it and on the new Sound, or refusing,
            // so it plays nowhere and the game's own line is back everywhere. On one real voice
            // mod the game gave six of the author's lines a second Sound under a new event, and
            // counting those as sounds the mod left alone dropped all six. This rests on one fact
            // read from the mod's bank, whether the Sound is in it, and never on pairing Sounds by
            // count. That fact holds only for a bank read to its end, so a walk that stopped early
            // counts every such Sound as one the mod left alone, which is the refusal this check
            // always made. A track is counted the same way, by the hierarchy walk.
            var players = c.Players(target).Union(lc.Tracks(target)).OrderBy(x => x).ToList();
            var landing = custom.Select(p => p.Now).ToHashSet();
            var kept = same.Concat(older).Select(p => p.Now).ToHashSet();
            bool Known(uint s) => m.SoundSource.ContainsKey(s) || lm.Has(s) || (!lm.Whole && lc.TrackMedia.ContainsKey(s));
            var leftAlone = players
                .Where(s => kept.Contains(s) || (!landing.Contains(s) && (!m.EveryObjectRead || Known(s))))
                .ToList();
            if (leftAlone.Count > 0)
            {
                refused.Add(new Refused("shared", target, source, sounds,
                    $"Sound(s) {string.Join(", ", leftAlone)} in the current bank also play media {target} " +
                    $"and the mod did not point them at {source}, so replacing {target} would change " +
                    "sounds the mod never touched"));
                return;
            }
            // Past that refusal, any Sound still beside the landing ones is one the mod's bank does not have.
            var added = players.Where(s => !landing.Contains(s)).ToList();
            if (!landed.Add(target))
            {
                refused.Add(new Refused("conflict", target, source, sounds,
                    $"media {target} already takes another of the mod's lines, which is kept"));
                return;
            }

            // Only reached as custom audio when the switch keeps what the bank built itself, so
            // the carry can be marked for it, and said not to be the game's where the game ships
            // no medium of its id.
            var built = BuiltByWwise(source, mine);
            var foreign = built && !Ships(source, mine);
            var lost = homeless.Where(kv => m.SoundSource[kv.Key] == source)
                               .Select(kv => new NoHome(kv.Key, kv.Value)).OrderBy(h => h.Sound).ToList();
            var how = source == target ? "in place" : "repointed";
            var start = m.Media.GetValueOrDefault(source);   // what the mod's bank embeds, which may be only the line's start
            var file = loose.GetValueOrDefault(source);       // the whole line, when the mod ships it as a loose file
            if (was is not null && file is not null && streamedAt(target) is { } prefetched)
            {
                // The game keeps the start of this medium in the bank and streams the rest from its
                // loose file, and the mod ships its line as a loose file too. Both halves are written,
                // the start into the bank and the file over the game's, because either alone would
                // play the author's start into the game's own rest, or the game's start into the
                // author's.
                if (start is null)
                {
                    landed.Remove(target);
                    refused.Add(new Refused("no start", target, source, sounds,
                        $"the game plays the start of media {target} from the bank and the rest from " +
                        $"{prefetched.Path}, and the mod ships this line only as a loose file, with no start in its " +
                        "bank to put in the bank's place"));
                    return;
                }
                if (StreamedElsewhere(target, source, prefetched, players) is { } others)
                {
                    landed.Remove(target);
                    refused.Add(others);
                    return;
                }
                carried.Add(new Carried(target, source, how, start, was, players, null)
                            { BuiltByWwise = built, Foreign = foreign, GameAdded = added, Placements = custom, Homeless = lost });
                carried.Add(new Carried(target, source, how + ", its streamed file", file, prefetched.Read(), players,
                                        prefetched.Path)
                            { BuiltByWwise = built, Foreign = foreign, GameAdded = added, Placements = custom, Homeless = lost });
            }
            else if (was is not null)
            {
                // A Sound of stream type 1 plays the start of its medium from the bank and the rest
                // from a loose file. Where the game ships no such file for this one, as for two
                // prefetched lines of bnk_display_1047001, the whole of the mod's audio goes into the
                // bank's slot, which is what the game reads there, and the report says so because
                // nothing the game ships streams after it.
                var prefetchOnly = streamedAt(target) is null &&
                                   c.Players(target).Any(s => c.SoundBodies.TryGetValue(s, out var b) && b.PlaysMedia &&
                                                              b.StreamType == 1);
                carried.Add(new Carried(target, source, how, mine, was, players, null)
                            { BuiltByWwise = built, Foreign = foreign, PrefetchNoFile = prefetchOnly, GameAdded = added,
                              Placements = custom, Homeless = lost });
            }
            else if (streamedAt(target) is { } stream)
            {
                // The current bank plays this medium but no longer embeds it: the game moved it
                // out to a loose file. The mod's audio follows it there, because a copy left in
                // the bank would be one the game never reads.
                if (StreamedElsewhere(target, source, stream, players) is { } elsewhere)
                {
                    landed.Remove(target);
                    refused.Add(elsewhere);
                    return;
                }
                carried.Add(new Carried(target, source, how + (source == target ? ", now streamed" : ", streamed"),
                                        mine, stream.Read(), players, stream.Path)
                            { BuiltByWwise = built, Foreign = foreign, GameAdded = added, Placements = custom, Homeless = lost });
            }
            else
            {
                landed.Remove(target);
                refused.Add(new Refused("nowhere", target, source, sounds,
                    $"the current bank plays media {target} but neither embeds it nor has a loose " +
                    "file for it in the game, so there is nothing to put the audio over"));
            }
        }

        foreach (var group in home.Values.GroupBy(p => c.SoundSource[p.Now]).OrderBy(g => g.Key))
            Decide(group.Key, group.OrderBy(p => p.Now).ThenBy(p => p.Was).ToList(), p => m.SoundSource[p.Was]);

        // ---- 3. What is left of the mod's own media: media no Sound above gave a slot, the loose
        // files handed in with the bank included, so one whose Sound has no slot is reported too.
        foreach (var id in m.Media.Keys.Union(loose.Keys).Where(id => !decided.Contains(id)).OrderBy(x => x))
        {
            var mine = Audio(id);
            // The game's own audio under the id it has today is no change, whoever plays it.
            if (c.Media.TryGetValue(id, out var theirs) && mine.AsSpan().SequenceEqual(theirs)) continue;

            var sounds = m.Players(id);
            if (sounds.Count > 0)
            {
                // Every Sound that played it is gone from the game and no fallback gave one a
                // unique home, so nothing says which line of the current game it would be. The
                // current bank may still use the id, for this line or another one.
                var why = string.Join("; ", sounds.Select(s =>
                    $"Sound {s}: {homeless.GetValueOrDefault(s) ?? "it plays no medium"}"));
                if (c.Knows(id) && GameAudio(mine, id)) { stock.Add(new Unchanged(id, id, c.Players(id))); continue; }
                // Said as audio the bank built itself unless the switch asked for it, and then still
                // where the game ships these very bytes, since that is what a person needs to know.
                if (BuiltByWwise(id, mine) && (!keepWwiseBuilt || Proof(id, mine, 0) is not null))
                {
                    refused.Add(GameOlderAudioNoSlot(id, mine, sounds, why));
                    continue;
                }
                refused.Add(new Refused(NoHomeKind, 0, id, sounds,
                    $"only Sound(s) {string.Join(", ", sounds)} play it, and the current game has no slot for " +
                    $"{(sounds.Count == 1 ? "it" : "any of them")}, so it is not placed anywhere. {why}" +
                    (c.Knows(id) ? $". The current bank still has a media {id}, but an id alone is not trusted, " +
                                   "because the game gives old ids to other lines" : "")));
                continue;
            }

            // No Sound plays it. In a music bank a track may, and the track is then its anchor,
            // matched by the medium it plays and never by the position of a clip.
            var tracks = lm.Tracks(id);
            if (tracks.Count > 0)
            {
                var still = tracks.Where(t => lc.TrackMedia.TryGetValue(t, out var now) && now.Contains(id)).ToList();
                if (still.Count > 0)
                {
                    Decide(id, still.Select(t => new Placement(t, t, SameTrack)).ToList(), _ => id);
                    continue;
                }
                decided.Add(id);
                refused.Add(new Refused(NoHomeKind, 0, id, tracks,
                    $"only music track(s) {string.Join(", ", tracks)} play it, and none of them plays it in the " +
                    "current game, so it is not placed anywhere; a track's clips are matched by the medium " +
                    "they play, never by their position"));
                continue;
            }

            if (c.Knows(id) && GameAudio(mine, id)) { stock.Add(new Unchanged(id, id, c.Players(id))); continue; }
            refused.Add(new Refused("orphan", 0, id, [],
                "no Sound in the mod's bank plays it" +
                (lm.Parsed ? ", and no music track does" :
                    m.MusicTracks > 0
                        ? $" (the bank also has {m.MusicTracks} music track(s), and its hierarchy could not be read to see what they play)"
                        : "") +
                (c.Knows(id)
                    ? $". The current bank has a media {id}, but nothing in the mod's bank says it is the same line, " +
                      "and an id alone is not trusted, because the game gives old ids to other lines"
                    : "")));
        }

        return new Plan(m, c, carried, refused, stock,
                        m.Events.Except(c.Events).OrderBy(x => x).ToList(),
                        c.Events.Except(m.Events).OrderBy(x => x).ToList(),
                        c.Media.Keys.Where(id => !m.Media.ContainsKey(id)).OrderBy(x => x).ToList())
               { ModLoose = loose, CurrentLines = lc };
    }

    /// <summary>The media id a loose file's name gives, or null for a name that is not one.</summary>
    public static uint? MediaIdOf(string path) =>
        uint.TryParse(Path.GetFileNameWithoutExtension(path), out var id) ? id : null;

    /// <summary>
    /// The loose files a mod ships that one of its banks plays, by media id: for each medium a
    /// Sound in the bank decodes, the file in the bank's own language folder, or else the neutral
    /// one, which is where the game looks a streamed medium up for that bank.
    ///
    /// A loose file replaces the game's by its path, and its path names only a media id, which
    /// the game hands to other lines. So a file is placed only as the medium of a Sound the mod's
    /// bank has, through that bank's update, where it follows the Sound to whatever medium the
    /// Sound plays today. A file no Sound of the bank plays is left out here, and the caller
    /// reports it rather than write it where its id points.
    /// </summary>
    /// <param name="language">The language folder of the game's bank, or null for a bank that has none.</param>
    public static Dictionary<uint, ModBank.LooseMedia> LooseFor(byte[] bank, string language,
                                                                IEnumerable<ModBank.LooseMedia> files)
    {
        var shape = Read(bank, withMedia: false);
        var played = shape.SoundSource.Where(kv => shape.SoundBodies[kv.Key].PlaysMedia).Select(kv => kv.Value).ToHashSet();
        var found = new Dictionary<uint, ModBank.LooseMedia>();
        foreach (var f in files)
        {
            if (MediaIdOf(f.MountedAt) is not { } id || !played.Contains(id)) continue;
            var lang = GameSession.LanguageOf(f.MountedAt);
            if (lang is not null && !lang.Equals(language, StringComparison.OrdinalIgnoreCase)) continue;
            // The bank's own language folder wins over the neutral one, as it does in game.
            if (lang is null && found.ContainsKey(id)) continue;
            found[id] = f;
        }
        return found;
    }

    private static bool IsStartOf(byte[] part, byte[] whole) =>
        part.Length > 0 && whole is not null && part.Length <= whole.Length &&
        whole.AsSpan(0, part.Length).SequenceEqual(part);

    // A medium's size and, where its own header says, how long it plays, which is what a
    // person weighs when a refusal says one copy is an older version of the other.
    private static string SizeOf(byte[] b) =>
        b is null ? "unknown size"
        : $"{b.Length:N0} B" + (BnkBuilder.Seconds(b) is { } s ? $", {s:0.00} s" : "");

    /// <summary>
    /// What is known of the audio a mod's bank built itself, said after the count of it: how much
    /// is provably the game's own, how much only reads that way, how much sits under media ids the
    /// game ships nowhere and so is not shown to be the game's at all, and what
    /// <see cref="KeepWwiseBuiltFlag"/> would carry. Where nothing is proven and nothing is foreign
    /// it reads as it always has.
    /// </summary>
    private static string BuiltSaid(int all, int noSlot, int proven, int foreign, int flaggable)
    {
        if (proven == 0 && foreign == 0)
            return ", so it is the game's own older audio " +
                   (noSlot == 0 ? $"({KeepWwiseBuiltFlag} carries it anyway)"
                    : noSlot == all ? $"(and none of it has a slot in the current game, so {KeepWwiseBuiltFlag} would not carry it either)"
                    : $"({KeepWwiseBuiltFlag} carries the {all - noSlot} of them that still have a slot in the current game)");
        var parts = new List<string>();
        if (proven > 0)
            parts.Add($"{proven} provably the game's own older audio, found byte for byte in the game's own containers");
        if (all - proven - foreign > 0)
            parts.Add($"{all - proven - foreign} the game's own older audio by the size its Sounds declare");
        if (foreign > 0)
            parts.Add($"{foreign} under media ids the game ships nowhere today, so not shown to be the game's own audio");
        parts.Add(flaggable == all ? $"{KeepWwiseBuiltFlag} carries it anyway"
                  : flaggable == 0 ? $"{KeepWwiseBuiltFlag} would carry none of it"
                  : $"{KeepWwiseBuiltFlag} carries the {flaggable} of them that are not provably the game's and still " +
                    "have a slot in the current game");
        return $" ({string.Join("; ", parts)})";
    }

    /// <summary>
    /// The game's bank a mod's bank replaces: the one at the very same path, or null.
    ///
    /// Exact, language folder included, and never the nearest name. A voice bank ships once
    /// per language under one file name, and comparing an English bank against the Chinese
    /// one shares no media at all, which reads as a mod that changed every line.
    /// </summary>
    public static string Counterpart(string mountedAt, IEnumerable<string> gameBanks) =>
        mountedAt is null ? null
        : gameBanks.FirstOrDefault(g => g.Equals(mountedAt, StringComparison.OrdinalIgnoreCase));

    // ---- writing ----------------------------------------------------------------------

    public sealed record Written(List<string> Outputs, string BankPath, long BankBytes, int Replaced);

    /// <summary>
    /// Write a plan's bank and loose files under outRoot, at the game's own paths, which is
    /// the same layout every other build writes and what repak expects.
    /// </summary>
    /// <param name="taken">
    /// Loose paths something has already written. Every loose file is a carry of some bank's,
    /// so where a later carry lands on a path an earlier one wrote, the earlier file is the one
    /// kept, and the later carry is moved to the refusals with that reason.
    /// </param>
    public static Written Write(Plan plan, byte[] current, string gamePath, string outRoot, ISet<string> taken)
    {
        var outputs = new List<string>();
        string bankPath = null;
        long bankBytes = 0;
        var replaced = 0;

        var inBank = plan.InBank;
        if (inBank.Count > 0)
        {
            // Through the one bank writer every build uses: the current bank, with only
            // these media swapped and everything else, HIRC included, copied through.
            var built = BnkBuilder.Build(current, inBank);
            replaced = built.Replaced;
            var dest = Path.Combine(outRoot, gamePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, built.Bytes);
            bankPath = Path.GetRelativePath(outRoot, dest);
            bankBytes = built.Bytes.Length;
            outputs.Add(bankPath);
        }

        foreach (var c in plan.Carried.Where(c => c.LoosePath is not null).ToList())
        {
            if (!taken.Add(c.LoosePath))
            {
                plan.Carried.Remove(c);
                plan.Refused.Add(new Refused("kept the mod's file", c.Target, c.Source, c.Sounds,
                    $"another of the mod's lines is already written at {c.LoosePath}, and that one is kept"));
                continue;
            }
            var dest = Path.Combine(outRoot, c.LoosePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, c.Bytes);
            outputs.Add(Path.GetRelativePath(outRoot, dest));
        }
        return new Written(outputs, bankPath, bankBytes, replaced);
    }

    // ---- the placement audit -------------------------------------------------------------

    /// <summary>What the placement audit found for one bank.</summary>
    /// <param name="Lines">The custom lines the update placed: each distinct medium of the mod's that it carried.</param>
    /// <param name="Verified">
    /// Those found on the very Sound, or music track, they were written for, on every medium they
    /// landed on. The slot is then the same object in both banks, so nothing about it is inferred.
    /// </param>
    /// <param name="Inferred">
    /// Those found where they belong, but on at least one Sound the game added in place of the one
    /// they were written for, which only a fallback pairs them with. However strong its evidence,
    /// a fallback is an inference, and it is never counted as verified.
    /// </param>
    /// <param name="Problems">Every disagreement, in words, one per landing or per medium nothing claims.</param>
    /// <param name="Inferences">Each inferred placement with the evidence the audit found for it, for a person to check by ear.</param>
    public sealed record Audit(int Lines, int Verified, int Inferred, IReadOnlyList<string> Problems,
                               IReadOnlyList<string> Inferences)
    {
        /// <summary>The lines found where they belong, verified or inferred.</summary>
        public int Placed => Verified + Inferred;

        public bool Clean => Problems.Count == 0 && Placed == Lines;

        public string Line => Said(Placed, Lines, Verified, Inferred);

        /// <summary>The audit line, said the same way for one bank and for a whole mod.</summary>
        public static string Said(int placed, int lines, int verified, int inferred) =>
            $"placement audit: {placed} of {lines} placed; {verified} verified by the same sound object, " +
            $"{inferred} inferred by a fallback" + (inferred > 0 ? " (listed below)" : "");
    }

    /// <summary>
    /// Walk what was written, check that every custom line landed on the slot it was written
    /// for, and say how each one is known to be there.
    ///
    /// This is what makes a result trustworthy rather than just likely, so it trusts nothing
    /// the plan decided beyond its claim of where each line went. It reads the written bank
    /// back from its bytes, and the mod's bank again, through the same hierarchy walk the index
    /// uses (<see cref="Lines"/>), and for each carry asks the written bank three things: does
    /// the claimed medium hold exactly the mod's audio; is every Sound that plays it either a
    /// Sound the line was written for, one a fallback pairs with such a Sound uniquely, or one
    /// the mod's bank never had and no fallback gives to another of its lines; and is at least
    /// one of them of the first two kinds. A line found on the very Sound it was written for is
    /// verified. One that needed a fallback is inferred, never verified, and is listed with its
    /// evidence. The fallback is worked out again here from the two walks and the two banks'
    /// declared sizes, under the rules the update places by, and is not taken from the plan.
    /// Last, every medium of the written bank that differs from the game's own must be one a
    /// carry claims, so nothing lands anywhere unaccounted for.
    /// </summary>
    /// <param name="mod">The mod's bank, as shipped.</param>
    /// <param name="current">The game's current bank the output was built on.</param>
    /// <param name="output">The bank as written, read back from where it was written; the current bank when none was.</param>
    /// <param name="looseWritten">The bytes written at a carry's loose path, or null when nothing is there.</param>
    /// <param name="eventName">Names an event for the report; null to give bare ids.</param>
    /// <param name="looseMod">The loose files the mod ships that this bank's Sounds play, by media id, as the plan was given them.</param>
    public static Audit AuditPlacement(Plan plan, byte[] mod, byte[] current, byte[] output,
                                       Func<string, byte[]> looseWritten, Func<uint, string> eventName = null,
                                       IReadOnlyDictionary<uint, byte[]> looseMod = null)
    {
        string Ev(uint id) => eventName?.Invoke(id) ?? id.ToString();
        looseMod ??= new Dictionary<uint, byte[]>();
        var problems = new List<string>();
        var lines = plan.Carried.Select(x => x.Source).Distinct().Count();

        var lm = Lines.Of(mod, "mod");
        var lo = Lines.Of(output, "output");
        if (!lm.Whole || !lo.Whole)
            return new Audit(lines, 0, 0, [
                $"the hierarchy of {(!lm.Whole ? "the mod's bank" : "the written bank")} could not be read in full, " +
                "so no line's slot can be checked"], []);
        var modShape = Read(mod);
        var outShape = Read(output);
        var modMedia = modShape.Media;
        var outMedia = outShape.Media;
        var gameMedia = Read(current).Media;

        // Nothing may change that no carry claims.
        var claimed = plan.Carried.Where(x => x.LoosePath is null).Select(x => x.Target).ToHashSet();
        foreach (var id in outMedia.Keys.Union(gameMedia.Keys).OrderBy(x => x))
        {
            var mine = outMedia.GetValueOrDefault(id);
            var theirs = gameMedia.GetValueOrDefault(id);
            if (mine is null || theirs is null)
                problems.Add($"media {id} is {(mine is null ? "missing from" : "new in")} the written bank, and an update adds and removes no media");
            else if (!claimed.Contains(id) && !mine.AsSpan().SequenceEqual(theirs))
                problems.Add($"media {id} differs from the game's own in the written bank, and no carry claims it");
        }

        // The events that reach a slot, named, so a person can see what the line was and what it became.
        string EventsOf(Lines l, IEnumerable<uint> players)
        {
            var evs = players.SelectMany(p => l.SoundEvents.TryGetValue(p, out var e) ? e : []).Distinct()
                             .Select(Ev).OrderBy(x => x, StringComparer.Ordinal).ToList();
            return evs.Count == 0 ? "reached by no event"
                : $"reached by event{(evs.Count == 1 ? "" : "s")} " + string.Join(", ", evs.Take(4)) +
                  (evs.Count > 4 ? $" and {evs.Count - 4} more" : "");
        }

        // A fallback's match, worked out again from the two walks and the two banks' own bytes
        // under the rules the update places by, and asked of the written bank rather than the
        // plan. The landing Sound must declare the size the removed Sound declared in the mod's
        // bank and be the only candidate that does, play under the same switch states, and be
        // the one match of an event fallback or a parent container fallback, with the two not
        // disagreeing. An event fallback needs every surviving event that reached the removed
        // Sound to reach the landing Sound as their one new Sound in common, not just one event,
        // and the landing Sound inside the removed Sound's own container where that is still in
        // the game. A container fallback in an actor-mixer needs such an event as well. Gives the
        // evidence in words when they pair the removed Sound with the landing one and nothing
        // else, and null when they do not.
        string Unique(uint was, uint now, uint line)
        {
            if (lo.Has(was) || lm.Has(now)) return null;
            var declared = modShape.SoundBodies.TryGetValue(was, out var own) ? own.Declared : null;
            if (declared is not > 0) return null;
            bool Sized(uint x) => outShape.SoundBodies.TryGetValue(x, out var b) && b.PlaysMedia && b.Declared == declared;
            var states = lm.SwitchChain(was);
            bool SameStates(uint x) => lo.SwitchChain(x).SequenceEqual(states);

            var parent = lm.Parent.GetValueOrDefault(was);
            uint[] had = null, has = null;
            var stillThere = parent != 0 && lm.Children.TryGetValue(parent, out had) && lo.Children.TryGetValue(parent, out has);
            List<uint> removed = stillThere ? had.Except(has).ToList() : [];
            List<uint> added = stillThere ? has.Except(had).ToList() : [];
            bool Inside(uint x) => !stillThere || lo.Parent.GetValueOrDefault(x) == parent ||
                                   (added.Count == 1 && (added[0] == x || lo.SoundsUnder(added[0]).Contains(x)));

            // Events count only for the Sounds they play, as in the update: one that only stops or
            // seeks a Sound is no evidence of which line it is.
            static IEnumerable<uint> Played(Lines l, uint e) => l.PlaySounds.TryGetValue(e, out var s) ? s : [];
            var survived = (lm.PlayEvents.TryGetValue(was, out var evs) ? evs : [])
                .Where(lo.EventSounds.ContainsKey).ToList();
            HashSet<uint> Fresh(uint e) => Played(lo, e).Where(x => !lm.Has(x)).ToHashSet();
            var common = survived.Count == 0 ? null : survived.Select(Fresh).Aggregate((a, b) => a.Intersect(b).ToHashSet());

            uint byEvent = 0, witness = 0;
            if (common is { Count: 1 })
            {
                witness = survived.FirstOrDefault(e => Fresh(e).Count == 1 &&
                    Played(lm, e).Where(x => !lo.Has(x)).Select(x => lm.SoundMedia[x]).ToHashSet().SetEquals([line]));
                var only = common.First();
                if (witness != 0 && Sized(only) && SameStates(only) && Inside(only)) byEvent = only;
            }

            uint byParent = 0;
            List<uint> pool = [];
            if (stillThere && removed.Count == 1 && removed[0] == was && added.Count == 1 && !lm.Has(added[0]))
            {
                pool = lo.SoundMedia.ContainsKey(added[0]) ? [added[0]] : lo.SoundsUnder(added[0]);
                var hits = pool.Where(Sized).ToList();
                var mixer = lm.Nodes.GetValueOrDefault(parent) is HierarchyActorMixer ||
                            lo.Nodes.GetValueOrDefault(parent) is HierarchyActorMixer;
                if (hits.Count == 1 && SameStates(hits[0]) && (!mixer || common?.Contains(hits[0]) == true))
                    byParent = hits[0];
            }

            if (byEvent != 0 && byParent != 0 && byEvent != byParent) return null;
            if (byEvent == 0 && byParent != 0 && common is not null && !common.Contains(byParent)) return null;
            if ((byEvent != 0 ? byEvent : byParent) != now) return null;

            var size = $"{declared:N0} B";
            var heard = survived.Where(e => Played(lo, e).Contains(now)).Select(Ev).ToList();
            var switched = states.Count > 0 ? $"; both play under {Lines.Said(states)}" : "";
            if (byEvent != 0)
                return $"by the event fallback: event {Ev(witness)} lost only that line and gained only Sound {now}" +
                       (survived.Count > 1
                           ? $", and all {survived.Count} surviving events that reached Sound {was} have Sound {now} as " +
                             "their one new Sound in common"
                           : "") +
                       $"; Sound {now} declares the {size} Sound {was} declared in the mod's bank{switched}";
            return $"by the parent container fallback: container {parent} lost only Sound {was} and gained only " +
                   (pool.Count == 1 && added[0] == now
                       ? $"Sound {now}, which declares"
                       : $"container {added[0]}, and Sound {now} is the only one of its {pool.Count} Sounds that declares") +
                   $" the {size} Sound {was} declared in the mod's bank{switched}" +
                   (heard.Count > 0
                       ? $"; corroborated by event{(heard.Count == 1 ? "" : "s")} {string.Join(", ", heard.Take(3))}, " +
                         $"which reached Sound {was} and reach{(heard.Count == 1 ? "es" : "")} Sound {now}"
                       : "");
        }

        // Every Sound of the mod's bank the game removed. A landing Sound that a fallback gives to
        // one of them is that Sound's slot, so another line on it replaces that Sound's line.
        var removedSounds = lm.SoundMedia.Keys
            .Where(w => !lo.Has(w) && modShape.SoundBodies.TryGetValue(w, out var b) && b.PlaysMedia)
            .OrderBy(w => w).ToList();

        int verified = 0, inferred = 0;
        var inferences = new List<string>();
        foreach (var line in plan.Carried.GroupBy(x => x.Source).OrderBy(g => g.Key))
        {
            var ok = true;
            var fell = new List<string>();                          // the fallbacks that put it where it is
            var wrote = lm.Players(line.Key);                       // the slot it was written for
            foreach (var x in line.OrderBy(x => x.Target).ThenBy(x => x.LoosePath is not null))
            {
                // The mod's audio for the line is what its bank embeds, or the loose file it ships for
                // it, which a prefetched medium needs both of in their two places.
                var at = x.LoosePath is null ? outMedia.GetValueOrDefault(x.Target) : looseWritten?.Invoke(x.LoosePath);
                var meant = new[] { modMedia.GetValueOrDefault(x.Source), looseMod.GetValueOrDefault(x.Source) };
                if (at is null || !meant.Any(b => b is not null && at.AsSpan().SequenceEqual(b)))
                {
                    problems.Add($"mod media {x.Source} was to land on media {x.Target}, and the written " +
                                 $"{(x.LoosePath is null ? "bank" : "file")} does not hold it there");
                    ok = false;
                    continue;
                }

                var lands = lo.Players(x.Target);                   // the slot it lands on
                var linked = 0;
                var wrong = new List<string>();
                foreach (var o in lands)
                {
                    if (wrote.Contains(o)) { linked++; continue; }
                    if (lm.Has(o))
                    {
                        wrong.Add(lm.SoundMedia.TryGetValue(o, out var had)
                            ? $"Sound {o}, which the mod's bank plays from media {had}"
                            : $"object {o}, which the mod's bank has playing other media");
                        continue;
                    }
                    // A Sound the mod's bank did not have. Where a fallback gives it to another of the
                    // mod's removed Sounds, one that played a different line, it is that line's slot,
                    // whatever the plan claims, and this line on it is on the wrong slot.
                    var other = removedSounds.FirstOrDefault(w => lm.SoundMedia[w] != line.Key &&
                                                                  Unique(w, o, lm.SoundMedia[w]) is not null);
                    if (other != 0)
                    {
                        wrong.Add($"Sound {o}, which a fallback pairs with Sound {other}, a Sound the mod's bank plays " +
                                  $"from media {lm.SoundMedia[other]}");
                        continue;
                    }
                    // Where the plan says a fallback put the line on it, that fallback must be the only
                    // match; otherwise the game added it beside the slot and it only shares the medium.
                    var claim = x.Placements.FirstOrDefault(p => p.Now == o && p.Was != o);
                    if (claim is null) continue;
                    if (wrote.Contains(claim.Was) && Unique(claim.Was, o, line.Key) is { } evidence)
                    {
                        linked++;
                        fell.Add($"mod media {x.Source} -> media {x.Target}: on Sound {o} in place of Sound {claim.Was}, " +
                                 $"which the game removed, {evidence}");
                    }
                    else wrong.Add($"Sound {o}, which no unique fallback pairs with Sound {claim.Was}");
                }
                if (wrong.Count > 0 || linked == 0)
                {
                    problems.Add($"mod media {x.Source} -> media {x.Target}: written for " +
                                 $"{(wrote.Count == 0 ? "no Sound" : "Sound(s) " + string.Join(", ", wrote.Take(6)))} " +
                                 $"({EventsOf(lm, wrote)}), and it lands on " +
                                 $"{(lands.Count == 0 ? "no Sound" : "Sound(s) " + string.Join(", ", lands.Take(6)))} " +
                                 $"({EventsOf(lo, lands)})" +
                                 (wrong.Count > 0 ? $"; not the slot it was written for: {string.Join("; ", wrong)}" : "") +
                                 (linked == 0 ? "; none of them is a Sound it was written for or one a unique fallback pairs with one" : ""));
                    ok = false;
                }
            }
            if (!ok) continue;
            // Verified only where every Sound it lands on is the very one it was written for. One
            // fallback anywhere makes the whole line an inference, which is listed with its evidence.
            if (fell.Count == 0) verified++;
            else
            {
                inferred++;
                inferences.AddRange(fell.Distinct());
            }
        }
        return new Audit(lines, verified, inferred, problems, inferences);
    }

    // ---- the report --------------------------------------------------------------------

    /// <summary>What the report says about a medium beyond its id: the event and line that play it.</summary>
    public delegate string Namer(uint mediaId);

    /// <summary>
    /// One bank's section of the report. Kept free of the session so the self-test can read
    /// the same words a person will.
    /// </summary>
    /// <param name="audit">What the placement audit found for this bank, or null when it was not run.</param>
    public static string Describe(string name, string modPath, int modBytes, string gamePath, int gameBytes,
                                  Plan plan, Written written, Namer media, Func<uint, string> eventName,
                                  Func<uint, bool> streams, Audit audit = null)
    {
        var r = new StringBuilder();
        var m = plan.Mod;
        var c = plan.Current;
        string Ev(uint id) => eventName?.Invoke(id) ?? id.ToString();
        string Named(uint id) => media?.Invoke(id) is { Length: > 0 } n ? "   " + n : "";

        r.AppendLine(name);
        r.AppendLine($"  mod's copy      {modPath}   {modBytes:N0} B, bank version {m.Version}");
        r.AppendLine($"  game's current  {gamePath}   {gameBytes:N0} B, bank version {c.Version}");
        if (m.Version != c.Version)
            r.AppendLine("  NOTE            the two banks are different Wwise versions, so the Sound reading below may not hold for the mod's copy");

        // Before: what the old bank did in game. The game asks the bank for the events it
        // defines today, and the old bank can only answer the ones whose ids survived.
        var answered = c.Events.Count(m.Events.Contains);
        r.AppendLine($"  as shipped      the mod's bank answers {answered} of the {c.Events.Count} events the game " +
                     $"defines in this bank today" +
                     (answered == 0 && c.Events.Count > 0 ? ", so in game it plays nothing at all" : ""));

        // The reading this relies on, checked against the bank it was read from: a Sound that
        // decodes a medium the bank neither embeds nor finds as a loose file in the game would
        // mean the offsets are wrong. Only a codec Sound decodes a medium. A source plugin
        // makes its audio itself, so it is counted apart, where counting it as a miss raised
        // this alarm on every sfx bank for its Silence Sounds. The loose count is of Sounds
        // whose medium is NOT in the bank: most of a voice bank's Sounds stream too, but the
        // bank holds the start of each line, so they count as embedded and are named as such
        // rather than read as nothing streaming.
        var decoding = c.SoundSource.Where(kv => c.SoundBodies.TryGetValue(kv.Key, out var b) && b.PlaysMedia).ToList();
        var embedded = decoding.Count(kv => c.Media.ContainsKey(kv.Value));
        var prefetch = decoding.Count(kv => c.Media.ContainsKey(kv.Value) && c.SoundBodies[kv.Key].StreamType == 1);
        var loose = decoding.Count(kv => !c.Media.ContainsKey(kv.Value) && (streams?.Invoke(kv.Value) ?? false));
        r.AppendLine($"  read check      {decoding.Count:N0} of {c.SoundSource.Count:N0} Sounds in the current bank decode a medium, " +
                     $"and the other {c.SoundSource.Count - decoding.Count:N0} use a source plugin (Silence and the like), which plays none");
        r.AppendLine($"                  of those {decoding.Count:N0}: {embedded:N0} play a medium the bank embeds ({prefetch:N0} of them " +
                     $"only its start, streaming the rest), {loose:N0} one the bank does not embed but the game has as a " +
                     $"loose file, {decoding.Count - embedded - loose:N0} neither");
        r.AppendLine();
        var inBank = plan.Carried.Count(x => x.LoosePath is null);
        r.AppendLine($"  CARRIED {plan.Carried.Count} ({inBank} into the bank, {plan.Carried.Count - inBank} as loose files)");
        foreach (var x in plan.Carried.OrderBy(x => x.How).ThenBy(x => x.Target))
        {
            var from = x.Source == x.Target ? "" : $" from mod media {x.Source}";
            r.AppendLine($"    media {x.Target,-10}  {x.How}{from}");
            r.AppendLine($"        game {Size(x.Was)} -> mod {Size(x.Bytes)};  " +
                         $"played by {Sounds(x.Sounds)}{Named(x.Target)}");
            if (x.LoosePath is not null) r.AppendLine($"        written to {x.LoosePath}");
            if (x.BuiltByWwise)
                r.AppendLine($"        carried only because {KeepWwiseBuiltFlag} was given: the mod's bank built this " +
                             (x.Foreign
                                 ? $"audio itself, under media {x.Source}, which the game ships nowhere today, so nothing shows it " +
                                   "is the game's own audio"
                                 : "audio itself, so it may be the game's own older version and undo a game update"));
            // A prefetched slot the game ships no streamed file for takes the whole line in the bank,
            // which is what the game reads there; said so, because nothing streams after it.
            if (x.PrefetchNoFile)
                r.AppendLine($"        NOTE: the current bank plays only the start of media {x.Target} from the bank and " +
                             "streams the rest from a loose file, but the game ships no streamed file for that slot, so " +
                             $"the whole of the mod's line ({Size(x.Bytes)}) was written into the bank's slot");
            if (x.GameAdded.Count > 0)
                r.AppendLine("        carried onto " +
                             (x.GameAdded.Count == 1
                                 ? "a sound the game added since the mod was made, which plays the same line: "
                                 : "sounds the game added since the mod was made, which play the same line: ") +
                             Sounds(x.GameAdded));
            // Why it sits here, so a line placed by a fallback reads differently from one whose
            // Sound simply kept its id, and a person knows which to listen to first.
            foreach (var anchor in Anchors(x)) r.AppendLine($"        anchor: {anchor}");
            foreach (var h in x.Homeless)
                r.AppendLine($"        also written for Sound {h.Sound}, which has no slot in the current game: {h.Why}");
        }

        r.AppendLine();
        var built = plan.Refused.Where(x => x.Kind == WwiseBuiltKind).ToList();
        r.AppendLine($"  NOT CARRIED {plan.Refused.Count}" +
                     (built.Count > 0
                         ? $", {built.Count} of them because the mod's bank built that audio itself" +
                           BuiltSaid(built.Count, built.Count(x => x.NoSlot), built.Count(x => x.ShippedIn is not null),
                                     built.Count(x => x.Foreign), built.Count(x => x.Flaggable))
                         : ""));
        foreach (var x in plan.Refused.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Target).ThenBy(x => x.Source))
        {
            var what = x.Target != 0 && x.Source != 0 && x.Target != x.Source
                ? $"mod media {x.Source} -> media {x.Target}"
                : x.Target != 0 ? $"media {x.Target}" : $"mod media {x.Source}";
            r.AppendLine($"    {what}  ({x.Kind})");
            r.AppendLine($"        {x.Why}");
            // The sizes are what lets a person decide: a full clip where the game keeps a
            // few hundred bytes of prefetch is new audio, the same size is likely not.
            var sizes = new List<string>();
            if (x.Source != 0 && (plan.ModLoose.GetValueOrDefault(x.Source) ?? m.Media.GetValueOrDefault(x.Source)) is { } ours)
                sizes.Add($"mod {Size(ours)}");
            if (x.Target != 0 && c.Media.TryGetValue(x.Target, out var now)) sizes.Add($"game {Size(now)}");
            if (sizes.Count > 0) r.AppendLine($"        {string.Join(", ", sizes)}");
            if (x.Target != 0 && Named(x.Target) is { Length: > 0 } n) r.AppendLine($"       {n}");
        }

        if (plan.Stock.Count > 0)
        {
            r.AppendLine();
            r.AppendLine($"  NOT A CHANGE {plan.Stock.Count}: the mod's copy is audio the current bank already plays there,");
            r.AppendLine("  byte for byte or as the start of the file the game streams, so there is nothing to carry");
            foreach (var line in plan.Stock.OrderBy(x => x.Target)
                                     .Select(x => x.Source == x.Target ? $"{x.Target}" : $"{x.Target} (mod media {x.Source})")
                                     .Chunk(4))
                r.AppendLine("    " + string.Join(", ", line));
        }

        r.AppendLine();
        r.AppendLine($"  EVENTS only in the mod's bank: {plan.EventsGone.Count}. The game renamed or removed them; there " +
                     "is nothing to carry, because the output answers the game's own events.");
        if (plan.EventsGone.Count > 0)
            r.AppendLine("    e.g. " + string.Join(", ", plan.EventsGone.Take(6).Select(Ev)));

        r.AppendLine($"  GAINED by updating: {plan.EventsGained.Count} event(s) and {plan.MediaGained.Count} media " +
                     "the old bank was hiding");
        if (plan.EventsGained.Count > 0)
            r.AppendLine("    e.g. " + string.Join(", ", plan.EventsGained.Take(6).Select(Ev)));

        r.AppendLine();
        if (written.BankPath is not null)
            r.AppendLine($"  wrote  {written.BankPath.Replace(Path.DirectorySeparatorChar, '/')}   {written.BankBytes:N0} B, " +
                         $"{written.Replaced} medi{(written.Replaced == 1 ? "um" : "a")} replaced");
        else
            r.AppendLine("  wrote  no bank: none of the mod's audio lands in it, so the game's own copy applies " +
                         "and shipping it would only hide the next update");
        foreach (var o in written.Outputs.Where(o => o != written.BankPath))
            r.AppendLine($"  wrote  {o.Replace(Path.DirectorySeparatorChar, '/')}");

        if (audit is not null)
        {
            r.AppendLine();
            r.AppendLine($"  {audit.Line}" +
                         (audit.Clean ? "" : $"; {audit.Problems.Count} disagreement(s), so this bank is NOT a clean update:"));
            // Each placement a fallback made, with the evidence the audit found for it, because the
            // slot is inferred from structure and size rather than shown by the same object, and an
            // ear is the last check on it.
            foreach (var i in audit.Inferences) r.AppendLine($"    INFERRED   {i}");
            foreach (var p in audit.Problems) r.AppendLine($"    MISPLACED  {p}");
        }
        return r.ToString();

        // Each anchor behind a carry, grouped for the plain ones and one line per fallback,
        // since a fallback is the claim a person may want to check by ear.
        IEnumerable<string> Anchors(Carried x)
        {
            foreach (var g in x.Placements.Where(p => !p.IsFallback).GroupBy(p => p.Anchor).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var who = g.Select(p => p.Now).Distinct().OrderBy(s => s).ToList();
                var noun = g.Key == SameTrack ? (who.Count == 1 ? "music track" : "music tracks") : (who.Count == 1 ? "Sound" : "Sounds");
                yield return $"{g.Key} ({noun} {string.Join(", ", who.Take(6))}{(who.Count > 6 ? $" and {who.Count - 6} more" : "")})";
            }
            foreach (var p in x.Placements.Where(p => p.IsFallback).OrderBy(p => p.Was))
                yield return (p.Anchor == EventFallback ? $"event {Ev(p.Event)} fallback" : p.Anchor) +
                             (p.Detail is null ? "" : $": {p.Detail}");
        }

        // The codec is named because a carried medium keeps whatever the mod shipped, and
        // hand-built mods embed PCM where the game uses Vorbis: that plays, but is many
        // times the size, and --recompress is the tool's answer to it.
        static string Size(byte[] b)
        {
            if (b is null) return "?";
            var s = BnkBuilder.Seconds(b);
            return $"{BnkBuilder.Inspect(b).Codec} {b.Length:N0} B" + (s is { } v ? $" {v:0.00} s" : "");
        }
        static string Sounds(IReadOnlyList<uint> s) =>
            s.Count == 0 ? "no Sound" : $"Sound{(s.Count == 1 ? "" : "s")} {string.Join(", ", s.Take(6))}" +
                                        (s.Count > 6 ? $" and {s.Count - 6} more" : "");
    }

    // ---- the whole mod ------------------------------------------------------------------

    /// <param name="WwiseBuilt">Media left out because the mod's bank built that audio itself.</param>
    /// <param name="WwiseBuiltKept">Media carried although the mod's bank built them, because the person asked.</param>
    public sealed record Result(int Banks, int Loose, int Carried, int Refused, string Report, List<string> Outputs,
                                int WwiseBuilt, int WwiseBuiltKept)
    {
        /// <summary>Loose files no bank of the mod placed, followed through the game's history: still the same sound, moved to its new id, kept unverified.</summary>
        public int LooseSame { get; init; }
        public int LooseMoved { get; init; }
        public int LooseUnverified { get; init; }

        /// <summary>Loose files nothing could place, written under <see cref="UnplacedFolder"/> to stage by hand.</summary>
        public int Unplaced { get; init; }
        public const string UnplacedFolder = "_unplaced";

        /// <summary>Sounds the mod added beside the game's events (its manifest's layers), put back on the current bank, and not.</summary>
        public int LayersPutBack { get; init; }
        public int LayersNotPutBack { get; init; }

        /// <summary>The version the loose files were read as made for, when one was decided.</summary>
        public string MadeFor { get; init; }

        /// <summary>The versions to choose between when the records disagree about a file; empty when they do not.</summary>
        public IReadOnlyList<string> Versions { get; init; } = [];

        /// <summary>Of <see cref="WwiseBuilt"/>, the media whose Sounds the current game no longer has, which the switch would not carry either.</summary>
        public int WwiseBuiltNoSlot { get; init; }

        /// <summary>Of <see cref="WwiseBuilt"/>, the media a container of the game ships byte for byte, so provably the game's own, which the switch does not carry.</summary>
        public int WwiseBuiltProven { get; init; }

        /// <summary>Of <see cref="WwiseBuilt"/>, the media under ids the game ships nowhere, which are not shown to be the game's own audio.</summary>
        public int WwiseBuiltForeign { get; init; }

        /// <summary>Of <see cref="WwiseBuilt"/>, the media the switch would carry: with a slot, and not provably the game's.</summary>
        public int WwiseBuiltFlaggable { get; init; }

        /// <summary>Of <see cref="WwiseBuiltKept"/>, the media under ids the game ships nowhere, which are not shown to be the game's own audio.</summary>
        public int WwiseBuiltKeptForeign { get; init; }

        /// <summary>
        /// Custom lines the update placed across every bank, how many the audit found on the very
        /// Sound they were written for, and how many it found where a fallback inferred their slot.
        /// </summary>
        public int AuditLines { get; init; }
        public int AuditVerified { get; init; }
        public int AuditInferred { get; init; }

        /// <summary>Every disagreement the audit found, named with its bank. An update with any is not clean.</summary>
        public IReadOnlyList<string> AuditProblems { get; init; } = [];

        /// <summary>Every inferred placement with its evidence, named with its bank, for a person to check by ear.</summary>
        public IReadOnlyList<string> AuditInferences { get; init; } = [];

        public bool Clean => AuditProblems.Count == 0 && AuditVerified + AuditInferred == AuditLines;

        public string AuditLine => Audit.Said(AuditVerified + AuditInferred, AuditLines, AuditVerified, AuditInferred);
    }

    public const string ReportName = "update-report.txt";
    public const string EventsName = "update-events.txt";

    /// <summary>
    /// Every bank in the game whose Sounds stream each medium from a loose file, by media id.
    ///
    /// A Sound streams when its stream type is 1, where the bank holds the start and the rest
    /// is read from the loose file, or 2, where all of it is. A Sound of stream type 0 plays
    /// from a bank's own data and never reads the loose file, so it is not counted. A bank
    /// that cannot be read is listed under every id as one that might, because a guard that
    /// missed a reader would let a carry change it.
    /// </summary>
    private static (Dictionary<uint, List<string>> ByMedia, List<string> Unreadable) StreamingBanks(
        GameSession session, IReadOnlyList<string> gameBanks, Action<string> progress)
    {
        var byMedia = new Dictionary<uint, List<string>>();
        var unreadable = new List<string>();
        var n = 0;
        foreach (var key in gameBanks)
        {
            if (++n % 200 == 0) progress($"finding which banks stream loose media... {n}/{gameBanks.Count}");
            var file = session.Bank(key);
            if (file is null) continue;
            Shape shape;
            try { shape = Read(file.Read(), withMedia: false); }
            catch { unreadable.Add(file.Path); continue; }
            foreach (var (sound, source) in shape.SoundSource)
            {
                if (!shape.SoundBodies.TryGetValue(sound, out var body) || !body.PlaysMedia || body.StreamType == 0)
                    continue;
                if (!byMedia.TryGetValue(source, out var list)) byMedia[source] = list = [];
                if (!list.Contains(file.Path, StringComparer.OrdinalIgnoreCase)) list.Add(file.Path);
            }
        }
        return (byMedia, unreadable);
    }

    /// <summary>
    /// Rebase every bank and loose medium in a mod onto the mounted game, into outRoot.
    /// </summary>
    /// <param name="keepWwiseBuilt">See <see cref="KeepWwiseBuiltFlag"/>.</param>
    /// <param name="history">
    /// What earlier versions of the game said about their media ids, for the loose files no bank
    /// of the mod places. Null keeps the old rule: every such file is reported and none written.
    /// </param>
    /// <param name="madeFor">The version the mod was made for, when the history leaves it open.</param>
    public static Result Build(GameSession session, string modPath, string outRoot, Settings settings,
                               Action<string> progress = null, bool keepWwiseBuilt = false,
                               GameHistory history = null, string madeFor = null)
    {
        progress ??= _ => { };
        Directory.CreateDirectory(outRoot);

        progress($"opening {Path.GetFileName(modPath)}...");
        var opened = ModBank.Open(modPath, settings, withMedia: true);

        var outputs = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sections = new StringBuilder();
        var summary = new List<string>();
        var events = new StringBuilder();
        int banks = 0, loose = 0, carriedTotal = 0, refusedTotal = 0, layersPutBack = 0, layersNotPutBack = 0;
        int looseSame = 0, looseMoved = 0, looseUnverified = 0, unplaced = 0;
        string followMadeFor = null;
        IReadOnlyList<string> followVersions = [];
        int wwiseBuilt = 0, wwiseBuiltKept = 0, wwiseBuiltNoSlot = 0, eventsNamed = 0, eventsBare = 0;
        int wwiseBuiltProven = 0, wwiseBuiltForeign = 0, wwiseBuiltFlaggable = 0, wwiseBuiltKeptForeign = 0;
        int auditLines = 0, auditVerified = 0, auditInferred = 0;
        var auditProblems = new List<string>();
        var auditInferences = new List<string>();
        var gameBanks = session.Provider.Files.Keys
            .Where(k => k.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // The banks that read a loose file, worked out the first time a carry would write
        // one. It reads every bank in the game, which most updates never need to.
        (Dictionary<uint, List<string>> ByMedia, List<string> Unreadable)? streaming = null;
        IReadOnlyList<string> OtherReaders(uint id, string path, string self)
        {
            streaming ??= StreamingBanks(session, gameBanks, progress);
            var (byMedia, unreadable) = streaming.Value;
            // A bank reads this file only when the id resolves to this very path for it: its
            // own language's folder first, then the neutral one, which is how the game looks
            // a streamed medium up.
            var readers = (byMedia.TryGetValue(id, out var list) ? list : [])
                .Where(b => !b.Equals(self, StringComparison.OrdinalIgnoreCase) &&
                            session.LooseMedia(id, GameSession.LanguageOf(b))?.Path is { } p &&
                            p.Equals(path, StringComparison.OrdinalIgnoreCase));
            return readers.Concat(unreadable.Select(u => u + " (could not be read, so it might)")).ToList();
        }

        // Every medium the game install ships, in every container it mounts and not only in the
        // copy the engine loads, worked out the first time audio the mod's bank built itself needs a
        // verdict. The base pak keeps the banks the patches replaced, so a medium there can be an
        // older version of one the current bank plays, and a mod made before that patch holds it
        // byte for byte: with --keep-wwise-built, two such media of bnk_sfx_1053303 were carried
        // and undid the patch on lines the author never touched. Reading every bank of every
        // container takes a few seconds, which only an update with such audio pays.
        Dictionary<uint, List<(string Container, GameFile File, long Offset, long Size, bool Loose)>> copies = null;
        var dataOf = new Dictionary<GameFile, byte[]>();
        void IndexCopies()
        {
            copies = [];
            void Add(uint id, (string, GameFile, long, long, bool) copy)
            {
                if (!copies.TryGetValue(id, out var list)) copies[id] = list = [];
                list.Add(copy);
            }
            var n = 0;
            foreach (var vfs in session.Provider.MountedVfs.OrderBy(v => v.ReadOrder).ThenBy(v => v.Name, StringComparer.Ordinal))
                foreach (var (key, file) in vfs.Files)
                {
                    if (key.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
                    {
                        if (MediaIdOf(key) is { } wem) Add(wem, (vfs.Name, file, 0, file.Size, true));
                        continue;
                    }
                    if (!key.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)) continue;
                    if (++n % 200 == 0) progress($"finding the media every container of the game ships... {n} banks");
                    try
                    {
                        var didx = BnkBuilder.ReadSections(file.Read()).FirstOrDefault(s => s.Tag == "DIDX");
                        if (didx is null) continue;
                        foreach (var e in BnkBuilder.ReadDidx(didx.Body)) Add(e.Id, (vfs.Name, file, e.Offset, e.Size, false));
                    }
                    catch { /* a bank that cannot be read proves nothing either way */ }
                }
        }
        GameCopy Shipped(uint id, byte[] bytes)
        {
            if (copies is null) IndexCopies();
            if (!copies.TryGetValue(id, out var found)) return new GameCopy(false);
            foreach (var x in found.Where(x => x.Size == bytes.Length))
            {
                try
                {
                    byte[] copy;
                    if (x.Loose) copy = x.File.Read();
                    else
                    {
                        if (!dataOf.TryGetValue(x.File, out var data))
                            dataOf[x.File] = data = BnkBuilder.ReadSections(x.File.Read()).FirstOrDefault(s => s.Tag == "DATA")?.Body ?? [];
                        if (x.Offset + x.Size > data.Length) continue;
                        copy = data.AsSpan((int)x.Offset, (int)x.Size).ToArray();
                    }
                    if (copy.AsSpan().SequenceEqual(bytes)) return new GameCopy(true, x.Container, x.File.Path);
                }
                catch { /* a copy that cannot be read is not one */ }
            }
            return new GameCopy(true);
        }

        // The loose files the mod ships are never written by their path. A path names only a
        // media id, and the game hands old ids to other lines, so a file written where its id
        // points can land on another line with nothing in the output looking wrong. Each one goes
        // instead to the bank whose Sound plays it, as that bank's medium, and follows the Sound
        // to wherever it plays today, where the audit sees it like any other carry. Those handed
        // to a bank are kept here so the rest can be reported once every bank has been through.
        var handed = new HashSet<ModBank.LooseMedia>();

        foreach (var bank in opened.Banks)
        {
            progress($"{bank.Name}: comparing with the game's current copy...");

            // The game's bank at the same path. A loose .bnk never said where it goes, so
            // for that alone the copy sharing the most media ids stands in, and the report
            // says that is what happened.
            string gamePath;
            var how = "";
            if (bank.MountedAt is not null)
            {
                // Through the session once the path is settled, which resolves a path several
                // containers ship to the copy the engine loads.
                gamePath = Counterpart(bank.MountedAt, gameBanks) is { } key ? session.Bank(key)?.Path : null;
            }
            else
            {
                ModBank.VanillaMedia(session, bank.Name, Read(bank.Bytes).Media.Keys.ToHashSet(), out gamePath);
                how = " (the mod named no path, so the game's copy sharing the most media ids was used)";
            }

            if (gamePath is null)
            {
                var elsewhere = gameBanks
                    .Where(k => Path.GetFileName(k).Equals(bank.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                sections.AppendLine(new string('=', 78));
                sections.AppendLine(bank.Name);
                // A bank that named no path is matched by the media ids it shares with the game's
                // banks of its name, so where the game has one and it was still not matched, that is
                // the reason, and it is said: a bank built in its own Wwise project gives its media
                // ids of its own, and then nothing says which of the game's lines its audio replaces.
                if (bank.MountedAt is null && elsewhere.Count > 0)
                {
                    sections.AppendLine($"  SKIPPED: the mod named no path for it, and it has no media id in common with the " +
                                        $"game's bank of this name ({string.Join(", ", elsewhere)}), which usually means it was " +
                                        "built in a separate Wwise project, with ids of its own, so nothing says which of the " +
                                        "game's lines its audio replaces. Nothing was written for it.");
                    summary.Add($"  {bank.Name,-24} skipped, no media id in common with the game's bank of that name");
                }
                else
                {
                    sections.AppendLine($"  SKIPPED: the game has no bank at {bank.MountedAt ?? bank.Name}" +
                                        (elsewhere.Count > 0 ? $"; it has one of this name at {string.Join(", ", elsewhere)}" : "") +
                                        ". Nothing was written for it.");
                    summary.Add($"  {bank.Name,-24} skipped, the game has no bank at that path");
                }
                sections.AppendLine();
                continue;
            }

            var current = session.Bank(gamePath).Read();
            var language = GameSession.LanguageOf(gamePath);
            var cache = new Dictionary<uint, byte[]>();
            var self = gamePath;
            Streamed StreamedAt(uint id) =>
                session.LooseMedia(id, language) is { } f
                    ? new Streamed(f.Path, () => cache.TryGetValue(id, out var b) ? b : cache[id] = f.Read())
                      { OtherBanks = () => OtherReaders(id, f.Path, self) }
                    : null;

            // The loose files this bank's Sounds play, from its own language folder or the neutral
            // one, placed as its own media so each follows the Sound that played it.
            var looseHere = LooseFor(bank.Bytes, language, opened.Media);
            handed.UnionWith(looseHere.Values);
            var looseBytes = looseHere.ToDictionary(kv => kv.Key, kv => kv.Value.Bytes);

            // Sounds this tool added beside the mod's events (its manifest names them) have no Sound
            // in the game for the rebase to place them on. Taken out of the mod's bank first, so the
            // rebase sees the mod without them, and added again to the patched bank below, anchored
            // on the Sound each one copied.
            var modBytes = bank.Bytes;
            List<LayerManifest.Taken> layersTaken = [];
            if (BnkBuilder.ReadSections(bank.Bytes).Any(s => s.Tag == VoiceLineBuilder.ManifestTag))
            {
                try
                {
                    var hb = HircBank.Parse(bank.Name, bank.Bytes);
                    layersTaken = LayerManifest.TakeOut(hb, LayerManifest.Read(hb));
                    if (layersTaken.Count > 0) modBytes = hb.Write();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A bank the typed reader cannot take apart goes to the rebase as it is.
                    layersTaken = [];
                }
            }

            var plan = Rebase(modBytes, current, StreamedAt, keepWwiseBuilt, looseBytes, Shipped);
            var written = Write(plan, current, gamePath, outRoot, taken);
            outputs.AddRange(written.Outputs);
            if (written.BankPath is not null) banks++;
            loose += plan.Carried.Count(c => c.LoosePath is not null);
            carriedTotal += plan.Carried.Count;
            refusedTotal += plan.Refused.Count;
            var builtHere = plan.Refused.Where(x => x.Kind == WwiseBuiltKind).ToList();
            var older = builtHere.Count;
            var proven = builtHere.Count(x => x.ShippedIn is not null);
            var olderKept = plan.Carried.Count(x => x.BuiltByWwise);
            wwiseBuilt += older;
            wwiseBuiltKept += olderKept;
            wwiseBuiltKeptForeign += plan.Carried.Count(x => x.BuiltByWwise && x.Foreign);
            wwiseBuiltNoSlot += builtHere.Count(x => x.NoSlot);
            wwiseBuiltProven += proven;
            wwiseBuiltForeign += builtHere.Count(x => x.Foreign);
            wwiseBuiltFlaggable += builtHere.Count(x => x.Flaggable);

            // Checked against what is on disk, read back from where it was written, so the audit
            // sees the bytes a person will pack and not the plan's own account of them.
            progress($"{bank.Name}: auditing where each line landed...");
            var outBank = written.BankPath is not null
                ? File.ReadAllBytes(Path.Combine(outRoot, written.BankPath))
                : current;
            byte[] WrittenLoose(string at)
            {
                var file = Path.Combine(outRoot, at.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(file) ? File.ReadAllBytes(file) : null;
            }
            var audit = AuditPlacement(plan, modBytes, current, outBank, WrittenLoose, session.EventName, looseBytes);
            auditLines += audit.Lines;
            auditVerified += audit.Verified;
            auditInferred += audit.Inferred;
            auditProblems.AddRange(audit.Problems.Select(p => $"{bank.Name}: {p}"));
            auditInferences.AddRange(audit.Inferences.Select(i => $"{bank.Name}: {i}"));

            // The added sounds, onto the bank as the update wrote it (or the game's, where it carried
            // nothing into it): after the audit, which checks the rebase alone.
            LayerBuild.Readded readded = null;
            if (layersTaken.Count > 0)
            {
                readded = LayerBuild.Readd(outBank, current, gamePath, layersTaken,
                                           id => session.MediaBank.ContainsKey(id) || session.LooseMedia(id) is not null,
                                           session.EventName);
                if (readded.Bytes is not null)
                {
                    var dest = Path.Combine(outRoot, gamePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, readded.Bytes);
                    var rel = Path.GetRelativePath(outRoot, dest);
                    if (written.BankPath is null)
                    {
                        outputs.Add(rel);
                        banks++;
                    }
                    // The report says what ships: this bank, as the added sounds left it.
                    written = written with { BankPath = rel, BankBytes = readded.Bytes.Length };
                }
                layersPutBack += readded.Added;
                layersNotPutBack += readded.Refused;
            }

            // Names from the game's own rows for this bank, so a carried medium reads as the
            // line it is rather than a number. A bank the catalogue cannot name still reports.
            var rows = new Dictionary<uint, SoundRow>();
            try
            {
                foreach (var row in ModBank.ToSounds(session, new ModBank.Bank(bank.Name, gamePath, current)).Rows)
                    rows.TryAdd(row.MediaId, row);
            }
            catch { }
            // The row kept for a medium is the first the index met, from an event of any action, and
            // that can be one that only stops the Sound: game-wide, 945 of 76,990 SFX media were named
            // by such an event, as a loop of bnk_sfx_1053303 was by its end event. So where the current
            // bank plays the medium through a Play action and the row's event is not one of those, the
            // line is named by one that is, which is where it is heard. Only the name changes.
            var lcur = plan.CurrentLines;
            string Name(uint id)
            {
                if (!rows.TryGetValue(id, out var row)) return null;
                var display = row.Display;
                var subtitle = row.Subtitle;
                var plays = lcur?.Players(id).SelectMany(s => lcur.PlayEvents.TryGetValue(s, out var e) ? e : [])
                                 .Distinct().ToList() ?? [];
                if (plays.Count > 0 && !plays.Contains(row.EventHash))
                {
                    var pick = plays.Select(e => (Id: e, Name: session.EventName(e)))
                                    .OrderBy(e => e.Name is null).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id)
                                    .First();
                    display = pick.Name ?? $"<unnamed:{pick.Id}>";
                    // A subtitle belongs to an event, so the row's went with the event it named. A voice
                    // bank's line is looked up again under the event that plays it; nothing else has one.
                    subtitle = row.Kind == "VO" && pick.Name is not null ? session.VoiceLine(pick.Name)?.Subtitle ?? "" : "";
                }
                return display + (string.IsNullOrWhiteSpace(subtitle) ? "" : $"  \"{subtitle}\"");
            }

            sections.AppendLine(new string('=', 78));
            sections.Append(Describe(bank.Name + how, bank.MountedAt ?? bank.SourcePath, bank.Bytes.Length,
                                     gamePath, current.Length, plan, written, Name, session.EventName,
                                     id => session.LooseMedia(id, language) is not null, audit));
            foreach (var line in readded?.Log ?? []) sections.AppendLine(line);
            sections.AppendLine();

            summary.Add($"  {bank.Name,-24} carried {plan.Carried.Count}" +
                        (olderKept > 0 ? $" ({olderKept} built by the mod's bank itself, kept by {KeepWwiseBuiltFlag})" : "") +
                        $", not carried {plan.Refused.Count}" +
                        (older > 0
                            ? $" ({older} built by the mod's bank itself" +
                              (proven > 0 ? $", {proven} of them provably the game's own older audio" : "") + ")"
                            : "") +
                        $", not a change {plan.Stock.Count}; " +
                        $"answered {plan.Current.Events.Count(plan.Mod.Events.Contains)} of {plan.Current.Events.Count} events " +
                        $"before, gains {plan.EventsGained.Count} event(s) and {plan.MediaGained.Count} media");
            // What placed each carry, counted by the weakest anchor behind it, and the audit's
            // word on the bank, so the summary alone says how far the result can be trusted.
            if (plan.Carried.Count > 0)
                summary.Add($"  {"",-24} anchors: " + string.Join(", ",
                    plan.Carried.GroupBy(x => x.Anchor).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                                .Select(g => $"{g.Count()} {g.Key}")));
            summary.Add($"  {"",-24} {audit.Line}" + (audit.Clean ? "" : $", {audit.Problems.Count} disagreement(s)"));
            if (readded is not null)
                summary.Add($"  {"",-24} added sounds: {readded.Added} of {layersTaken.Count} put back" +
                            (readded.Refused > 0 ? $", {readded.Refused} not (the reasons are in its section)" : ""));

            // An event is written by name only where the game's catalogue has one, and many
            // it does not, so the count of each is kept for the summary to say so.
            void Event(uint e)
            {
                var named = session.EventName(e);
                if (named is null) eventsBare++; else eventsNamed++;
                events.AppendLine($"    {e,-10}  {named ?? ""}");
            }
            events.AppendLine($"{gamePath}");
            events.AppendLine($"  gained by updating, {plan.EventsGained.Count} event(s):");
            foreach (var e in plan.EventsGained) Event(e);
            events.AppendLine($"  only in the mod's bank, {plan.EventsGone.Count} event(s):");
            foreach (var e in plan.EventsGone) Event(e);
            events.AppendLine();
        }

        // The loose files no bank took as one of its Sounds' media. Nothing places them but the
        // id their path names, which is the one thing never trusted on its own, so each is
        // reported and none is written. One that is byte for byte the game's own at that path is
        // no change and is only counted.
        if (opened.Media.Count > 0)
        {
            var part = new StringBuilder();
            part.AppendLine("loose media the mod ships under WwiseAudio/Media/");
            if (opened.Banks.Count == 0)
                part.AppendLine(history is null
                    ? "  This mod ships no .bnk, only loose Media files, and a loose file is named by nothing but a " +
                      "media id: nothing in it says which sound each one was. If it was built with XzoundWave, open " +
                      "its project instead -- a staged file follows its sound to the id that sound plays today."
                    : "  This mod ships no .bnk, only loose Media files named by media id. Each is placed by what the " +
                      "game's history (its recorded versions, and SoundKit's lists) says that id was when the mod was made.");
            int same = 0, left = 0;
            var playedBy = opened.Banks.Select(b => (b.Name, Ids: Read(b.Bytes, withMedia: false).SoundSource.Values.ToHashSet()))
                                       .ToList();
            var leftover = new List<ModBank.LooseMedia>();
            foreach (var file in opened.Media.Where(f => !handed.Contains(f)))
            {
                byte[] theirs = null;
                try { theirs = session.GameFileAt(file.MountedAt)?.Read(); } catch { }
                if (theirs is not null && theirs.AsSpan().SequenceEqual(file.Bytes)) same++;
                else leftover.Add(file);
            }

            // What the game's history says each one was, in the version the mod was made for, and
            // where that sound's audio is read today. Not for a file a Sound of the mod's own banks
            // plays: that bank says what it was, and its update (or the refusal below) answers for it.
            var byId = leftover.Where(f => !(MediaIdOf(f.MountedAt) is { } m && playedBy.Any(b => b.Ids.Contains(m)))).ToList();
            GameHistory.FollowResult follow = null;
            if (history is not null && byId.Count > 0)
            {
                progress("following the loose files through the game's history...");
                follow = history.Follow(session, byId, madeFor, progress);
                followMadeFor = follow.MadeFor;
                followVersions = follow.Versions;
                if (follow.MadeFor is not null) part.AppendLine($"  read as made for {follow.MadeFor}");
            }
            var verdicts = follow?.Files.ToDictionary(f => f.File) ?? [];
            foreach (var file in leftover)
            {
                var id = MediaIdOf(file.MountedAt);
                var f = verdicts.GetValueOrDefault(file);
                if (f is { Verdict: "same" or "moved" or "unverified", NewPath: not null })
                {
                    if (taken.Add(f.NewPath))
                    {
                        var dest = Path.Combine(outRoot, f.NewPath.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.WriteAllBytes(dest, file.Bytes);
                        outputs.Add(Path.GetRelativePath(outRoot, dest));
                        loose++;
                        carriedTotal++;
                        if (f.Verdict == "same") looseSame++;
                        else if (f.Verdict == "moved") looseMoved++;
                        else looseUnverified++;
                        var moves = !f.NewPath.Equals(file.MountedAt, StringComparison.OrdinalIgnoreCase);
                        part.AppendLine((f.Verdict switch
                        {
                            "moved" => "  MOVED        ",
                            "same" => "  KEPT         ",
                            _ => "  KEPT, NOT VERIFIED  ",
                        }) + file.MountedAt + (moves ? $"  ->  {f.NewPath}" : ""));
                        part.AppendLine("        " + f.Reason + (f.From is null ? "" : $"   [{f.From}]"));
                        continue;
                    }
                    f = f with { Verdict = "ambiguous", Reason = $"another of the mod's files is already written at {f.NewPath}, and that one is kept" };
                }
                left++;
                refusedTotal++;
                List<string> players = id is { } known ? playedBy.Where(b => b.Ids.Contains(known)).Select(b => b.Name).ToList() : [];
                part.AppendLine($"  NOT CARRIED  {file.MountedAt}   {file.Bytes.Length:N0} B");
                part.AppendLine("        " +
                    (f is not null ? f.Reason + (f.From is null ? "" : $"   [{f.From}]")
                    : id is null
                        ? "its name is not a media id, so nothing says which line it is"
                        : players.Count > 0
                            ? $"a Sound in {string.Join(", ", players)} plays media {id}, but that bank either was not " +
                              "updated or streams it from another language folder, so nothing places this copy"
                            : $"no Sound in the mod's banks plays media {id}, so nothing says which line of the current " +
                              "game it is. Its path names only that id, and the game hands old ids to other lines, so " +
                              "writing it there could put it on another line"));
                // Kept beside the update, under its language and bucket as it was, so it can be
                // staged on the right sound by hand and two languages of one id stay two files.
                if (history is not null)
                {
                    var under = file.MountedAt.Replace('\\', '/');
                    var media = under.IndexOf("/WwiseAudio/Media/", StringComparison.OrdinalIgnoreCase);
                    var rel = media >= 0 ? under[(media + "/WwiseAudio/Media/".Length)..] : Path.GetFileName(under);
                    var spare = Path.Combine(outRoot, Result.UnplacedFolder, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(spare)!);
                    File.WriteAllBytes(spare, file.Bytes);
                    unplaced++;
                }
            }
            if (same > 0) part.AppendLine($"  {same} file(s) are byte for byte the game's own and were left out");
            if (unplaced > 0) part.AppendLine($"  {unplaced} file(s) not carried are in {Result.UnplacedFolder}, to stage on the right sound by hand");
            summary.Insert(0, $"  loose media              {handed.Count} placed through the bank whose Sound plays them " +
                              $"(counted there)" +
                              (follow is null ? "" : $", {looseSame} still the same sound, {looseMoved} moved to their sound's new id, " +
                                                     $"{looseUnverified} kept unverified") +
                              $", not carried {left}, identical to the game {same}");
            sections.Insert(0, new string('=', 78) + Environment.NewLine + part + Environment.NewLine);
        }

        var report = new StringBuilder();
        // The mod by its name on disk, as the pak is named: a folder given as "." or with a closing
        // separator has no name as typed.
        report.AppendLine($"Updating {Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(modPath)))} onto the game's current banks.");
        report.AppendLine($"Output: {outRoot}");
        report.AppendLine();
        report.AppendLine("Each bank below is the GAME'S CURRENT bank with only the mod's audio carried onto it, so it");
        report.AppendLine("answers every event the game defines today. Nothing the mod changed in the bank's structure is");
        report.AppendLine("kept; only audio is.");
        report.AppendLine();
        // The window and the command line both pack what the update wrote into one .pak and put
        // this report beside it, so the report says that, and nothing about packing it by hand:
        // the command line is the whole of the Linux release, where there was no repak to hand.
        foreach (var line in Wrapped(banks + loose > 0
                     ? PackedParagraph(VerbPak.UpdateName(modPath))
                     : "Nothing was carried, so no .pak is written; why each sound was not is below."))
            report.AppendLine(line);
        report.AppendLine();
        report.AppendLine("SUMMARY");
        foreach (var s in summary) report.AppendLine(s);
        if (opened.Skipped.Count > 0)
            report.AppendLine($"  could not open: {string.Join(", ", opened.Skipped)}");
        report.AppendLine($"  {banks} bank(s) and {loose} loose file(s) written; " +
                          $"every event gained or lost is listed in {EventsName}, {eventsNamed:N0} by name and " +
                          $"{eventsBare:N0} as a bare id the game's event catalogue has no name for");

        // The audit across every bank, and every disagreement it found, up front: a line on
        // the wrong slot plays in game as if nothing were wrong, so this is the one result a
        // person must not have to dig for.
        var clean = auditProblems.Count == 0 && auditVerified + auditInferred == auditLines;
        report.AppendLine();
        report.AppendLine($"  {Audit.Said(auditVerified + auditInferred, auditLines, auditVerified, auditInferred)}");
        // An inferred placement is where a fallback put a line the game removed the Sound of: the
        // slot is judged from structure and size, not shown by the same object, so each is named
        // with its evidence for a person to check by ear.
        if (auditInferences.Count > 0)
        {
            report.AppendLine("  INFERRED, so check each by ear:");
            foreach (var i in auditInferences) report.AppendLine($"    {i}");
        }
        if (!clean)
        {
            report.AppendLine($"  NOT A CLEAN UPDATE: {auditProblems.Count} disagreement(s) between where a line was written for and");
            report.AppendLine("  where it landed. Each is listed here and under its bank's placement audit below:");
            foreach (var p in auditProblems) report.AppendLine($"    {p}");
        }

        // Said up front and in full, because this is the one refusal a person may need to
        // overturn: audio the mod's bank built itself is the game's own older audio unless
        // the author rebuilt the bank in the Wwise authoring tool, and only they know which.
        // Where the Sounds that played it are gone from the game, the switch would not carry
        // it either, so the count it can help with is said apart. Where some of it is proven the
        // game's own, or sits under ids the game never shipped, that is said as well, because the
        // first is no longer a guess and the second is not the game's audio at all.
        if (wwiseBuilt > 0 && (wwiseBuiltProven > 0 || wwiseBuiltForeign > 0))
        {
            var read = wwiseBuilt - wwiseBuiltProven - wwiseBuiltForeign;
            var said = new List<string>
            {
                $"LEFT OUT, BUILT BY THE MOD'S BANK ITSELF: {wwiseBuilt} medi{(wwiseBuilt == 1 ? "um" : "a")}. Every Sound that plays " +
                "each of them declares exactly its size, which is how Wwise writes audio it built and which a hand swap never changes.",
            };
            if (wwiseBuiltProven > 0)
                said.Add($"{wwiseBuiltProven} of them {(wwiseBuiltProven == 1 ? "is" : "are")} provably the game's own older audio: a " +
                         "container of the game install ships the very same bytes under the same media id, so carrying them would " +
                         $"undo a game update, and {KeepWwiseBuiltFlag} does not carry them either.");
            if (read > 0)
                said.Add($"{read} of them read{(read == 1 ? "s" : "")} as the game's own audio from before an update, since the game " +
                         "ships those media ids, though no container ships these very bytes.");
            if (wwiseBuiltForeign > 0)
                said.Add($"{wwiseBuiltForeign} of them {(wwiseBuiltForeign == 1 ? "sits" : "sit")} under media ids the game ships nowhere " +
                         "today, so nothing shows they are the game's own audio; they may be your own, built in Wwise, or lines " +
                         "of the game's under ids it has since dropped.");
            // Every one the switch would not carry is proven the game's or has no slot, so where none
            // is left the sentence names whichever of the two applies, and nothing when the proven
            // ones above already said it.
            if (wwiseBuiltFlaggable > 0)
                said.Add($"If you rebuilt your bank in the Wwise authoring tool, run again with {KeepWwiseBuiltFlag} to carry the " +
                         $"{wwiseBuiltFlaggable} of them that are not provably the game's and still have a slot in the current game.");
            else if (wwiseBuiltProven < wwiseBuilt)
                said.Add($"{KeepWwiseBuiltFlag} would carry none of them: " +
                         (wwiseBuiltProven > 0
                             ? "each is provably the game's own or has no slot in the current game."
                             : "none has a slot in the current game."));
            said.Add("Each one is listed under NOT CARRIED below.");
            report.AppendLine();
            foreach (var line in Wrapped(string.Join(" ", said))) report.AppendLine("  " + line);
        }
        else if (wwiseBuilt > 0)
        {
            var slotted = wwiseBuilt - wwiseBuiltNoSlot;
            report.AppendLine();
            report.AppendLine($"  LEFT OUT AS THE GAME'S OWN OLDER AUDIO: {wwiseBuilt} medi{(wwiseBuilt == 1 ? "um" : "a")}. The mod's bank built this");
            report.AppendLine("  audio itself (every Sound that plays it declares exactly its size, which a hand swap never");
            report.AppendLine("  changes), so it is the game's own audio from before an update, and carrying it would undo that");
            if (wwiseBuiltNoSlot == 0)
            {
                report.AppendLine("  update. If you rebuilt your bank in the Wwise authoring tool, it may be your own audio instead:");
                report.AppendLine($"  run again with {KeepWwiseBuiltFlag} to carry it. Each one is listed under NOT CARRIED below.");
            }
            else if (slotted == 0)
            {
                report.AppendLine("  update. Every Sound that played it is gone from the current game, so it has no slot to go to");
                report.AppendLine($"  and {KeepWwiseBuiltFlag} would not carry it either. Each one is listed under NOT CARRIED below.");
            }
            else
            {
                report.AppendLine("  update. If you rebuilt your bank in the Wwise authoring tool, it may be your own audio instead:");
                report.AppendLine($"  run again with {KeepWwiseBuiltFlag} to carry the {slotted} that still have a slot in the current game.");
                report.AppendLine($"  The other {wwiseBuiltNoSlot} were played only by Sounds the current game no longer has, so they have");
                report.AppendLine("  nowhere to go. Each one is listed under NOT CARRIED below.");
            }
        }
        if (wwiseBuiltKept > 0)
        {
            // Every one of these was first looked for in every container of the game install, and
            // one shipped there byte for byte would have been left out, so none is provably the
            // game's, though any may still be, from before an update or under an id since dropped.
            report.AppendLine();
            foreach (var line in Wrapped(
                         $"CARRIED BECAUSE {KeepWwiseBuiltFlag} WAS GIVEN: {wwiseBuiltKept} medi{(wwiseBuiltKept == 1 ? "um" : "a")} " +
                         "the mod's bank built itself. No container of the game install ships any of them byte for byte, so " +
                         "none is provably the game's own, but each may still be the game's own older audio and undo a game " +
                         "update" +
                         (wwiseBuiltKeptForeign > 0
                             ? $"; {wwiseBuiltKeptForeign} of them sit{(wwiseBuiltKeptForeign == 1 ? "s" : "")} under media ids the " +
                               "game ships nowhere today"
                             : "") +
                         "; each is marked under CARRIED below."))
                report.AppendLine("  " + line);
        }
        report.AppendLine();
        report.Append(sections);

        File.WriteAllText(Path.Combine(outRoot, ReportName), report.ToString(), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(outRoot, EventsName), events.ToString(), new UTF8Encoding(true));
        return new Result(banks, loose, carriedTotal, refusedTotal, report.ToString(), outputs, wwiseBuilt, wwiseBuiltKept)
               { WwiseBuiltNoSlot = wwiseBuiltNoSlot, WwiseBuiltProven = wwiseBuiltProven, WwiseBuiltForeign = wwiseBuiltForeign,
                 WwiseBuiltFlaggable = wwiseBuiltFlaggable, WwiseBuiltKeptForeign = wwiseBuiltKeptForeign,
                 AuditLines = auditLines, AuditVerified = auditVerified,
                 AuditInferred = auditInferred, AuditProblems = auditProblems, AuditInferences = auditInferences,
                 LooseSame = looseSame, LooseMoved = looseMoved, LooseUnverified = looseUnverified, Unplaced = unplaced,
                 MadeFor = followMadeFor, Versions = followVersions,
                 LayersPutBack = layersPutBack, LayersNotPutBack = layersNotPutBack };
    }

    // A paragraph of the report cut into lines of at most this many characters at spaces, the
    // width the fixed lines around it are written to.
    private static IEnumerable<string> Wrapped(string text, int width = 98)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    // The report's paragraph on the pak, which names it, because a folder updated into more than
    // once can hold more than one pak. Run reads the name back from an earlier report to find the
    // pak that update was packed into, and replaces the paragraph when its own packing fails after
    // the report was written.
    private static string PackedParagraph(string pak) =>
        $"The update is packed into one .pak, {pak}.pak, written beside this report. Put it in ~mods in place of " +
        "the old mod rather than beside it: two paks that ship the same .bnk path cannot both apply, " +
        "and the old one would hide this.";

    // The pak an update's report names, by PackedParagraph, or null where it names none, as for an
    // update that carried nothing. Only a name an update gives its pak, and only one in the report's
    // own folder: what a report says is read here to delete by, and a report is a file anyone can edit.
    // Read with its line breaks as spaces, since Wrapped breaks the sentence wherever the length of
    // the name puts it.
    private static string PackedBy(string report)
    {
        var said = System.Text.RegularExpressions.Regex.Match(
            System.Text.RegularExpressions.Regex.Replace(report, @"\s+", " "),
            @"The update is packed into one \.pak, (\S+)\.pak, written beside this report");
        var name = said.Success ? said.Groups[1].Value : null;
        return name is not null && name == Path.GetFileName(name) &&
               name.EndsWith("_updated" + VerbPak.PatchSuffix, StringComparison.OrdinalIgnoreCase) ? name : null;
    }

    /// <summary>
    ///     XzoundWave.exe --updatemod &lt;mod.pak|mod.bnk|folder&gt; &lt;outDir&gt; [--keep-wwise-built]
    /// The update packed into &lt;mod&gt;_updated_9999999_P.pak, the window's name for it, beside the
    /// report and the Marvel folder it was packed from.
    /// </summary>
    public static int Run(string modPath, string outDir, bool keepWwiseBuilt = false, string madeFor = null, string historyDir = null)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();

        // The game's paks are mounted without the ~mods folder beneath them, which is what
        // makes "the current bank" the game's own and not the very mod being updated.
        var session = new GameSession();
        session.Mount(settings, _ => { });
        // Where a sound's audio sits today -- whole in a bank, or loose -- is read from the index.
        session.IndexBanks(_ => { });

        // An earlier update into this folder left files placed by what it read then; a second reading
        // (another --made-for) must not pack beside them. Nor may the pak they were packed into
        // outlive them: a reading that carries nothing writes no pak, and the old one would then be
        // the only thing left to install. That pak is the one the earlier report names, which has
        // this run's own name only when it updated the same mod: an update of another mod into the
        // same folder otherwise left its pak behind with neither its files nor its report. The mod
        // being updated is never deleted, even when it is that earlier pak, since it is read below.
        var pakName = VerbPak.UpdateName(modPath);
        var earlier = Path.Combine(outDir, ReportName);
        if (File.Exists(earlier))
        {
            foreach (var old in new[] { "Marvel", Result.UnplacedFolder }.Select(d => Path.Combine(outDir, d)).Where(Directory.Exists))
                Directory.Delete(old, true);
            foreach (var pak in new[] { pakName, PackedBy(File.ReadAllText(earlier)) }
                         .Where(p => p is not null).Select(p => Path.Combine(outDir, p + ".pak")).Distinct(AppPaths.Comparer)
                         .Where(p => File.Exists(p) && !AppPaths.SamePath(p, modPath)))
                File.Delete(pak);
        }

        try
        {
            var history = historyDir is null ? GameHistory.Load() : GameHistory.Load(historyDir);
            var r = Build(session, modPath, outDir, settings, W, keepWwiseBuilt, history, madeFor);
            if (r.Versions.Count > 1)
                W($"Some loose files read differently in {string.Join(", ", r.Versions.Select(v => $"\"{v}\""))}: " +
                  "run again with --made-for and one of them, in quotes.");
            W("");
            W(r.Report);
            W($"{r.Banks} bank(s) and {r.Loose} loose file(s) written, {r.Carried} carried, {r.Refused} not carried.");
            if (r.LayersPutBack + r.LayersNotPutBack > 0)
                W($"Added sounds: {r.LayersPutBack} put back, {r.LayersNotPutBack} not" +
                  (r.LayersNotPutBack > 0 ? $" (why is in {ReportName})." : "."));
            // Repeated as the last thing printed, because a person who reads only the end of
            // the output is the one most likely to miss audio that was left out on purpose.
            if (r.WwiseBuilt > 0 && (r.WwiseBuiltProven > 0 || r.WwiseBuiltForeign > 0))
            {
                // Not all of it is the same kind of left out: some is shown to be the game's, and
                // some is under ids the game ships nowhere, so it is counted apart and not called
                // the game's own older audio as a whole.
                W($"{r.WwiseBuilt} of those not carried were built by the mod's bank itself" +
                  (r.WwiseBuiltProven > 0
                      ? $"; {r.WwiseBuiltProven} of them provably the game's own older audio, shipped byte for byte by a " +
                        "container of the game"
                      : "") +
                  (r.WwiseBuiltForeign > 0
                      ? $"; {r.WwiseBuiltForeign} under media ids the game ships nowhere today, so not shown to be the game's own audio"
                      : "") +
                  (r.WwiseBuiltFlaggable == 0
                      ? $"; {KeepWwiseBuiltFlag} would carry none of them."
                      : $"; if you made that bank in the Wwise authoring tool, run again with {KeepWwiseBuiltFlag} to carry " +
                        $"the {r.WwiseBuiltFlaggable} of them that are not provably the game's and still have a slot in the current game."));
            }
            else if (r.WwiseBuilt > 0)
            {
                var slotted = r.WwiseBuilt - r.WwiseBuiltNoSlot;
                W($"{r.WwiseBuilt} of those not carried are the game's own older audio, built by the mod's bank itself; " +
                  (slotted == 0
                      ? $"none of it has a slot in the current game, so {KeepWwiseBuiltFlag} would not carry it either."
                      : $"if you made that bank in the Wwise authoring tool, run again with {KeepWwiseBuiltFlag} to carry " +
                        (r.WwiseBuiltNoSlot == 0 ? "them." : $"the {slotted} of them that still have a slot in the current game.")));
            }
            if (r.WwiseBuiltKept > 0)
                W($"{r.WwiseBuiltKept} carried only because {KeepWwiseBuiltFlag} was given; none is shipped byte for byte by the game, " +
                  "but each may still be the game's own older audio.");
            // And the audit, last of all, because it is the line that says whether the result
            // can be trusted, and a disagreement is never reported as a clean update.
            W(r.AuditLine);
            foreach (var i in r.AuditInferences) W("  INFERRED  " + i);
            if (!r.Clean)
            {
                W($"NOT A CLEAN UPDATE: {r.AuditProblems.Count} line(s) did not land on the slot they were written for:");
                foreach (var p in r.AuditProblems) W("  " + p);
            }
            // Packed as the window packs it, from the update's own list of what it wrote, so the
            // _unplaced files and the report stay beside the pak and never go into it. Said with
            // the report's path: both are where the result is, not what it is.
            var packed = VerbPak.Pack(outDir, pakName, r.Outputs, W);
            // Build wrote the report before there was a pak to write, and it says the pak is beside
            // it. The window copies the report out only once its pak is written, so only here can it
            // be left saying so of a pak that is not there, for someone to look for in vain or to
            // install a stale one by its name; it says what happened instead.
            if (!packed)
                File.WriteAllText(Path.Combine(outDir, ReportName), r.Report.Replace(
                    string.Join(Environment.NewLine, Wrapped(PackedParagraph(pakName))),
                    string.Join(Environment.NewLine, Wrapped(
                        $"Packing the update into {pakName}.pak failed, so there is no .pak of this update; one of that " +
                        "name beside this report is an earlier run's, and the command's output says why packing failed. " +
                        "Every file the update wrote is in the Marvel folder beside this report. Run the update again once " +
                        "the cause is fixed, and put the .pak it writes in ~mods in place of the old mod rather than " +
                        "beside it: two paks that ship the same .bnk path cannot both apply."))),
                    new UTF8Encoding(true));
            W($"report: {Path.Combine(outDir, ReportName)}");
            return !packed ? 1 : !r.Clean ? 2 : r.Banks + r.Loose > 0 ? 0 : 1;
        }
        catch (Exception ex) { W("UPDATE FAILED: " + ex.Message); return 1; }
    }
}
