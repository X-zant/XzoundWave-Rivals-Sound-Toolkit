using CUE4Parse.UE4.Wwise;
using CUE4Parse.UE4.Wwise.Objects;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

/// <summary>
/// The music half of a soundbank: the media that hang off MusicTrack objects instead of
/// off a Sound.
///
/// A music bank is built out of different Wwise objects than a character bank. The game's
/// one music bank holds over twelve hundred MusicTrack objects carrying more than six
/// hundred distinct media and exactly ONE HierarchySoundSfxVoice, so a walk that keeps
/// only Sound objects finds one row and hides 36.9 MB of shipped music behind it. A
/// MusicTrack is the same shape as a Sound apart from carrying an ARRAY of sources: it
/// owns a BaseParams chain the same way and each source is an AkBankSourceData the same
/// way, which is why the row machinery needs nothing new from it.
///
/// The counts quoted through this file were measured on that bank. Three of the game's
/// containers ship it with a different HIRC chunk, timing 625, 627 and 655 media, and the
/// session hands over the copy the engine would load, which is the newest patch's 1,265
/// tracks and 655 media. Nothing here depends on the figure; the walk reports whatever the
/// bank it was handed contains.
///
/// Two things only this walk can state. Names: an event reaches a medium through
/// MusicSwitch to MusicRanSeq to MusicSegment to MusicTrack, and the event is the only
/// object in these banks whose id has a shipped asset to spell it, because the banks
/// carry no STID name table and no track, segment or container id matches an asset stem.
/// Lengths: the track's playlist states each clip's own duration, which is the figure
/// the bank places the clip by and the one number a replacement can contradict.
/// </summary>
public static class MusicIndex
{
    /// <summary>
    /// One place a medium is used: the track that carries it, the source row inside that
    /// track, and what that track's playlist and enclosing segment state there.
    ///
    /// A medium used in more than one place has several of these, and they do not have to
    /// agree. Measured on music_formal: of the 347 media it places more than once, 69
    /// disagree about the level a reader would act on, 102 about the length of the segment
    /// the clip plays in and 24 about whether the playlist trims it, while 0 disagree about
    /// the clip's own duration. So the duration is a property of the medium and the level
    /// and the segment are properties of the PLACEMENT, which is why they travel here
    /// rather than on the clip.
    /// </summary>
    public sealed record Placement(HierarchyMusicTrack Track, AkBankSourceData Source,
                                   double? Seconds, double? SegmentSeconds, bool Trimmed);

    /// <summary>One music medium, and what its bank states about it.</summary>
    /// <param name="Track">
    /// The node itself, never its BaseParams: the inherited property chain starts at the
    /// object that OWNS the properties, and one track owns one chain for several sources.
    ///
    /// The FIRST track in hierarchy order that carries this medium, which is what the row
    /// describes. <see cref="Placements"/> holds every use of the medium including this
    /// one, so a reader can be told that the numbers on the row are one placement of
    /// several rather than the whole truth.
    /// </param>
    public sealed record Clip(uint MediaId, HierarchyMusicTrack Track, AkBankSourceData Source)
    {
        /// <summary>
        /// Every place this medium is used, in hierarchy order, the first being the one the
        /// fields above describe. ONE ENTRY PER SOURCE OCCURRENCE, not one per track: a
        /// track may carry the same medium more than once, and 7 of music_formal's do, so 4
        /// of its media have more entries here than they have tracks. Medium 679409166 is
        /// three entries in a single track and 299299185 is nine entries in five, and a
        /// count of these read out as a track count would be wrong about both. One entry
        /// for a medium used once, which is 308 of that bank's 655.
        ///
        /// Kept because the alternative was silence: the walk used to drop every occurrence
        /// after the first before reading any of its properties, discarding 665 of this
        /// bank's 1,320 source occurrences, and a row would then state one placement's level
        /// and segment as though they were the medium's. Nothing downstream changed shape
        /// for this, since there is still exactly one clip per medium, and the cost is
        /// bounded by the walk the tracks already get.
        /// </summary>
        public List<Placement> Placements { get; init; } = [];

        /// <summary>
        /// The clip's own length, as the playlist row that places it states it, or as the
        /// last of them where one track places it more than once. Read Placements below
        /// for why those cannot be told apart.
        /// </summary>
        public double? Seconds { get; init; }

        /// <summary>
        /// The stated length of the segment the track sits in, when it states one. A
        /// segment as long as its clip is a segment whose length IS that audio, so a
        /// longer replacement is cut by the segment rather than merely mistimed.
        /// </summary>
        public double? SegmentSeconds { get; init; }

        /// <summary>
        /// The playlist trims this clip, so its in and out points were authored against
        /// the audio the game shipped and mean something else against anything longer.
        /// </summary>
        public bool Trimmed { get; init; }

        public uint EventHash { get; init; }
        public string EventName { get; init; }

        /// <summary>The action that reached it, whose own delay applies on top.</summary>
        public HierarchyEventAction Action { get; init; }

        /// <summary>The other events that reach the same medium, named.</summary>
        public List<string> AlsoPlayedBy { get; init; } = [];
    }

    /// <summary>What one event action reaches, and what it is called.</summary>
    private sealed record Route(uint EventId, string Name, HierarchyEventAction Action, List<uint> Media);

    /// <summary>
    /// Every distinct music medium in one bank, in the order its tracks appear.
    ///
    /// ONE ROW PER MEDIUM, not one per source occurrence and not one per event.
    /// music_formal carries 1,267 sources for 627 distinct ids, 327 of them in more than
    /// one track and one in seventeen, so a row per occurrence would say a swap lands in
    /// seventeen places when it lands in one. Its 61 events reach 951 (event, medium)
    /// pairs for those media, with a single event reaching 620 of them, so a row per
    /// event would inflate the same audio instead of describing it. A swap lands on the
    /// medium, so the medium is the unit and the events that were not chosen are named
    /// on the row.
    ///
    /// Every occurrence is kept all the same, in <see cref="Clip.Placements"/>, the chosen
    /// one among them. The others were being thrown away before their properties were read,
    /// which left a row stating one placement's level and segment as though the medium had
    /// only one.
    /// </summary>
    /// <param name="nameOf">
    /// Event hash to shipped name. Null when the caller wants only the timings, which is
    /// all a build needs, and which skips the event walk entirely.
    /// </param>
    public static List<Clip> Read(IEnumerable<AbstractHierarchy> ordered,
                                  IReadOnlyDictionary<uint, AbstractHierarchy> nodes,
                                  Func<uint, string> nameOf)
    {
        var all = ordered as IReadOnlyList<AbstractHierarchy> ?? ordered.ToList();
        var tracks = all.OfType<HierarchyMusicTrack>().ToList();
        if (tracks.Count == 0) return [];   // every shipped bank but one stops here

        // Which routes reach which medium. Built medium-first because the choice below
        // is per medium, and 61 routes times 620 media is nothing to walk twice.
        var reaching = new Dictionary<uint, List<Route>>();
        if (nameOf is not null)
            foreach (var route in Routes(all, nodes, nameOf))
                foreach (var id in route.Media)
                {
                    if (!reaching.TryGetValue(id, out var list)) reaching[id] = list = [];
                    list.Add(route);
                }

        // Every occurrence, gathered before any clip is built, because the medium is the
        // unit and a medium's placements are only all known once every track has been
        // walked. The order list is what keeps the rows in the order the tracks appear:
        // a dictionary's own order is not a promise, and a bank that reads differently on
        // every run makes the same row impossible to find twice.
        var order = new List<uint>();
        var placesById = new Dictionary<uint, List<Placement>>();
        foreach (var track in tracks)
        {
            var segment = nodes.GetValueOrDefault(track.BaseParams?.DirectParentId ?? 0)
                          as HierarchyMusicSegment;

            // What this track's playlist states about each medium it places: the last stated
            // duration wins and any trim anywhere makes the medium trimmed. Read once per
            // medium here rather than once per source row, so the collapse below is one
            // stated decision instead of a side effect of which loop the scan sat in.
            //
            // It IS a collapse, and it is the one figure this walk cannot promise. A source
            // row and a playlist entry are tied together by the source id and by nothing
            // finer, so a track that places one medium three times offers no reading that
            // hands the second placement the second entry's duration. Pairing them by
            // position would be an invention rather than a reading of the bank, and it has
            // no answer at all where the two arrays differ in length, which music_formal
            // does on 4 of its 1,265 tracks: 1,320 source rows against 1,324 playlist
            // entries, one track carrying medium 299299185 once while placing it twice.
            // Making the playlist entry the unit instead would be the honest fix and is the
            // one worth doing if this ever matters, at the cost of every occurrence count
            // downstream changing meaning.
            //
            // Not done now because nothing is lost. All 7 of the shipped bank's tracks that
            // carry one medium more than once state a single clip length for it and agree
            // about the trim, so the collapse changes no figure the window shows. A patch
            // that authored two lengths for one of those 7 would be read as one and this
            // walk could not say so, and that 7 is the number to re-measure before trusting
            // a duration on a medium a single track places twice.
            var stated = new Dictionary<uint, (double? Seconds, bool Trimmed)>();
            foreach (var entry in track.Playlist ?? [])
            {
                if (entry.SourceId == 0) continue;
                var was = stated.GetValueOrDefault(entry.SourceId);
                stated[entry.SourceId] =
                    (entry.SrcDuration > 0 ? entry.SrcDuration / 1000.0 : was.Seconds,
                     was.Trimmed || entry.BeginTrimOffset != 0 || entry.EndTrimOffset != 0);
            }

            foreach (var source in track.Sources ?? [])
            {
                var id = source.SourceId != 0 ? source.SourceId : source.FileId;
                if (id == 0) continue;

                // A source row the playlist never places states nothing, which is the same
                // answer the per-row scan came to when it found no matching entry.
                var says = stated.GetValueOrDefault(id);

                if (!placesById.TryGetValue(id, out var places))
                {
                    placesById[id] = places = [];
                    order.Add(id);
                }
                places.Add(new Placement(track, source, says.Seconds,
                                         segment?.Duration > 0 ? segment.Duration / 1000.0 : null,
                                         says.Trimmed));
            }
        }

        var clips = new List<Clip>();
        foreach (var id in order)
        {
            var places = placesById[id];
            var first = places[0];

            var routes = reaching.GetValueOrDefault(id) ?? [];
            var pick = Pick(routes);

            // By event and not by route: one event can reach the same medium through
            // two of its actions, and listing the chosen event among the OTHERS that
            // play this clip reads as two events that happen to share a name.
            var others = pick is null ? [] : routes
                .Where(r => r.Name is not null && r.EventId != pick.EventId &&
                            !r.Name.Equals(pick.Name, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            clips.Add(new Clip(id, first.Track, first.Source)
            {
                Seconds = first.Seconds,
                SegmentSeconds = first.SegmentSeconds,
                Trimmed = first.Trimmed,
                EventHash = pick?.EventId ?? 0,
                EventName = pick?.Name,
                Action = pick?.Action,
                AlsoPlayedBy = others,
                Placements = places,
            });
        }
        return clips;
    }

    /// <summary>
    /// The route that describes a medium best: a named one over an unnamed one, then the
    /// one that reaches the fewest media.
    ///
    /// Specificity is the whole point. Of music_formal's 35 chosen events one reaches 620
    /// media and the rest reach between one and 29, so taking whichever route happened to
    /// be walked first would label a map's loading music "Entry" and lose the event that
    /// is actually about it. The remaining order is by name and then by id so the same
    /// bank reads the same way on every run.
    /// </summary>
    private static Route Pick(List<Route> routes) => routes
        .OrderByDescending(r => r.Name is not null)
        .ThenBy(r => r.Media.Count)
        .ThenBy(r => r.Name ?? "", StringComparer.OrdinalIgnoreCase)
        .ThenBy(r => r.EventId)
        .FirstOrDefault();

    /// <summary>
    /// What each of the bank's event actions reaches, walking DOWN from the action.
    ///
    /// Downward, because that is the direction a name travels: walking up from a track
    /// arrives at a MusicSwitchContainer, and no container in these banks has a name to
    /// give. One route per action rather than per event, because the delay a build has to
    /// report belongs to the action.
    /// </summary>
    private static IEnumerable<Route> Routes(IReadOnlyList<AbstractHierarchy> all,
                                             IReadOnlyDictionary<uint, AbstractHierarchy> nodes,
                                             Func<uint, string> nameOf)
    {
        foreach (var ev in all.OfType<HierarchyEvent>())
        {
            var name = nameOf(ev.Id);
            foreach (var actionId in ev.EventActionIds ?? [])
            {
                if (nodes.GetValueOrDefault(actionId) is not HierarchyEventAction act) continue;
                var media = Media(act.ReferencedId, nodes, []).Where(id => id != 0).Distinct().ToList();
                if (media.Count == 0) continue;
                yield return new Route(ev.Id, name, act, media);
            }
        }
    }

    /// <summary>
    /// The media below one object, through the music containers.
    ///
    /// Only the music node types are descended here. The generic containers are the
    /// existing walk's business, and an event whose action points at one is already
    /// reached by it, so descending them again would produce the same row twice.
    /// MusicRanSeq also carries a playlist of its children, but its ChildIds reach every
    /// medium in the bank on their own (628 of 628 measured), so the simpler member is
    /// the one used.
    /// </summary>
    private static IEnumerable<uint> Media(uint id, IReadOnlyDictionary<uint, AbstractHierarchy> nodes,
                                           HashSet<uint> seen)
    {
        if (!seen.Add(id) || !nodes.TryGetValue(id, out var node)) yield break;
        switch (node)
        {
            case HierarchyMusicTrack track:
                foreach (var s in track.Sources ?? [])
                    yield return s.SourceId != 0 ? s.SourceId : s.FileId;
                break;
            case HierarchyMusicSegment segment:
                foreach (var c in segment.ChildIds ?? [])
                    foreach (var x in Media(c, nodes, seen)) yield return x;
                break;
            case HierarchyMusicRandomSequenceContainer ranseq:
                foreach (var c in ranseq.ChildIds ?? [])
                    foreach (var x in Media(c, nodes, seen)) yield return x;
                break;
            case HierarchyMusicSwitchContainer switcher:
                foreach (var c in switcher.ChildIds ?? [])
                    foreach (var x in Media(c, nodes, seen)) yield return x;
                break;
        }
    }

    /// <summary>
    /// What a bank's own bytes state about its clips, keyed by media id.
    ///
    /// For the build, which holds the bank it is about to rewrite and no index of it. A
    /// bank with no music tracks costs one HIRC parse and returns nothing.
    /// </summary>
    public static Dictionary<uint, Clip> Timings(byte[] bank, string name)
    {
        var byId = new Dictionary<uint, Clip>();
        try
        {
            var reader = new WwiseReader(new FWwiseArchive(name, bank), new WwiseArchiveSource());
            if (reader.Hierarchies is null) return byId;

            var ordered = new List<AbstractHierarchy>();
            var nodes = new Dictionary<uint, AbstractHierarchy>();
            foreach (var h in reader.Hierarchies)
            {
                if (h.Data is null) continue;
                ordered.Add(h.Data);
                nodes[h.Data.Id] = h.Data;
            }
            foreach (var clip in Read(ordered, nodes, null)) byId.TryAdd(clip.MediaId, clip);
        }
        catch { }   // a warning must never be the reason a build fails
        return byId;
    }

    /// <summary>
    /// The bank's stated durations agree with the audio to within a millisecond on every
    /// clip measured, so anything past a hundredth of a second is a real difference and
    /// not the format rounding.
    /// </summary>
    public const double Tolerance = 0.01;

    public static bool Disagrees(double? stated, double? got) =>
        stated is > 0 && got is > 0 && Math.Abs(stated.Value - got.Value) > Tolerance;

    /// <summary>
    /// What to say about a replacement the bank's timing no longer describes.
    ///
    /// One wording for every caller. The build log, the staging dialog and any later
    /// reader of this must not each invent their own account of the same hazard, and the
    /// sentence has to describe the inconsistency rather than promise a symptom: nothing
    /// here has watched Wwise play a clip that disagrees with its stated length.
    /// </summary>
    public static string LengthNote(double? stated, double? segment, bool trimmed, double got)
    {
        var text = $"the .bnk times this clip at {stated:0.###} s and this audio is {got:0.###} s. " +
                   "A music bank places a clip by that figure and a rebuild leaves the hierarchy " +
                   "exactly as the game shipped it, so the bank's timing keeps describing the old length.";
        if (trimmed)
            text += " The playlist also trims this clip, and those in and out points were set " +
                    "against the audio being replaced.";
        if (segment is { } seg && Math.Abs(seg - (stated ?? 0)) <= Tolerance)
            text += $" The segment it plays in is {seg:0.###} s long, which is this clip's own length, " +
                    "so the segment is no longer than the audio it was built around.";
        return text;
    }
}
