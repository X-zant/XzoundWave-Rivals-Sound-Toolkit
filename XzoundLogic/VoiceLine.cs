using System.Text.Json.Serialization;

namespace MRAudioKit;

/// <summary>
/// One new voice line, as a project stores it: which voice set, where in its VoiceItem it
/// goes, what it says, and who hears it. Its audio is NOT here: it is staged like any other
/// sound, as a project replacement keyed by <see cref="MediaId"/>, so the main window's
/// staging, preview and volume work on new lines exactly as on the game's own.
///
/// <see cref="Target"/> is a path into the VoiceItem, read by <see cref="VoiceItemPath"/>:
/// <c>HealthPackVoices.Voices</c> adds one more line to a single list;
/// <c>KillVoices.KillVoices[3].Voices</c> adds a line to an existing entry;
/// <c>KillVoices.KillVoices[+HeroID=1034,SculptID=0].Voices</c> makes a new entry, keyed as
/// given, holding this line. The window builds these from the trigger catalog; the builder
/// just follows them, so no trigger needs code of its own.
/// </summary>
public sealed class VoiceLine
{
    /// <summary><see cref="VoiceSet.Key"/> of the set this line belongs to.</summary>
    public string Set { get; set; } = "";
    public string Target { get; set; } = "";

    /// <summary>
    /// Which of the set's VoiceItems the line goes into, by package (<see cref="VoiceSet.ItemPackages"/>):
    /// a hero with several forms keeps one VoiceItem per form, and the game reads only the one
    /// for the form in play. Null (and every line from before this existed) means the set's first.
    /// </summary>
    public string Item { get; set; }

    public string Subtitle { get; set; } = "";
    public bool HearSelf { get; set; } = true;
    public bool HearAllies { get; set; } = true;
    public bool HearEnemies { get; set; }

    /// <summary>
    /// The row's fourth audience, bRelevantHear: whoever an effect involves (the one a buff
    /// lands on, the teammate a revive is for), whatever team they are on. Null keeps the value
    /// of the line it copies, which is what the game's own lines in the same place do.
    /// </summary>
    public bool? HearRelevant { get; set; }

    /// <summary>The VoiceItem package this line goes into: <see cref="Item"/>, or the set's first.</summary>
    public string ItemIn(VoiceSet set) => Item ?? set.ItemPackages[0];

    /// <summary>
    /// Allocated once, when the line is created, and never changed: it names the line in
    /// the voice table, the event, the bank objects and the staged audio, so a project that
    /// is rebuilt keeps every id it had. Its prefix is the hero (the game drops rows whose
    /// VoiceID/10000 is not the hero) and its thousands digit is the set's sculpt digit.
    /// </summary>
    public int VoiceId { get; set; }

    [JsonIgnore] public string EventName => EventNameOf(VoiceId);
    [JsonIgnore] public uint EventId => Fnv(EventName);
    [JsonIgnore] public uint MediaId => Fnv($"xzoundwave/{EventName}/media");
    [JsonIgnore] public uint SoundId => Fnv($"xzoundwave/{EventName}/sound");
    [JsonIgnore] public uint ActionId => Fnv($"xzoundwave/{EventName}/play");

    /// <summary>
    /// "vo_&lt;VoiceID&gt;_xzw_play": the game's vo_&lt;id&gt;_..._play shape with a marker that says
    /// which events a tool added, which is what an update after a game patch looks for.
    /// </summary>
    public static string EventNameOf(int voiceId) => $"vo_{voiceId}_xzw_play";

    /// <summary>Wwise's id for a name: 32-bit FNV-1 over the lowercased bytes.</summary>
    public static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (var c in s.ToLowerInvariant()) { h *= 16777619; h ^= (byte)c; }
        return h;
    }
}
