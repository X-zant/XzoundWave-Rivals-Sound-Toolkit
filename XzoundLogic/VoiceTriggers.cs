using CUE4Parse.UE4.Assets.Exports;
using Newtonsoft.Json.Linq;

namespace MRAudioKit;

/// <summary>
/// The triggers a voice line can be added to, as the voice-lines window offers them. Each is
/// a VoiceItem field plus what the game's own code does with it (mapped from the decompiled
/// voice scripts: what fires it, how an entry is chosen, whether an added line or entry is
/// ever picked). The window builds a <see cref="VoiceLine.Target"/> from one of these; the
/// builder follows the path without knowing which trigger it came from.
/// </summary>
public static class VoiceTriggers
{
    public enum Shape
    {
        /// <summary>One list of lines; the game picks one at random. Path: Field.Voices.</summary>
        Single,
        /// <summary>Entries under Field.Inner, each keyed (by hero, map, ...) with its own lines.</summary>
        Keyed,
        /// <summary>A list of entries directly in Field, each keyed with its own lines.</summary>
        List,
    }

    /// <param name="Inner">for Keyed: the list inside the field (KillVoices, SawKills, EscortVoices, TriggerVoices)</param>
    /// <param name="Keys">the fields that decide when an entry applies, set on a new entry</param>
    /// <param name="NewEntry">whether the game ever chooses a new entry (false: add lines only)</param>
    /// <param name="Voices">the entry's list of lines</param>
    /// <param name="HeroKey">the entry is keyed by a hero (HeroID + SculptID)</param>
    /// <param name="PickKey">the key the game picks an entry by, alone, when it is not 0: a NEW entry
    /// needs a value of it that no entry has yet, and not 0, or the game never picks it</param>
    /// <param name="HealTagDefers">an entry tagged <see cref="HealTag"/> is never picked when the
    /// VoiceItem voices heals through the two "Healed" triggers (its bClassifyHpForHealBuffVo)</param>
    /// <param name="NeedsAllies">the line is only ever heard by the speaker's teammates</param>
    public sealed record Trigger(string Field, string Group, string Name, string Description, Shape Shape,
                                 string Inner = null, string[] Keys = null, bool NewEntry = false,
                                 string Voices = "Voices", bool HeroKey = false, string PickKey = null,
                                 bool HealTagDefers = false, bool NeedsAllies = false);

    /// <summary>The buff tag of a heal, as the game's buff lines name it.</summary>
    public const string HealTag = "Voice.Ability.Healing";

    /// <summary>The VoiceItem flag that hands every heal to the two "Healed" triggers.</summary>
    public const string HealsByHealthFlag = "bClassifyHpForHealBuffVo";

    public static readonly IReadOnlyList<Trigger> All =
    [
        // ---- kills -------------------------------------------------------------------------
        new("KillVoices", "Kills", "Kills a specific hero",
            "When this hero gets a kill, after multi-kill, ult, sniper, terrain, assist and revenge lines. Entries are keyed by the victim; the \"anyone\" entry plays when no victim matches.",
            Shape.Keyed, "KillVoices", ["HeroID", "SculptID"], NewEntry: true, HeroKey: true),
        new("KillVoicesOfNormalAbility", "Kills", "Kills with an ability",
            "When this hero's kill came from the entry's ability.",
            Shape.List, Keys: ["BelongingAbilityID"], NewEntry: true, Voices: "KillVoices[0].Voices"),
        new("KillVoiceOfUltimateAbility", "Kills", "Ult kill",
            "A kill with the ult (or while it is active). One line is picked at random, whoever the victim.",
            Shape.Keyed, "KillVoices"),
        new("KillVoiceOfNormalAttack", "Kills", "Kill with the primary attack",
            "A kill from the hero's main attack. One line at random.",
            Shape.Keyed, "KillVoices"),
        new("ThreeKillConfig", "Kills", "Three kills in a row", "A third kill in quick succession.", Shape.Keyed, "KillVoices"),
        new("FiveKillConfig", "Kills", "Five kills in a row", "A fifth kill in quick succession.", Shape.Keyed, "KillVoices"),
        new("AceKillConfig", "Kills", "Team wipe", "The last enemy of a team goes down.", Shape.Keyed, "KillVoices"),
        new("AssistKillConfig", "Kills", "Assist", "A teammate gets a kill this hero assisted; this hero speaks.", Shape.Keyed, "KillVoices"),
        new("KillSniperConfig", "Kills", "Kills the sniper", "This hero kills Black Widow (hero 1033).", Shape.Keyed, "KillVoices"),
        new("TerrainKillConfig", "Kills", "Environmental kill", "A kill by knocking the enemy off the map.", Shape.Keyed, "KillVoices"),
        new("RevengeVoiceConfig", "Kills", "Revenge kill", "Kills the enemy who last killed this hero.", Shape.Keyed, "KillVoices"),
        new("SawAllyKillVoices", "Kills", "A teammate's kill",
            "This hero sees a teammate get a kill. Entries are keyed by the teammate who got it.",
            Shape.Keyed, "SawKills", ["HeroID", "SculptID"], NewEntry: true, HeroKey: true),
        new("SawEnemyKillVoices", "Kills", "A teammate goes down",
            "This hero sees a teammate killed. Entries are keyed by the teammate who died.",
            Shape.Keyed, "SawKills", ["HeroID", "SculptID"], NewEntry: true, HeroKey: true),

        // ---- abilities and buffs -----------------------------------------------------------
        new("UltimateVoices", "Abilities and buffs", "Ult", "When the ult is cast.", Shape.Single),
        // An ability's entries for one stage can differ only by their condition (a team-up's
        // Base or Advance state); the game plays the first whose condition holds, so the
        // condition is a key too, or a line meant for one would land in the other.
        new("AbilitiesVoices", "Abilities and buffs", "Ability lines",
            "When the entry's ability reaches its stage (activate, end, ...), under the entry's condition (None: always).",
            Shape.List, Keys: ["AbilityID", "Stage", "ConditionTag.TagName"]),
        // The game picks an applied buff's entry by the ability it came from alone; the kind of
        // buff is compared only for a buff no ability applied (VoCompProxy_Buff._get_vo_config).
        // Both stay keys, so entries that differ only by their tag stay apart.
        new("ApplyBuffVoices", "Abilities and buffs", "Gives a buff",
            "When this hero applies any buff from the entry's ability; the buff's kind (its tag) counts only for buffs no ability applied. A new entry needs an ability that has none yet.",
            Shape.List, Keys: ["Tag.TagName", "OwnedAbilityID"], NewEntry: true, PickKey: "OwnedAbilityID"),
        new("TakeBuffVoices", "Abilities and buffs", "Receives a buff",
            "When this hero receives the entry's kind of buff (its tag), from the entry's ability (0: any).",
            Shape.List, Keys: ["Tag.TagName", "OwnedAbilityID"], NewEntry: true, HealTagDefers: true),
        new("LowHpHealBuffVoiceConfig", "Abilities and buffs", "Healed while low", "Healed while on low health.", Shape.Single),
        new("NormalHpHealBuffVoiceConfig", "Abilities and buffs", "Healed", "Healed while not on low health.", Shape.Single),
        new("ReviveVoices", "Abilities and buffs", "Revived", "Revived by a teammate's ability listed in the entry.", Shape.List),
        new("RebornVoices", "Abilities and buffs", "Respawn", "When this hero respawns.", Shape.Single),
        new("HealthPackVoices", "Abilities and buffs", "Health pack", "When this hero picks up a health pack.", Shape.Single),
        new("DamagedVoices", "Abilities and buffs", "Takes damage", "When this hero takes heavy damage.", Shape.Single),
        new("SawDamagedVoices", "Abilities and buffs", "Warns a teammate hit from behind",
            "When a teammate is hit from behind or the side, this hero warns them if it is nearby and in their view. Only that teammate hears it, so it needs Teammates under Heard by.",
            Shape.Single, NeedsAllies: true),

        // ---- team ----------------------------------------------------------------------------
        new("SynergyEntranceVoices", "Team", "Team-up entrance",
            "When this hero joins with the team-up partner in the entry. Base lines play at the base level.",
            Shape.List, Keys: ["BondID"], Voices: "BaseVoices"),
        new("ChangeHeroVoices", "Team", "Picked mid-match", "When a player switches to this hero.", Shape.Single),

        // ---- maps and objectives ----------------------------------------------------------
        new("EnemySpottedVoices", "Maps and objectives", "Spots an enemy", "When this hero spots an enemy.", Shape.Single),
        new("SniperSpottedVoices", "Maps and objectives", "Spots a sniper", "When this hero spots an enemy sniper.", Shape.Single),
        new("BuildingSpottedVoices", "Maps and objectives", "Spots a building", "When this hero spots the entry's kind of summoned building.", Shape.List, Keys: ["BuildingTag.TagName"], NewEntry: true),
        new("BuildingDestroyedVoices", "Maps and objectives", "Destroys a building", "When this hero destroys the entry's kind of summoned building.", Shape.List, Keys: ["BuildingTag.TagName"], NewEntry: true),
        new("EscortingPayloadVoices", "Maps and objectives", "Escorting", "While escorting the payload, on the entry's map.", Shape.Keyed, "EscortVoices", ["BindMapName"], NewEntry: true),
        new("RemindEscortVoices", "Maps and objectives", "Reminds to escort", "Reminding the team to escort, on the entry's map.", Shape.Keyed, "EscortVoices", ["BindMapName"], NewEntry: true),
        new("TriggerAmbientVoices", "Maps and objectives", "Map spots", "Entering the entry's map trigger.", Shape.Keyed, "TriggerVoices", ["TriggerKey"], NewEntry: true),

        // ---- match flow ----------------------------------------------------------------------
        new("AttackRunningOutTimeVoices", "Match flow", "Time running out (attacking)", "Time is running out while attacking.", Shape.Single),
        new("DefendRunningOutTimeVoices", "Match flow", "Time running out (defending)", "Time is running out while defending.", Shape.Single),
        new("TipBattleVoices", "Match flow", "Battle tip", "A battle callout.", Shape.Single),
        new("TipAttackVoices", "Match flow", "Attack call", "An attack callout.", Shape.Single),
        new("TipDefendVoices", "Match flow", "Defend call", "A defend callout.", Shape.Single),
        new("TipCelebrateVoices", "Match flow", "Celebrate", "A celebration callout.", Shape.Single),
        new("EnemyQuantityAdvantageVoices", "Match flow", "Outnumbered",
            "When the enemy team outnumbers this hero's team near the objective.", Shape.Single),
    ];

    /// <summary>One existing entry of a trigger in a voice set, for the window to show and add to.</summary>
    public sealed record Entry(int Index, IReadOnlyDictionary<string, string> Keys, IReadOnlyList<int> Voices);

    /// <summary>
    /// The trigger's entries as one of the set's VoiceItems holds them (<paramref name="item"/>,
    /// a package of <see cref="VoiceSet.ItemPackages"/>; null: the first), read through
    /// CUE4Parse. Empty when the VoiceItem lacks the field (the window then offers nothing).
    /// A hero with several forms keeps one VoiceItem per form, and they differ.
    /// </summary>
    public static List<Entry> Entries(GameSession session, VoiceSet set, Trigger t, string item = null) =>
        Entries(LoadItem(session, item ?? set.ItemPackages[0]), t);

    /// <summary>Every trigger's entries in one VoiceItem of a set, from one read of it.</summary>
    public static Dictionary<string, List<Entry>> AllEntries(GameSession session, VoiceSet set, string item = null)
    {
        var loaded = LoadItem(session, item ?? set.ItemPackages[0]);
        return All.ToDictionary(t => t.Field, t => Entries(loaded, t));
    }

    /// <summary>A top-level flag of a VoiceItem, as in bClassifyHpForHealBuffVo.</summary>
    public static bool ItemFlag(GameSession session, string item, string field) =>
        LoadItem(session, item).Properties.FirstOrDefault(p => p.Name.Text == field)?.Tag?.GenericValue is true;

    private static UObject LoadItem(GameSession session, string itemPackage)
    {
        var pkg = VoiceSet.VfsPath(itemPackage)[..^".uasset".Length];
        return session.Provider.LoadPackageObject(pkg + "." + System.IO.Path.GetFileName(pkg));
    }

    private static List<Entry> Entries(UObject item, Trigger t)
    {
        var prop = item.Properties.FirstOrDefault(p => p.Name.Text == t.Field);
        if (prop?.Tag?.GenericValue is null) return [];
        var v = JToken.FromObject(prop.Tag.GenericValue);
        IEnumerable<JToken> list = t.Shape switch
        {
            Shape.Single => [v],
            Shape.Keyed => v[t.Inner] as JArray ?? new JArray(),
            _ => v as JArray ?? new JArray(),
        };
        var entries = new List<Entry>();
        var i = 0;
        foreach (var e in list)
        {
            var keys = (t.Keys ?? []).ToDictionary(k => k, k => e.SelectToken(k)?.ToString() ?? "");
            var voices = e.SelectToken(t.Voices) is JArray a ? a.Select(x => (int)x).ToList() : [];
            entries.Add(new Entry(i++, keys, voices));
        }
        return entries;
    }

    /// <summary>The target path for one more line in an existing entry (or in a single list).</summary>
    public static string AddTo(Trigger t, int index) => t.Shape switch
    {
        Shape.Single => $"{t.Field}.{t.Voices}",
        Shape.Keyed => $"{t.Field}.{t.Inner}[{index}].{t.Voices}",
        _ => $"{t.Field}[{index}].{t.Voices}",
    };

    /// <summary>The target path for a new entry with the given key values.</summary>
    public static string NewEntryPath(Trigger t, IReadOnlyDictionary<string, string> keys)
    {
        if (!t.NewEntry) throw new InvalidOperationException($"{t.Name}: the game never picks a new entry here");
        var spec = "+" + string.Join(",", (t.Keys ?? []).Select(k => $"{k}={keys[k]}"));
        return t.Shape == Shape.Keyed ? $"{t.Field}.{t.Inner}[{spec}].{t.Voices}" : $"{t.Field}[{spec}].{t.Voices}";
    }

    /// <summary>
    /// The target for a line under one entry: by its keys wherever the trigger's entries have
    /// them (and, for a trigger the game picks new entries of, made when missing), by position
    /// otherwise. Keys survive a game update that reorders a list; a position does not.
    /// </summary>
    public static string Target(Trigger t, IReadOnlyDictionary<string, string> keys, int index)
    {
        if (t.Shape == Shape.Single) return $"{t.Field}.{t.Voices}";
        var spec = t.Keys is { Length: > 0 }
            ? (t.NewEntry ? "+" : "") + string.Join(",", t.Keys.Select(k => $"{k}={keys[k]}"))
            : index.ToString();
        return t.Shape == Shape.Keyed ? $"{t.Field}.{t.Inner}[{spec}].{t.Voices}" : $"{t.Field}[{spec}].{t.Voices}";
    }

    /// <summary>A line's entry as its target names it: key values, or a position (-1 when neither).</summary>
    public static (Dictionary<string, string> Keys, int Index) Condition(VoiceLine line)
    {
        var keys = new Dictionary<string, string>();
        var open = line.Target.IndexOf('[');
        if (open < 0) return (keys, -1);
        var spec = line.Target[(open + 1)..line.Target.IndexOf(']', open)];
        if (int.TryParse(spec, out var index)) return (keys, index);
        foreach (var kv in spec.TrimStart('+').Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = kv.IndexOf('=');
            if (eq > 0) keys[kv[..eq].Trim()] = kv[(eq + 1)..].Trim();
        }
        return (keys, -1);
    }

    /// <summary>The entry a set already has with these key values, or null.</summary>
    public static Entry Find(IEnumerable<Entry> entries, IReadOnlyDictionary<string, string> keys) =>
        entries.FirstOrDefault(e => keys.Count > 0 && keys.All(kv => e.Keys.TryGetValue(kv.Key, out var v)
                                                                  && VoiceItemPath.SameKey(v, kv.Value)));

    /// <summary>What one row of a voice table says, and whether "relevant" players hear it (bRelevantHear).</summary>
    public sealed record Row(string Subtitle, bool Relevant);

    /// <summary>
    /// Every line of a voice set's table by VoiceID, so an entry can be shown by what its lines
    /// say rather than by its position, and a new line's fourth audience shown as its template's.
    /// </summary>
    public static Dictionary<int, Row> Rows(GameSession session, VoiceSet set)
    {
        var pkg = VoiceSet.VfsPath(set.TablePackage)[..^".uasset".Length];
        var dt = session.Provider.LoadPackageObject<CUE4Parse.UE4.Assets.Exports.Engine.UDataTable>(pkg + "." + System.IO.Path.GetFileName(pkg));
        var map = new Dictionary<int, Row>();
        foreach (var (_, row) in dt.RowMap)
            map[row.GetOrDefault<int>("VoiceID")] = new Row(
                session.Localize(row.GetOrDefault<CUE4Parse.UE4.Objects.Core.i18N.FText>("Lines")),
                row.GetOrDefault<bool>("bRelevantHear"));
        return map;
    }

    /// <summary>Every line of a voice set's table, VoiceID to subtitle.</summary>
    public static Dictionary<int, string> Subtitles(GameSession session, VoiceSet set) =>
        Rows(session, set).ToDictionary(r => r.Key, r => r.Value.Subtitle);

    public static Trigger ByField(string field) => All.FirstOrDefault(t => t.Field == field);

    /// <summary>
    /// Why a line's target names other keys than its trigger has, or null. A target saved
    /// before a trigger's keys changed would otherwise match nothing, or the wrong entry.
    /// </summary>
    public static string KeysDiffer(Trigger t, VoiceLine line)
    {
        var (keys, index) = Condition(line);
        if (t.Shape == Shape.Single || index >= 0 || keys.Count == 0) return null;
        var want = t.Keys ?? [];
        return keys.Keys.OrderBy(k => k).SequenceEqual(want.OrderBy(k => k))
            ? null
            : $"names {string.Join(", ", keys.Keys)}, but {t.Name} entries are keyed by {string.Join(", ", want)}; pick its entry again in the voice-lines window";
    }

    /// <summary>The trigger a line's target belongs to: its first path segment.</summary>
    public static Trigger Of(VoiceLine line) => ByField(line.Target.Split('.', '[')[0]);
}
