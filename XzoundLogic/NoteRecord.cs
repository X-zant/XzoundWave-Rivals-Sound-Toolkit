namespace MRAudioKit;

/// <summary>
/// A free-text note against one sound. These live inside a <see cref="Project"/>
/// rather than in one global pile, because a remark like "too loud, drop 3 dB"
/// belongs to the mod it was written for.
///
/// The event name and bank are stored alongside the media id because media ids
/// change between game updates and event names do not — that is what lets a note
/// survive a patch.
/// </summary>
public sealed class NoteRecord
{
    public uint MediaId { get; set; }
    public string Event { get; set; } = "";
    public string Bank { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Extra { get; set; } = "";

    /// <summary>Tags from the shared vocabulary, stored as bare codes.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// The number this sound was given by the last numbered test bank built in this
    /// project. Kept separate from the free-text fields so rebuilding a test bank
    /// never overwrites something the author typed.
    /// </summary>
    public int? TestNumber { get; set; }

    /// <summary>Silenced in test builds, so quieter sounds under it can be heard.</summary>
    public bool Muted { get; set; }

    /// <summary>Loudness multiplier, and whether it was set by hand.</summary>
    public double? Volume { get; set; }
    public bool VolumeByHand { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Notes) && string.IsNullOrWhiteSpace(Extra)
                           && TestNumber is null && !Muted && Volume is null
                           && (Tags is null || Tags.Count == 0);
}
