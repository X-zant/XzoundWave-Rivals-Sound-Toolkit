using System.IO;
using System.Text.Json;

namespace MRAudioKit;

public sealed class NoteRecord
{
    public uint MediaId { get; set; }
    public string Event { get; set; } = "";
    public string Bank { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Extra { get; set; } = "";

    public bool IsEmpty => string.IsNullOrWhiteSpace(Notes) && string.IsNullOrWhiteSpace(Extra);
}

/// <summary>
/// Free-text notes the user keeps against individual sounds, saved beside the app's
/// settings so they survive restarts. Not scoped to any bank or category -- SFX are
/// the obvious case (a bare event name tells you nothing about what you just heard)
/// but a voice line or an emote can carry notes just the same.
/// </summary>
public sealed class NoteStore
{
    private Dictionary<uint, NoteRecord> _byId = new();

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MRAudioKit");
    private static string File_ => Path.Combine(Dir, "notes.json");

    public int Count => _byId.Count(kv => !kv.Value.IsEmpty);

    public static NoteStore Load()
    {
        var store = new NoteStore();
        try
        {
            if (File.Exists(File_))
            {
                var list = JsonSerializer.Deserialize<List<NoteRecord>>(File.ReadAllText(File_));
                if (list is not null)
                    foreach (var r in list) store._byId[r.MediaId] = r;
            }
        }
        catch { /* a corrupt notes file must not stop the app starting */ }
        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var keep = _byId.Values.Where(r => !r.IsEmpty)
                                   .OrderBy(r => r.MediaId).ToList();
            File.WriteAllText(File_, JsonSerializer.Serialize(keep,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public NoteRecord Get(uint mediaId) => _byId.GetValueOrDefault(mediaId);

    public void Set(SoundRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Notes) && string.IsNullOrWhiteSpace(row.ExtraNotes))
        {
            _byId.Remove(row.MediaId);
            return;
        }
        _byId[row.MediaId] = new NoteRecord
        {
            MediaId = row.MediaId,
            Event = row.Display,
            Bank = row.Bank,
            Notes = row.Notes ?? "",
            Extra = row.ExtraNotes ?? "",
        };
    }

    /// <summary>
    /// Attach saved notes to freshly-built rows, and repair the ones a patch broke.
    ///
    /// Media ids are NOT stable across game updates -- soundKit ships a whole
    /// old-vs-new remapping system because of it. Event names ARE stable. So when a
    /// note's id no longer exists, look for the same event name in the same bank and
    /// move the note onto its new id.
    /// </summary>
    public int Apply(IReadOnlyList<SoundRow> rows)
    {
        var present = rows.Select(r => r.MediaId).ToHashSet();
        var relinked = 0;

        var orphans = _byId.Values.Where(r => !present.Contains(r.MediaId) && !r.IsEmpty).ToList();
        var byEvent = new Dictionary<string, NoteRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in orphans)
            if (!string.IsNullOrWhiteSpace(o.Event)) byEvent[$"{o.Bank}|{o.Event}"] = o;

        foreach (var row in rows)
        {
            var rec = _byId.GetValueOrDefault(row.MediaId);
            if (rec is null && byEvent.TryGetValue($"{row.Bank}|{row.Display}", out var moved))
            {
                _byId.Remove(moved.MediaId);
                moved.MediaId = row.MediaId;
                _byId[row.MediaId] = moved;
                rec = moved;
                relinked++;
            }
            if (rec is null) continue;
            row.Notes = rec.Notes;
            row.ExtraNotes = rec.Extra;
        }

        if (relinked > 0) Save();
        return relinked;
    }
}
