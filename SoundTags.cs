namespace MRAudioKit;

/// <summary>One tag in the shared vocabulary.</summary>
public sealed record TagDef(string Code, string Group, string Meaning);

/// <summary>
/// A fixed vocabulary for describing what a sound slot can be used for, and what the
/// game does to whatever you put in it.
///
/// The point is uniformity. Notes like "This can be used for Ult music!", "Ult file
/// here" and "This one is for music" all mean the same thing and none of them can be
/// searched, sorted or shared, so the tags are picked from a menu and never typed.
/// Anything the vocabulary cannot say still belongs in the free-text Notes column --
/// that column exists precisely so this list does not have to grow without limit.
///
/// Tags are short and combine rather than compound: instead of one long
/// "[ULT - Music Compatible]" tag, a slot carries [ULT] and [MUSIC] and [LONG], and
/// each of those three means something on its own and can be searched for on its own.
/// </summary>
public static class SoundTags
{
    public const string SlotGroup = "What it is for";
    public const string BehaviourGroup = "What the game does to it";

    /// <summary>
    /// The vocabulary, in menu order. Codes are upper-case and never contain a space,
    /// so a tag list survives a CSV, a filename and a Discord message unharmed.
    /// </summary>
    public static readonly IReadOnlyList<TagDef> All =
    [
        new("ULT",     SlotGroup, "Ultimate — the callout or the music that plays on an ult"),
        new("MVP",     SlotGroup, "MVP, POTG and end-of-match"),
        new("INTRO",   SlotGroup, "Match intro, pre-round, gate opening"),
        new("SELECT",  SlotGroup, "Character select, lobby, gallery"),
        new("EMOTE",   SlotGroup, "Emotes and sprays"),
        new("ABILITY", SlotGroup, "A normal ability or attack, not an ult"),
        new("KO",      SlotGroup, "Taking someone down"),
        new("DEATH",   SlotGroup, "Your own death"),
        new("HURT",    SlotGroup, "Taking damage"),
        new("SPAWN",   SlotGroup, "Respawn and re-entry"),
        new("UI",      SlotGroup, "Menus, buttons, store, notifications"),
        new("MUSIC",   SlotGroup, "A music slot — takes a track rather than a one-shot"),
        new("AMB",     SlotGroup, "Ambience and background loops"),
        new("FOOT",    SlotGroup, "Footsteps and movement"),

        new("LOOP",    BehaviourGroup, "Loops until something stops it"),
        new("LONG",    BehaviourGroup, "Plays a long file to the end — good for music"),
        new("CUT",     BehaviourGroup, "Gets cut short, so only the first moment is heard"),
        new("NOSTOP",  BehaviourGroup, "Cannot be interrupted once it starts"),
        new("PITCH",   BehaviourGroup, "The game shifts its pitch or tempo"),
        new("VERB",    BehaviourGroup, "The game adds reverb or another effect"),
        new("3D",      BehaviourGroup, "Positional — quieter with distance"),
        new("RAND",    BehaviourGroup, "One of a random pool, so it may not play every time"),
        new("DUCK",    BehaviourGroup, "Ducks the rest of the mix while it plays"),
    ];

    private static readonly Dictionary<string, TagDef> ByCode =
        All.ToDictionary(t => t.Code, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, int> Order =
        All.Select((t, i) => (t.Code, i)).ToDictionary(x => x.Code, x => x.i, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string code) => code is not null && ByCode.ContainsKey(code);
    public static TagDef Find(string code) =>
        code is not null && ByCode.TryGetValue(code, out var d) ? d : null;
    public static string MeaningOf(string code) => Find(code)?.Meaning ?? "";

    /// <summary>
    /// Clean a set of tags: upper-case, no duplicates, known tags first in vocabulary
    /// order and anything unrecognised kept after them.
    ///
    /// Unknown codes are deliberately preserved rather than dropped. A project written
    /// by a newer build may carry tags this one has never heard of, and silently
    /// deleting someone else's work on open would be far worse than showing a tag
    /// whose meaning we cannot explain.
    /// </summary>
    public static List<string> Canonical(IEnumerable<string> tags)
    {
        if (tags is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = new List<string>();
        foreach (var raw in tags)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var code = raw.Trim().Trim('[', ']').Trim().ToUpperInvariant();
            if (code.Length == 0 || !seen.Add(code)) continue;
            clean.Add(Find(code)?.Code ?? code);
        }
        return [.. clean.OrderBy(c => Order.TryGetValue(c, out var i) ? i : int.MaxValue)
                        .ThenBy(c => c, StringComparer.Ordinal)];
    }

    /// <summary>Read tags back out of text: brackets, commas and spaces all separate.</summary>
    public static List<string> Parse(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : Canonical(text.Split(['[', ']', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>How a tag list is shown and written: <c>[ULT][MUSIC]</c>.</summary>
    public static string Format(IEnumerable<string> tags) =>
        tags is null ? "" : string.Concat(Canonical(tags).Select(t => $"[{t}]"));

    /// <summary>Space-separated, for a CSV cell or a log line.</summary>
    public static string Flat(IEnumerable<string> tags) =>
        tags is null ? "" : string.Join(' ', Canonical(tags));

    /// <summary>
    /// Does this tag list answer a filter query? Matches whole tags only, so typing
    /// "ult" finds [ULT] without also dragging in every row tagged [RESULT]-ish by
    /// some future vocabulary, and brackets in the query are ignored.
    /// </summary>
    public static bool Matches(IEnumerable<string> tags, string query)
    {
        if (tags is null || string.IsNullOrWhiteSpace(query)) return false;
        var want = query.Trim().Trim('[', ']').Trim();
        if (want.Length == 0) return false;
        foreach (var t in tags)
            if (t is not null && t.Equals(want, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
