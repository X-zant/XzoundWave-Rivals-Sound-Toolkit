using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MRAudioKit;

/// <summary>One staged replacement, as stored in a project.</summary>
public sealed class ProjectReplacement
{
    public uint MediaId { get; set; }
    /// <summary>
    /// Relative to the project file when possible, so a project folder can move --
    /// and spelled with forward slashes, so it can move between platforms as well.
    /// See <see cref="Project.ToStoredPath"/>; older files carrying backslashes are
    /// still resolved.
    /// </summary>
    public string File { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>
    /// Where the sound was when this was last saved: the game's bank (its path) and the Sound
    /// object in it that played <see cref="MediaId"/>. A game update renumbers media far more
    /// than Sounds (408 of 450 media ids changed in one voice bank across a patch, while 956 of
    /// 967 Sounds kept theirs), and hands old media ids to other lines, so on opening the file
    /// follows its Sound to whatever medium that Sound plays now. Absent from older projects.
    /// </summary>
    public string Bank { get; set; }
    public uint? Sound { get; set; }
}

/// <summary>
/// A mod that was open when the project was saved: a .pak, .utoc, .bnk or unpacked folder,
/// and the .bnk files taken from it. A build sits on top of the mods that are open, so they
/// are part of the work as much as the staged files are.
/// </summary>
public sealed class ProjectMod
{
    /// <summary>Stored the way a staged file is (<see cref="Project.ToStoredPath"/>).</summary>
    public string File { get; set; } = "";

    /// <summary>
    /// The .bnk files taken from it, by name inside it. Empty for a mod of loose Media wems
    /// only. Reopened as these exactly, without asking again which banks to take.
    /// </summary>
    public List<string> Banks { get; set; } = [];
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
    public const string Extension = ".xzw";

    /// <summary>
    /// What projects were called before the tool was renamed. Still opened, and still
    /// listed in the Open dialog, because someone's work does not stop being theirs
    /// because the extension changed. Saving writes <see cref="Extension"/>.
    /// </summary>
    public const string LegacyExtension = ".mrak";

    /// <summary>Open-dialog filter accepting both, newest first.</summary>
    public const string OpenFilter =
        "XzoundWave project (*" + Extension + ";*" + LegacyExtension + ")|*"
        + Extension + ";*" + LegacyExtension
        + "|XzoundWave project (*" + Extension + ")|*" + Extension
        + "|All files|*.*";

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
    /// New voice lines (the voice-lines window). Their audio is NOT here: it is staged like
    /// any sound, as a <see cref="Replacements"/> entry keyed by the line's media id, so a
    /// project that adds lines is opened, staged and saved the way every other one is.
    /// Absent from older files, which simply load with none.
    /// </summary>
    public List<VoiceLine> VoiceLines { get; set; } = [];

    /// <summary>
    /// Sounds added beside what an event already plays (right-click › Add a sound to this
    /// event…). Their audio is staged like a new line's, as a <see cref="Replacements"/> entry
    /// keyed by the layer's own media id. Absent from older files, which load with none.
    /// </summary>
    public List<ProjectLayer> Layers { get; set; } = [];

    /// <summary>
    /// Level applied to every sound that has not been set by hand. Kept on the project
    /// rather than per sound so "make the whole mod quieter" is one number, and so the
    /// export can be labelled with it.
    /// </summary>
    public double BulkVolume { get; set; } = 1.0;

    /// <summary>
    /// The mods open when the project was saved, in the order they were opened: the one opened
    /// last wins a .bnk or a loose file two of them ship, as it did then. Null in a project
    /// written before mods were saved, which leaves whatever is open alone; empty means none
    /// were open, and opening the project closes any that are.
    /// </summary>
    public List<ProjectMod> Mods { get; set; }

    /// <summary>Where this was loaded from / will save to. Null for an unsaved project.</summary>
    [JsonIgnore] public string Path { get; private set; }
    [JsonIgnore] public bool IsDirty { get; private set; }
    [JsonIgnore] public bool IsSaved => !string.IsNullOrEmpty(Path);
    [JsonIgnore] public string Title =>
        (string.IsNullOrWhiteSpace(Name) ? "Untitled" : Name) + (IsDirty ? " *" : "");

    public void Touch() { IsDirty = true; Changes++; }

    /// <summary>How many times it was touched: a long load compares it to tell whether it was edited meanwhile.</summary>
    [JsonIgnore] public int Changes { get; private set; }

    /// <summary>After a load has re-staged everything: the window's work so far IS the file.</summary>
    public void MarkClean() => IsDirty = false;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A file for people as well as for the tool: a subtitle in Japanese or a target with
        // a "+" stays as written instead of turning into \u escapes. The escaping guards
        // JSON pasted into HTML, which this never is; reading accepts either form.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
        var to = path ?? Path ?? throw new InvalidOperationException("no path to save to");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(to))!);
        File.WriteAllText(to, JsonSerializer.Serialize(this, Json),
                          new System.Text.UTF8Encoding(false));
        // Only once it is written: a save that failed must not leave the project pointing at a
        // file it could not write, or the next plain Save would aim there again without asking.
        Path = to;
        IsDirty = false;
    }

    // ---- staged files -------------------------------------------------------

    /// <summary>
    /// Store a path relative to the project when it is underneath it, absolute
    /// otherwise. Keeps "project + wems in one folder" portable without breaking
    /// people who keep their audio somewhere else entirely.
    ///
    /// The relative form is always spelled with forward slashes, whatever the host
    /// writing it uses. Writing the host's own separator makes the folder portable in
    /// one direction only: Windows reads "audio/x.wem" as a folder and a file, but a
    /// backslash is an ordinary character in a name everywhere else, so "audio\x.wem"
    /// resolves there to one file that has never existed and the staged clip is
    /// reported missing. A forward slash costs nothing on Windows and is the only
    /// spelling both hosts agree on.
    ///
    /// An absolute path is handed back exactly as this machine spells it, because a
    /// path outside the project folder was never going to mean anything on another.
    /// </summary>
    public string ToStoredPath(string fullPath)
    {
        if (!IsSaved) return fullPath;
        try
        {
            var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (root is null) return fullPath;
            var rel = System.IO.Path.GetRelativePath(root, fullPath);
            // ".." is a path component, not text, so it is matched literally: the
            // machine's collation decides on its own what punctuation is worth
            // comparing, and a wrong answer here stores a path that climbs out of the
            // project folder as though it were inside it.
            return rel.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(rel)
                ? fullPath : rel.Replace('\\', '/');
        }
        catch { return fullPath; }
    }

    /// <summary>
    /// Resolve a stored path against the project folder, accepting either separator.
    ///
    /// Projects saved from here on carry forward slashes, but one written by an older
    /// Windows build carries backslashes and has to keep opening -- someone's staged
    /// files do not stop being staged because the spelling changed. Translating before
    /// the combine is what makes that work: on Windows both characters already separate
    /// directories, and elsewhere the backslashes must go or Path.Combine yields a
    /// single name with them still inside it.
    /// </summary>
    public string ToFullPath(string stored)
    {
        if (string.IsNullOrEmpty(stored) || System.IO.Path.IsPathRooted(stored)) return stored;
        if (!IsSaved) return stored;
        var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        return root is null ? stored
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(root, HostSpelling(stored)));
    }

    /// <summary>
    /// A stored relative path in this host's own spelling.
    ///
    /// Both characters are read as separators because a project file can carry either,
    /// and the reading only goes wrong for a file whose own name contains a backslash
    /// on a host that allows it -- which no project this tool writes can name, since a
    /// backslash never reaches the stored form as anything but a separator.
    /// </summary>
    private static string HostSpelling(string stored) =>
        stored.Replace('\\', System.IO.Path.DirectorySeparatorChar)
              .Replace('/', System.IO.Path.DirectorySeparatorChar);

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
    ///
    /// A record moves only when that is not a guess: it is the one record for its bank and
    /// event, and that event now plays exactly one media id the project has no record for.
    /// An event of a random container plays several, and moving one note onto all of them (or
    /// onto whichever came last, as this once did) silences or levels sounds nobody touched.
    /// </summary>
    /// <param name="gone">
    /// When given, a record moves only if this says its media id is no longer in the game at
    /// all. Without it, a record whose media the rows do not hold counts as moved, which is
    /// also true of a sound in another bank, or of a mod's bank playing an event from another
    /// id than the game's copy.
    /// </param>
    /// <param name="mayTake">rows an orphaned note may move onto; null for all</param>
    public int ApplyNotes(IReadOnlyList<SoundRow> rows, Func<uint, bool> gone = null, Func<SoundRow, bool> mayTake = null)
    {
        static string Key(string bank, string evt) => $"{bank}|{evt}";
        var present = rows.Select(r => r.MediaId).ToHashSet();
        var orphans = Notes.Where(n => !present.Contains(n.MediaId) && !n.IsEmpty &&
                                       !string.IsNullOrWhiteSpace(n.Event) && (gone is null || gone(n.MediaId)))
                           .GroupBy(n => Key(n.Bank, n.Event), StringComparer.OrdinalIgnoreCase)
                           .Where(g => g.Count() == 1)
                           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var unclaimed = rows.Where(r => FindNote(r.MediaId) is null && (mayTake is null || mayTake(r)))
                            .GroupBy(r => Key(r.Bank, r.Display), StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.Select(r => r.MediaId).Distinct().ToList(),
                                          StringComparer.OrdinalIgnoreCase);

        // Two events can play one clip, so two records could both claim the same new id: the
        // first keeps it and the other stays where it was rather than becoming a second record
        // for one media id, which FindNote would never see.
        var relinked = 0;
        var claimed = new HashSet<uint>();
        foreach (var (key, moved) in orphans)
            if (unclaimed.TryGetValue(key, out var ids) && ids.Count == 1 && claimed.Add(ids[0]))
            {
                moved.MediaId = ids[0];
                relinked++;
            }

        foreach (var row in rows)
        {
            var rec = FindNote(row.MediaId);
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
