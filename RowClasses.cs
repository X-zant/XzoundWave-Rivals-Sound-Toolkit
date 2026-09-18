namespace MRAudioKit;

/// <summary>A kind of row that can be turned off in the list.</summary>
/// <param name="Key">Stored in settings. Never change one: an old settings file names them.</param>
/// <param name="Label">What the checkbox says.</param>
/// <param name="Meaning">Why you would turn it off.</param>
/// <param name="Matches">True when a row belongs to this kind.</param>
public sealed record RowClass(string Key, string Label, string Meaning, Func<SoundRow, bool> Matches);

/// <summary>
/// Which rows the list shows. A character bank runs to several hundred sounds and
/// most of a session is spent looking at a handful of them, so being able to drop
/// whole kinds of row -- everything already tagged, everything a mod left alone --
/// is worth more than another filter box.
///
/// One table drives both the settings panel and the filter, so a kind cannot appear
/// in the panel without actually being applied, or be applied without being listed.
/// </summary>
public static class RowClasses
{
    public static readonly IReadOnlyList<RowClass> All =
    [
        new("unnamed", "Unnamed events",
            "Sounds the bank references by hash, with no name shipped anywhere.",
            r => r.EventName is null),

        new("muted", "Muted sounds",
            "The ones you have silenced for the next test build.",
            r => r.Muted),

        new("staged", "Sounds with a file staged",
            "Where you have already dropped in custom audio.",
            r => r.ReplacementPath is not null),

        new("modded", "Changed entries, in an opened mod",
            "What a bank or pak you opened actually replaced.",
            r => r.ModTag is "MODDED" or "NEW"),

        // Deliberately matches "" and not null. The diff writes an empty tag for an
        // entry a mod left alone; a row from the game's own banks has no tag at all,
        // and hiding those would empty the list during normal browsing.
        new("unchanged", "Untouched entries, in an opened mod",
            "What the mod left exactly as the game shipped it.",
            r => r.ModTag == ""),

        new("streamed", "Streamed sources",
            "Sounds the bank streams from a loose file rather than embedding.",
            r => r.Streamed),

        new("nonvorbis", "Non-Vorbis codecs",
            "PCM and anything else that is not Vorbis.",
            r => !string.IsNullOrEmpty(r.Codec) &&
                 !r.Codec.Equals("VORBIS", StringComparison.OrdinalIgnoreCase)),

        new("nosub", "Lines with no subtitle",
            "Useful on a voice bank, where a line without text is rarely the one you want.",
            r => string.IsNullOrWhiteSpace(r.Subtitle)),

        new("tagged", "Tagged sounds",
            "Anything already carrying a tag.",
            r => r.Tags is { Count: > 0 }),

        new("untagged", "Untagged sounds",
            "Leaves only what you have already identified.",
            r => r.Tags is null || r.Tags.Count == 0),
    ];

    /// <summary>
    /// Off on a first run, matching the "show unnamed" box this replaces. Null in
    /// settings means "never chosen", which is why this is a separate list rather
    /// than a default on each entry.
    /// </summary>
    public static readonly string[] HiddenByDefault = ["unnamed"];

    public static RowClass Find(string key) =>
        All.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Drop every row belonging to a hidden kind. Kinds combine by removal, so turning
    /// off both "tagged" and "untagged" leaves nothing -- which is honest, and visible
    /// immediately in the counts.
    /// </summary>
    public static IEnumerable<SoundRow> Apply(IEnumerable<SoundRow> rows, IEnumerable<string> hidden)
    {
        if (hidden is null) return rows;
        foreach (var key in hidden)
        {
            var cls = Find(key);
            if (cls is null) continue;          // a kind from a newer build: ignore, do not throw
            var match = cls.Matches;
            rows = rows.Where(r => !match(r));
        }
        return rows;
    }
}
