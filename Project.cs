using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MRAudioKit;

/// <summary>One staged replacement, as stored in a project.</summary>
public sealed class ProjectReplacement
{
    public uint MediaId { get; set; }
    /// <summary>Relative to the project file when possible, so a project folder can move.</summary>
    public string File { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>
/// A saved piece of work: what you were editing, which custom files you staged, and
/// the notes you took. Notes live here rather than in one global pile because a note
/// like "too loud, drop 3 dB" belongs to a mod, not to the tool.
///
/// The file is plain JSON and is meant to sit next to the mod it describes -- keep it
/// with your .wem files and the whole folder is portable.
/// </summary>
public sealed class Project
{
    public const int CurrentFormatVersion = 1;
    public const string Extension = ".mrak";

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "Untitled";
    public string Character { get; set; } = "";
    public string Skin { get; set; } = "";
    public string Bank { get; set; } = "";
    public string Language { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public List<ProjectReplacement> Replacements { get; set; } = [];
    public List<NoteRecord> Notes { get; set; } = [];

    /// <summary>
    /// Level applied to every sound that has not been set by hand. Kept on the project
    /// rather than per sound so "make the whole mod quieter" is one number, and so the
    /// export can be labelled with it.
    /// </summary>
    public double BulkVolume { get; set; } = 1.0;

    /// <summary>Where this was loaded from / will save to. Null for an unsaved project.</summary>
    [JsonIgnore] public string Path { get; private set; }
    [JsonIgnore] public bool IsDirty { get; private set; }
    [JsonIgnore] public bool IsSaved => !string.IsNullOrEmpty(Path);
    [JsonIgnore] public string Title =>
        (string.IsNullOrWhiteSpace(Name) ? "Untitled" : Name) + (IsDirty ? " *" : "");

    public void Touch() => IsDirty = true;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Project New() => new();

    public static Project Load(string path)
    {
        var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("not a project file");
        if (p.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $"This project was written by a newer version (format {p.FormatVersion}).");
        p.Path = path;
        p.IsDirty = false;
        return p;
    }

    public void Save(string path = null)
    {
        Path = path ?? Path ?? throw new InvalidOperationException("no path to save to");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Json),
                          new System.Text.UTF8Encoding(false));
        IsDirty = false;
    }

    // ---- staged files -------------------------------------------------------

    /// <summary>
    /// Store a path relative to the project when it is underneath it, absolute
    /// otherwise. Keeps "project + wems in one folder" portable without breaking
    /// people who keep their audio somewhere else entirely.
    /// </summary>
    public string ToStoredPath(string fullPath)
    {
        if (!IsSaved) return fullPath;
        try
        {
            var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (root is null) return fullPath;
            var rel = System.IO.Path.GetRelativePath(root, fullPath);
            return rel.StartsWith("..") || System.IO.Path.IsPathRooted(rel) ? fullPath : rel;
        }
        catch { return fullPath; }
    }

    public string ToFullPath(string stored)
    {
        if (string.IsNullOrEmpty(stored) || System.IO.Path.IsPathRooted(stored)) return stored;
        if (!IsSaved) return stored;
        var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        return root is null ? stored : System.IO.Path.GetFullPath(System.IO.Path.Combine(root, stored));
    }

    /// <summary>
    /// Record which number a numbered test bank gave each sound, so that hearing "42"
    /// in game leads straight back to the row it came from. Existing notes are left
    /// alone; only the number is written.
    /// </summary>
    public int RecordTestNumbers(IEnumerable<(int Number, uint MediaId)> assignments,
                                 IReadOnlyList<SoundRow> rows)
    {
        var byId = rows.GroupBy(r => r.MediaId).ToDictionary(g => g.Key, g => g.ToList());
        var n = 0;
        foreach (var (number, mediaId) in assignments)
        {
            if (!byId.TryGetValue(mediaId, out var matches)) continue;
            foreach (var row in matches) row.TestNumber = number;
            var rec = FindNote(mediaId);
            if (rec is null)
            {
                rec = new NoteRecord
                {
                    MediaId = mediaId,
                    Event = matches[0].Display,
                    Bank = matches[0].Bank,
                };
                Notes.Add(rec);
            }
            rec.TestNumber = number;
            n++;
        }
        if (n > 0) Touch();
        return n;
    }

    // ---- notes --------------------------------------------------------------

    public NoteRecord FindNote(uint mediaId) => Notes.FirstOrDefault(n => n.MediaId == mediaId);

    public void SetNote(SoundRow row)
    {
        Notes.RemoveAll(n => n.MediaId == row.MediaId);
        if (!string.IsNullOrWhiteSpace(row.Notes) || !string.IsNullOrWhiteSpace(row.ExtraNotes)
            || row.TestNumber is not null || row.Muted || row.Volume is not null
            || row.Tags is { Count: > 0 })
            Notes.Add(new NoteRecord
            {
                MediaId = row.MediaId,
                Event = row.Display,
                Bank = row.Bank,
                Notes = row.Notes ?? "",
                Extra = row.ExtraNotes ?? "",
                Tags = SoundTags.Canonical(row.Tags),
                TestNumber = row.TestNumber,
                Muted = row.Muted,
                Volume = row.Volume,
                VolumeByHand = row.VolumeByHand,
            });
        Touch();
    }

    /// <summary>
    /// Attach notes to freshly-built rows, moving any whose media id a game update
    /// changed onto its new id by matching the event name within the same bank.
    /// Returns how many were relinked.
    /// </summary>
    public int ApplyNotes(IReadOnlyList<SoundRow> rows)
    {
        var present = rows.Select(r => r.MediaId).ToHashSet();
        var orphans = Notes.Where(n => !present.Contains(n.MediaId) && !n.IsEmpty).ToList();
        var byEvent = new Dictionary<string, NoteRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in orphans)
            if (!string.IsNullOrWhiteSpace(o.Event)) byEvent[$"{o.Bank}|{o.Event}"] = o;

        var relinked = 0;
        foreach (var row in rows)
        {
            var rec = FindNote(row.MediaId);
            if (rec is null && byEvent.TryGetValue($"{row.Bank}|{row.Display}", out var moved))
            {
                moved.MediaId = row.MediaId;
                rec = moved;
                relinked++;
            }
            if (rec is null) continue;
            row.Notes = rec.Notes;
            row.ExtraNotes = rec.Extra;
            row.Tags = SoundTags.Canonical(rec.Tags);
            row.TestNumber = rec.TestNumber;
            row.Muted = rec.Muted;
            row.Volume = rec.Volume;
            row.VolumeByHand = rec.VolumeByHand;
        }
        if (relinked > 0) Touch();
        return relinked;
    }
}
