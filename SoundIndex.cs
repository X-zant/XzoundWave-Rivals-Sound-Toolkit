using System.IO;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.UE4.Wwise.Enums;
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
                    foreach (var snd in Sounds(act.ReferencedId, nodes, []))
                    {
                        var id = snd.Source.SourceId != 0 ? snd.Source.SourceId : snd.Source.FileId;
                        if (id == 0) continue;
                        claimed.Add(id);
                        result.Rows.Add(MakeRow(session, kind, bankName, ev.Id, name, snd, id, subs));
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
                result.Rows.Add(MakeRow(session, kind, bankName, 0, null, snd, id, subs));
            }
        }

        // A media id reached by both a named event and its _stop/_p2 aliases produces
        // duplicate rows. Keep the named one; the alias adds nothing a modder can use.
        var named = result.Rows.Where(r => r.EventName is not null)
                               .Select(r => r.MediaId).ToHashSet();
        result.Rows.RemoveAll(r => r.EventName is null && named.Contains(r.MediaId));

        var seen = new HashSet<string>();
        result.Rows.RemoveAll(r => !seen.Add($"{r.Bank}|{r.EventHash}|{r.MediaId}"));
        result.Rows.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static SoundRow MakeRow(GameSession session, string kind, string bank,
                                    uint evHash, string evName, HierarchySoundSfxVoice snd,
                                    uint mediaId, Dictionary<string, (string Cat, string Line)> subs)
    {
        var loose = session.LooseMedia(mediaId);
        var row = new SoundRow
        {
            Kind = kind,
            Bank = bank,
            EventHash = evHash,
            EventName = evName,
            MediaId = mediaId,
            Streamed = snd.Source.SourceType == EAKBKSourceType.Streaming,
            Codec = snd.Source.Plugin.PluginId.ToString(),
            // LOOSE FIRST. The bank's embedded voice copy is a prefetch stub -- a few
            // hundred bytes of the opening moment. Reading it first gives a 0.03 s click.
            Origin = loose is not null ? loose.Path : "bank (embedded)",
            Bytes = loose?.Size ?? 0,
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

    private static IEnumerable<HierarchySoundSfxVoice> Sounds(
        uint id, Dictionary<uint, AbstractHierarchy> nodes, HashSet<uint> seen)
    {
        if (!seen.Add(id) || !nodes.TryGetValue(id, out var n)) yield break;
        switch (n)
        {
            case HierarchySoundSfxVoice s:
                yield return s; break;
            case HierarchyRandomSequenceContainer r:
                foreach (var c in r.ChildIds) foreach (var x in Sounds(c, nodes, seen)) yield return x;
                break;
            case HierarchySwitchContainer sw:
                foreach (var c in sw.ChildIds) foreach (var x in Sounds(c, nodes, seen)) yield return x;
                break;
            case HierarchyLayerContainer l:
                foreach (var c in l.ChildIds) foreach (var x in Sounds(c, nodes, seen)) yield return x;
                break;
            case HierarchyActorMixer m:
                foreach (var c in m.ChildIds) foreach (var x in Sounds(c, nodes, seen)) yield return x;
                break;
        }
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
