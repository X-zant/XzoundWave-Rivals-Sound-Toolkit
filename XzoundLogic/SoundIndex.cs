using System.IO;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.UE4.Wwise.Enums;
using CUE4Parse.UE4.Wwise.Objects;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

public sealed class SoundRow
{
    public string Kind { get; init; }          // VO / SFX / DISPLAY
    public string Bank { get; init; }
    public uint EventHash { get; init; }
    public string EventName { get; init; }     // null when the bank's hash has no shipped asset
    public uint MediaId { get; init; }
    public bool Streamed { get; init; }        // the bank declares this source as streaming
    public string Codec { get; init; }
    public string Origin { get; set; }         // where the bytes actually came from
    public long Bytes { get; set; }
    public string Category { get; set; } = "";

    /// <summary>
    /// The Category column is authored in Chinese and is not localized by the game.
    /// Both forms are kept: the original is the truth, the translation is a reading
    /// aid, and one switch decides which the grid shows.
    /// </summary>
    public string CategoryEnglish { get; set; }
    public static bool ShowTranslatedCategory { get; set; }
    public string CategoryShown =>
        ShowTranslatedCategory && !string.IsNullOrEmpty(CategoryEnglish) ? CategoryEnglish : Category;
    public string Subtitle { get; set; } = "";
    public double? Seconds { get; set; }

    /// <summary>
    /// What the .bnk does to this sound on top of the audio -- a volume trim, a pitch
    /// shift, a delay, a loop. Replacing the file does not replace any of it, which is
    /// the single most useful thing to know before swapping a sound and wondering why
    /// the result is quieter or late.
    /// </summary>
    public WwiseProps.Applied Applied { get; set; }
    public string AppliedLabel => Applied?.Summary ?? "";

    /// <summary>
    /// The level behind the label, so the column sorts by how loud the .bnk makes the
    /// sound rather than alphabetically -- which would put "+2 dB" beside "-6 dB" and
    /// "SILENT" somewhere else entirely. Quietest first, so the silenced ones gather.
    /// </summary>
    public double AppliedSort => Applied?.StaticDb ?? 0;

    /// <summary>
    /// Other events that play this same media. A sound reached by several events is one
    /// a swap changes in all of them, which is either the point or a surprise.
    /// </summary>
    public List<string> AlsoPlayedBy { get; set; } = [];

    /// <summary>
    /// A short name for the set of alternatives this sound belongs to, unique within
    /// the skin. The container's own id would do the job and be unreadable; this is
    /// the same fact in something you can sort a column by and see the group gather.
    /// </summary>
    public string VariantGroup { get; set; } = "";

    /// <summary>
    /// The group's number on its own, because "V10" sorts before "V2" as text and a
    /// column of variation groups is exactly where that reads as broken. Combined with
    /// the position inside the group, so sorting gathers a group AND puts its members
    /// in order. Rows in no group sort last rather than pretending to be group zero.
    /// </summary>
    public int VariantGroupNumber { get; set; }

    /// <summary>
    /// Variation groups first, then families, then everything else -- each in its own
    /// number order, so sorting gathers a set rather than scattering it alphabetically.
    /// </summary>
    public long GroupSort =>
        VariantGroupNumber > 0 ? (long)VariantGroupNumber * 100_000 + (Applied?.Alternative?.Index ?? 0)
        : FamilyMembers.Count > 1 ? 1_000_000_000L + (long)FamilyNumber * 100_000
        : long.MaxValue;

    /// <summary>The other alternatives, so the group can be read off one row.</summary>
    public List<string> Siblings { get; set; } = [];

    /// <summary>
    /// A short name for the set of related sounds this one is mixed with, unique
    /// within the skin. Sounds whose events are called release1..release4 share one,
    /// which is how the grouping is knowable without reading the names.
    /// </summary>
    public string Family { get; set; } = "";
    public int FamilyNumber { get; set; }
    public List<string> FamilyMembers { get; set; } = [];

    /// <summary>
    /// The alternatives inside one container when there are any, and otherwise the
    /// family. Both answer "what does this belong with"; only the first carries odds.
    /// </summary>
    public string GroupLabel => Applied?.Alternative is { Count: > 1 } v
        ? $"{VariantGroup}  {v.Index + 1}/{v.Count}" +
          (v.Share.Length == 0 ? "" : $"  {v.Share}")
        : FamilyMembers.Count > 1 ? $"{Family}  of {FamilyMembers.Count}" : "";

    public string GroupDetail
    {
        get
        {
            var lines = new List<string>();

            if (Applied?.Alternative is { Count: > 1 } v)
            {
                lines.Add($"{VariantGroup}: {v.Count} alternatives, picked at {v.Mode}.");
                lines.Add(v.FromWeights
                    ? "The shares are the container's own weights, read from the .bnk."
                    : "This container states no weights, so the split is assumed even.");
                lines.Add("");
                lines.AddRange(Siblings);
            }

            if (FamilyMembers.Count > 1)
            {
                if (lines.Count > 0) lines.Add("");
                lines.Add($"{Family}: mixed together with {FamilyMembers.Count - 1} other " +
                          "sound(s), which is how they are known to belong together without " +
                          "reading their names.");
                // The question this answers most often, and the answer is no.
                lines.Add("It is a mixing group, not an order. Which of them plays first is " +
                          "the game's own logic, and the .bnk says nothing about it.");
                lines.Add("");
                lines.AddRange(FamilyMembers.Take(12));
                if (FamilyMembers.Count > 12)
                    lines.Add($"  …and {FamilyMembers.Count - 12} more");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public string AppliedDetail
    {
        get
        {
            if (Applied is null) return "Not read for this .bnk.";

            var lines = new List<string> { Applied.Detail };
            if (Applied.SampleRate > 0)
                lines.Add($"Source: {Applied.SampleRate:N0} Hz.");

            if (AlsoPlayedBy.Count > 0)
            {
                lines.Add("");
                lines.Add($"Also played by {AlsoPlayedBy.Count} other event(s), which this " +
                          "swap would change too:");
                foreach (var e in AlsoPlayedBy.Take(8)) lines.Add("  " + e);
                if (AlsoPlayedBy.Count > 8) lines.Add($"  …and {AlsoPlayedBy.Count - 8} more");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Every place this music medium is used, when its bank uses it in more than one. ONE
    /// ENTRY PER SOURCE OCCURRENCE, not one per track: 7 of music_formal's tracks carry one
    /// medium more than once, so medium 679409166 is three entries in a single track and
    /// 299299185 is nine entries in five, and reading this count out as a number of tracks
    /// would be wrong on 4 of that bank's media. Empty on everything else, which is every
    /// sound outside a music bank and the 308 of its 655 media that are used once.
    ///
    /// The row's own numbers are the first entry's, because a row is one row per medium
    /// and something has to be shown. This is what lets that be said out loud instead of
    /// read as the whole truth: 141 of that bank's 655 rows have another placement stating
    /// a different level or a different segment length.
    /// </summary>
    public List<MusicPlacement> Placements { get; set; } = [];

    /// <summary>
    /// Whether the placements disagree about anything a reader would act on. Compared on
    /// the level, the segment and the trim and NOT on the clip's own duration, because
    /// none of the 347 media the bank places more than once disagree about that one. Media
    /// and not tracks: 345 of those 347 are in more than one track, and the other two are
    /// one track placing the same medium twice.
    /// </summary>
    public bool PlacementsDiffer => Placements
        .Select(p => (p.Db, p.Driven, p.SegmentSeconds, p.Trimmed))
        .Distinct().Count() > 1;

    /// <summary>User-kept notes, persisted in notes.json. Editable in the grid.</summary>
    public string Notes { get; set; } = "";
    public string ExtraNotes { get; set; } = "";

    /// <summary>
    /// Tags from the shared vocabulary -- see <see cref="SoundTags"/>. Separate from
    /// the free-text notes above on purpose: these are the part that can be searched
    /// and compared between two people's findings, which only works if nobody is free
    /// to invent their own wording.
    /// </summary>
    public List<string> Tags { get; set; } = [];
    public string TagLabel => SoundTags.Format(Tags);
    public bool HasTag(string code) => SoundTags.Matches(Tags, code);

    /// <summary>Number assigned by the last numbered test bank, if any.</summary>
    public int? TestNumber { get; set; }
    public string TestLabel => TestNumber is null ? "" : "#" + TestNumber;

    /// <summary>
    /// Silence this one in the next test build. When several sounds fire at once you
    /// can only make out the loudest; muting the number you already identified is how
    /// the ones underneath it become audible.
    /// </summary>
    public bool Muted { get; set; }

    /// <summary>
    /// Loudness multiplier for this sound, or null to follow the project's bulk
    /// level. Always applied to the ORIGINAL audio, so changing it twice does not
    /// compound the loss of two lossy round trips.
    /// </summary>
    public double? Volume { get; set; }

    /// <summary>Set by hand, so a bulk change leaves it alone.</summary>
    public bool VolumeByHand { get; set; }

    /// <summary>The level actually used, given the project's bulk setting.</summary>
    public double EffectiveVolume(double bulk) => Volume ?? bulk;

    public string VolumeLabel => Volume is null ? "" : $"{Volume:0.0#}x" + (VolumeByHand ? "*" : "");

    /// <summary>Custom .wem staged for this media id, if any.</summary>
    public string ReplacementPath { get; set; }

    /// <summary>Set when something other than staging decides the tag, e.g. a bank
    /// opened from a mod where this entry differs from the shipped one.</summary>
    public string ModTag { get; set; }

    /// <summary>
    /// Staging wins over the diff tag: a file you just dropped in is the thing you
    /// are working on. Note the explicit null test -- ModTag is "" for an entry a mod
    /// left untouched, and "" is not null, so `ModTag ?? ...` would swallow every
    /// staged row in an opened bank.
    /// </summary>
    public string Mod =>
        Muted ? "MUTED"
        : ReplacementPath is not null ? "REPLACED"
        : ModTag ?? "";

    public string Display => EventName ?? $"<unnamed:{EventHash}>";
    /// <summary>The spreadsheet's cell format, generated rather than typed.</summary>
    public string SheetCell => $"{MediaId}-{Display}";
    public string Length => Seconds is null ? "" : $"{Seconds.Value:0.00}s";
}

/// <summary>
/// What one of a music medium's placements states, reduced to the four things its tracks
/// were measured to disagree about.
///
/// The <see cref="WwiseProps.Applied"/> it was read from is not kept: a row with 18
/// placements would then carry 18 property chains for the sake of one number each, and
/// every figure here is one the pane prints.
/// </summary>
/// <param name="Db">The fixed part of the level this placement's chain comes to.</param>
/// <param name="Driven">
/// A game parameter drives that level, so the number above is where it starts. Kept
/// beside the number rather than folded into it, because two placements that state the
/// same dB and disagree about this do not agree.
/// </param>
/// <param name="Shown">This is the placement the row's own columns describe.</param>
public sealed record MusicPlacement(double Db, bool Driven, double? Seconds,
                                    double? SegmentSeconds, bool Trimmed, bool Shown);

public sealed class SkinSounds
{
    public List<SoundRow> Rows { get; } = [];
    public Dictionary<uint, CUE4Parse.UE4.Wwise.FDeferredByteData> Embedded { get; } = [];

    /// <summary>media id -> the bank that actually embeds it. Knowing which bank a
    /// swap lands in is what stops people rebuilding banks they never touched.</summary>
    public Dictionary<uint, string> BankOfMedia { get; } = [];

    /// <summary>
    /// Bytes held directly rather than read from the game, which is how a bank
    /// somebody else built gets played and exported. These win over everything else:
    /// a mod keeps the shipped media ids, so looking the id up in the game would
    /// return the VANILLA sound and quietly show the wrong audio.
    /// </summary>
    public Dictionary<uint, byte[]> Raw { get; } = [];

    /// <summary>Where an entry of <see cref="Raw"/> came from, when it is not an opened .bnk.</summary>
    public Dictionary<uint, string> RawFrom { get; } = [];
}

public static class SoundIndex
{
    public static SkinSounds Build(GameSession session, SkinEntry skin, string charId,
                                   Action<string> progress)
    {
        var result = new SkinSounds();
        var subs = LoadSubtitles(session, skin.SkinId, charId);

        foreach (var bank in skin.Banks)
        {
            progress($"Reading {Path.GetFileName(bank.Path)}...");
            WwiseReader w;
            try
            {
                using var ar = bank.CreateReader();
                w = new WwiseReader(new FWwiseArchive(ar), new WwiseGameFileSource(bank));
            }
            catch { continue; }   // one unparsable bank must not lose the rest

            if (w.Hierarchies is null) continue;

            var thisBank = Path.GetFileName(bank.Path);
            foreach (var (k, v) in w.WwiseEncodedMedias)
                if (uint.TryParse(k, out var mid))
                {
                    result.Embedded[mid] = v;
                    result.BankOfMedia[mid] = thisBank;
                }

            var nodes = new Dictionary<uint, AbstractHierarchy>();
            foreach (var h in w.Hierarchies)
                if (h.Data is not null) nodes[h.Data.Id] = h.Data;

            // The property-id table is version specific, so the version travels with
            // the walk rather than being assumed.
            var version = w.Header.Version;

            var bankName = Path.GetFileNameWithoutExtension(bank.Path);
            var kind = bankName.Contains("_vo_", StringComparison.OrdinalIgnoreCase) ? "VO"
                     : bankName.Contains("_sfx_", StringComparison.OrdinalIgnoreCase) ? "SFX"
                     : "DISPLAY";

            var claimed = new HashSet<uint>();

            foreach (var h in w.Hierarchies)
            {
                if (h.Data is not HierarchyEvent ev) continue;
                var name = session.EventName(ev.Id);

                foreach (var actionId in ev.EventActionIds)
                {
                    if (!nodes.TryGetValue(actionId, out var an) || an is not HierarchyEventAction act)
                        continue;
                    // The same walk a container's stated membership is read with
                    // (WwiseProps.SoundsUnder), so a set cannot list a medium the rows were
                    // never built from, or leave one out that they were.
                    foreach (var snd in WwiseProps.SoundsUnder(act.ReferencedId, nodes))
                    {
                        var id = snd.Source.SourceId != 0 ? snd.Source.SourceId : snd.Source.FileId;
                        if (id == 0) continue;
                        claimed.Add(id);
                        result.Rows.Add(MakeRow(session, kind, bankName, ev.Id, name, snd, snd.Source,
                                               id, subs, nodes, version, ActionDelay(act)));
                    }
                }
            }

            // Sounds no event in THIS bank reaches. For SFX especially these are still
            // playable and still worth listing -- the event may live in another bank.
            foreach (var h in w.Hierarchies)
            {
                if (h.Data is not HierarchySoundSfxVoice snd) continue;
                var id = snd.Source.SourceId != 0 ? snd.Source.SourceId : snd.Source.FileId;
                if (id == 0 || claimed.Contains(id)) continue;
                claimed.Add(id);
                result.Rows.Add(MakeRow(session, kind, bankName, 0, null, snd, snd.Source, id, subs,
                                       nodes, version, 0));
            }

            // Music. Neither walk above can see it: a music medium hangs off a MusicTrack
            // rather than off a Sound, and the game's music bank holds over twelve hundred
            // tracks and a single Sound, so the list showed one row for 36.9 MB of audio.
            // Those media are as replaceable as any other -- 80 sit in that bank's DIDX
            // and the rest ship as loose Media/ wems, both of which the build already
            // handles -- so what was missing was the row, not the ability to swap it.
            //
            // Walked in hierarchy order rather than the node map's, so a bank whose rows
            // share an event name still lists them the same way on every run.
            foreach (var clip in MusicIndex.Read(w.Hierarchies.Select(h => h.Data).Where(d => d is not null),
                                                 nodes, session.EventName))
            {
                if (claimed.Contains(clip.MediaId)) continue;
                claimed.Add(clip.MediaId);
                var row = MakeRow(session, kind, bankName, clip.EventHash, clip.EventName,
                                  clip.Track, clip.Source, clip.MediaId, subs, nodes, version,
                                  ActionDelay(clip.Action));
                // The bank states this clip's own length, so there is nothing to measure:
                // the figure agrees with the audio to a millisecond on every clip checked,
                // and filling it spares the Measure durations button five hours of decoding.
                row.Seconds = clip.Seconds;
                row.AlsoPlayedBy = clip.AlsoPlayedBy;
                if (row.Applied is { } applied)
                {
                    applied.ClipSeconds = clip.Seconds;
                    applied.SegmentSeconds = clip.SegmentSeconds;
                    applied.ClipTrimmed = clip.Trimmed;
                }
                // What the other tracks carrying this medium state. Read here rather than
                // in MusicIndex because the property chain needs the bank's VERSION, which
                // is a fact about the file this loop is holding and not about the walk; and
                // only when there is more than one placement, so the 308 media placed once
                // cost nothing and the build's timings walk stays one chain per medium.
                if (clip.Placements.Count > 1)
                    row.Placements = [.. clip.Placements.Select((p, at) =>
                    {
                        var chain = at == 0 ? row.Applied
                                  : WwiseProps.Read(p.Track, nodes, version, ActionDelay(clip.Action));
                        return new MusicPlacement(
                            chain?.StaticDb ?? 0,
                            chain?.DrivenProps.Any(d => d.Unit == WwiseProps.Unit.Db) ?? false,
                            p.Seconds, p.SegmentSeconds, p.Trimmed, at == 0);
                    })];
                result.Rows.Add(row);
            }
        }

        // A media id reached by both a named event and its _stop/_p2 aliases produces
        // duplicate rows. Keep the named one; the alias adds nothing a modder can use.
        var named = result.Rows.Where(r => r.EventName is not null)
                               .Select(r => r.MediaId).ToHashSet();
        result.Rows.RemoveAll(r => r.EventName is null && named.Contains(r.MediaId));

        var seen = new HashSet<string>();
        result.Rows.RemoveAll(r => !seen.Add($"{r.Bank}|{r.EventHash}|{r.MediaId}"));

        // Which other events reach the same audio. Only knowable once every bank of
        // the skin has been read, so it happens here rather than per row.
        foreach (var group in result.Rows.GroupBy(r => r.MediaId))
        {
            var names = group.Select(r => r.EventName).Where(n => n is not null)
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count <= 1) continue;
            foreach (var row in group)
                row.AlsoPlayedBy = names
                    .Where(n => !n.Equals(row.EventName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        }
        // The media id breaks a tie on the name. Several rows CAN share one: a music bank
        // names 468 of its 627 tracks after the one event that plays nearly all of them, and
        // sorting 468 equal keys with an unstable sort put the list in a different order on
        // every run, which made the same row impossible to find twice.
        // Named events first: an unnamed one shows as "<unnamed:hash>", and "<" sorts ahead of
        // every letter, so with unnamed rows on screen a bank opened on a wall of hashes.
        result.Rows.Sort((a, b) =>
        {
            var byNamed = (a.EventName is null).CompareTo(b.EventName is null);
            if (byNamed != 0) return byNamed;
            var byName = string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : a.MediaId.CompareTo(b.MediaId);
        });
        NameVariantGroups(result.Rows);
        return result;
    }

    /// <summary>
    /// Give every set of alternatives a short name, and let each of its members list
    /// the others. Numbered in the order they first appear in the sorted list, so the
    /// same skin always reads the same way.
    /// </summary>
    private static void NameVariantGroups(List<SoundRow> rows)
    {
        var names = new Dictionary<uint, string>();
        foreach (var row in rows)
        {
            if (row.Applied?.Alternative is not { Count: > 1 } v) continue;
            if (!names.TryGetValue(v.GroupId, out var name))
                names[v.GroupId] = name = "V" + (names.Count + 1);
            row.VariantGroup = name;
            row.VariantGroupNumber = int.Parse(name[1..]);
        }

        // The same idea one level up: sounds sharing a mixer are a set.
        var families = new Dictionary<uint, string>();
        foreach (var row in rows)
        {
            var id = row.Applied?.FamilyId ?? 0;
            if (id == 0) continue;
            if (!families.TryGetValue(id, out var name))
                families[id] = name = "F" + (families.Count + 1);
            row.Family = name;
            row.FamilyNumber = int.Parse(name[1..]);
        }
        foreach (var family in rows.Where(r => r.Family.Length > 0)
                                   .GroupBy(r => r.Applied.FamilyId))
        {
            var members = family.ToList();
            if (members.Count <= 1) continue;
            foreach (var row in members)
                row.FamilyMembers = members
                    .Select(m => (m == row ? "> " : "  ") +
                                 (m.EventName ?? $"media {m.MediaId}"))
                    .ToList();
        }

        // Read off each row's own container rather than gathered from the rows sharing its
        // group id. Those two disagree wherever the bank builds one set of media into
        // several containers, which it does constantly: the rows spread across whichever
        // container each medium was met in first, so container 183834482 of bnk_sfx_1022001
        // holds three media and gathering rows on it finds one. 47 sets across skins
        // 1030001, 1029001, 1034001, 1022001 and 1048001 were listed short that way, and a
        // set of one listed itself and called that the set.
        //
        // The marker goes on the slot this row sits in and on its medium both, because a
        // medium the container holds twice would otherwise be marked in a slot the reader
        // did not come from.
        foreach (var row in rows.Where(r => r.VariantGroup.Length > 0))
        {
            var v = row.Applied.Alternative;
            row.Siblings = v.Members
                .Select(m => (m.Index == v.Index && m.Media.Contains(row.MediaId) ? "> " : "  ") +
                             $"{m.Index + 1}. " +
                             // A slot whose child is a container reaches several media and
                             // has no single one to name, so it says what it holds instead
                             // of naming the first and passing it off as the slot. Worded
                             // as the panel words the same slot, so a reader meeting one in
                             // the tooltip and one on screen sees the same line.
                             (m.Media.Count == 1 ? $"media {m.Media[0]}"
                              : m.Media.Count == 0 ? "no media in this bank"
                              : $"a group of {m.Media.Count}: " + string.Join(", ", m.Media)) +
                             (m.Share.Length == 0 ? "" : $"   {m.Share}"))
                .ToList();
        }
    }

    /// <summary>
    /// Seconds the action itself waits before starting the sound. Separate from the
    /// sound's own delay: the same sound reached by two events can be late in one and
    /// immediate in the other.
    /// </summary>
    private static float ActionDelay(HierarchyEventAction act)
    {
        if (act?.PropBundle is not { } bundle || bundle.Props is null) return 0;
        foreach (var prop in bundle.Props)
            if (prop.Id == 0x0F) return prop.Value.f32;   // DelayTime
        return 0;
    }

    /// <param name="owner">
    /// The node the source belongs to, which is what the inherited property chain starts
    /// from. The NODE and not its BaseParams: WwiseProps looks that block up by name on
    /// whatever it is handed, so a BaseHierarchy would end the chain before a single
    /// property was read and every row's Applied column would come back blank.
    /// </param>
    /// <param name="src">
    /// The one source this row is about. A Sound has exactly one and a MusicTrack has an
    /// array of them, so the source travels separately from the node that owns it.
    /// </param>
    private static SoundRow MakeRow(GameSession session, string kind, string bank,
                                    uint evHash, string evName, AbstractHierarchy owner,
                                    AkBankSourceData src,
                                    uint mediaId, Dictionary<string, (string Cat, string Line)> subs,
                                    IReadOnlyDictionary<uint, AbstractHierarchy> nodes,
                                    uint version, float actionDelay)
    {
        var loose = session.LooseMedia(mediaId);
        var row = new SoundRow
        {
            Kind = kind,
            Bank = bank,
            EventHash = evHash,
            EventName = evName,
            MediaId = mediaId,
            Streamed = src.SourceType == EAKBKSourceType.Streaming,
            Codec = src.Plugin.PluginId.ToString(),
            // LOOSE FIRST. The bank's embedded voice copy is a prefetch stub -- a few
            // hundred bytes of the opening moment. Reading it first gives a 0.03 s click.
            Origin = loose is not null ? loose.Path : "bank (embedded)",
            Bytes = loose?.Size ?? 0,
            Applied = WwiseProps.Read(owner, nodes, version, actionDelay),
        };
        if (evName is not null && subs.TryGetValue(evName, out var s))
        {
            row.Category = s.Cat;
            row.Subtitle = s.Line;
        }
        else if (evName is not null && session.VoiceLine(evName) is { } any)
        {
            // Not in this character's own tables. Boss, NPC, system and tutorial banks
            // belong to no character at all, and even a hero has lines that only a
            // shared table describes -- so fall back to every voice table in the game.
            row.Category = any.Category;
            row.Subtitle = any.Subtitle;
        }
        return row;
    }


    /// <summary>
    /// event name -> (category, subtitle).
    ///
    /// A character's lines are spread across SEVERAL tables, not one: the skin's own,
    /// every other skin of the same character, and seasonal tables named after the
    /// event that added them (2207_1016001_HeroVoice). Reading only the first one
    /// found left 453 of Loki's 1032 events with no subtitle even though they speak
    /// in game -- the newer map lines live in a later table.
    ///
    /// They are merged in priority order and the first writer wins, so a skin's own
    /// wording beats another skin's for the same event.
    /// </summary>
    private static Dictionary<string, (string, string)> LoadSubtitles(
        GameSession session, string skinId, string charId)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        // A bank with no character (ambience, music, UI) has no voice table. Without
        // this, an empty charId makes the Contains() below match ALL 185 of them.
        if (string.IsNullOrWhiteSpace(charId) || charId.Length < 4) return map;

        var tables = session.Provider.Files.Keys
            .Where(k => k.EndsWith("_HeroVoice.uasset", StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(k).Contains(charId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => Rank(k, skinId, charId))
            .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var hit in tables)
        {
            try
            {
                var pkg = hit[..^".uasset".Length];
                var dt = session.Provider.LoadPackageObject<UDataTable>(
                    pkg + "." + Path.GetFileName(pkg));
                foreach (var (_, row) in dt.RowMap)
                {
                    var ev = row.GetOrDefault<FPackageIndex>("Event")?.Name;
                    if (string.IsNullOrEmpty(ev)) continue;
                    // First writer wins: the tables are already in priority order.
                    if (map.ContainsKey(ev)) continue;
                    map[ev] = (row.GetOrDefault<FName>("Category").Text ?? "",
                               session.Localize(row.GetOrDefault<FText>("Lines")));
                }
            }
            catch { /* a missing usmap makes this unreadable; the list still works */ }
        }
        return map;
    }

    /// <summary>This skin first, then the character's default, then everything else.</summary>
    private static int Rank(string path, string skinId, string charId)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals($"{skinId}_HeroVoice", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals($"{charId}001_HeroVoice", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.StartsWith(charId, StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;   // seasonal tables, named after the event that added them
    }
}
