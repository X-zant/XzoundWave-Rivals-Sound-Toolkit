using System.IO;

namespace MRAudioKit;

/// <summary>
/// The mods currently loaded, as an overlay on the game's own .bnk files.
///
/// Keyed by .bnk stem, because that is the unit the game loads and the unit a mod
/// replaces: two mods that both touch <c>bnk_vo_1029001</c> are competing for the same
/// slot, and only one of them can win. The newest one added does, which matches how the
/// game itself resolves a collision — last pak read wins — and how people expect a
/// stack of mods to behave.
///
/// Everything a loaded .bnk contains wins over the game's copy of that .bnk: its rows,
/// its audio for preview, its bytes for extraction. Vanilla is what shows through for
/// every .bnk no mod has claimed.
/// </summary>
public sealed class ModSet
{
    /// <param name="Changed">How many sounds this .bnk alters from the shipped one.</param>
    public sealed record Entry(string Stem, string BankName, string Source,
                               ModBank.Bank Bank, SkinSounds Sounds, int Changed)
    {
        /// <summary>
        /// The mod's file name, however the path was spelled.
        ///
        /// Path.GetFileName only knows the host's own separator, so a path carrying
        /// backslashes hands back the whole thing on Linux -- and these paths are not
        /// always this machine's: a project file written on Windows and opened
        /// elsewhere brings its paths with it, and the name is what a person reads
        /// when one mod displaces another.
        /// </summary>
        public string SourceName => Source is null ? null
            : Source[(Source.LastIndexOfAny(['/', '\\']) + 1)..];
    }

    /// <summary>
    /// A loose media file a mod ships under WwiseAudio/Media/: the audio of a sound no bank
    /// holds, which the game reads by its path alone. It is not a .bnk and claims no .bnk's
    /// slot, so it is kept apart from <see cref="All"/>: two mods that replace two different
    /// files streamed by one bank both apply in game, and one bank entry per stem would make
    /// them displace each other.
    /// </summary>
    /// <param name="Language">Its language folder, or null for the Media/ folder itself.</param>
    /// <param name="Tag">
    /// "MODDED" when the game ships different bytes at this path, "NEW" when it ships nothing
    /// there, "" when it ships these very bytes.
    /// </param>
    /// <param name="Stem">The bank that plays it, by stem, or null when nothing in the game does.</param>
    public sealed record Loose(uint Id, string Language, string MountedAt, byte[] Bytes,
                               string Source, string Tag, string Stem)
    {
        /// <summary>
        /// The game reads this file for its sound: it sits where the game ships the id, or, for
        /// an id a sound streams and the game ships no file for, at the id's own path. False for
        /// a file nothing reads -- the id ships at another path, a bank holds it whole, or the
        /// folder is not the id's -- which changes nothing in a match.
        /// </summary>
        public bool Reaches { get; init; } = true;

        /// <summary>
        /// For a file in the neutral Media/ folder: the languages the game ships this id in a
        /// folder of their own, where that copy is read instead of this one.
        /// </summary>
        public IReadOnlyCollection<string> ShadowedIn { get; init; } = [];

        public bool Changed => Tag is "MODDED" or "NEW";

        /// <summary>Changes what a bank of this language plays.</summary>
        public bool AppliesIn(string language) =>
            Changed && Reaches &&
            (Language is null
                ? language is null || !ShadowedIn.Contains(language, StringComparer.OrdinalIgnoreCase)
                : Language.Equals(language, StringComparison.OrdinalIgnoreCase));

        public string SourceName => Source is null ? null : Source[(Source.LastIndexOfAny(['/', '\\']) + 1)..];
    }

    private readonly Dictionary<string, Entry> _byStem = new(StringComparer.OrdinalIgnoreCase);
    // Every loaded loose file by its path, and the ones the game reads by language and id. Two
    // files of one id can sit at two paths -- one where the game reads it, one where it does not
    // -- and keying the set by id alone let whichever was read last stand for both.
    private readonly Dictionary<string, Loose> _loose = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Loose> _reaching = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loaded .bnk files. The loose files are <see cref="LooseFiles"/>.</summary>
    public int Count => _byStem.Count;
    public IReadOnlyCollection<Entry> All => _byStem.Values;
    public IReadOnlyCollection<Loose> LooseFiles => _loose.Values;
    public bool Any => _byStem.Count > 0 || _loose.Count > 0;

    public Entry Get(string stem) =>
        stem is not null && _byStem.TryGetValue(stem, out var e) ? e : null;

    public bool Has(string stem) => Get(stem) is not null;

    /// <summary>
    /// Load one .bnk, reading it against the game so its rows carry MODDED / NEW.
    /// </summary>
    /// <param name="replaced">
    /// The entry this one displaced, when another mod had already claimed the same
    /// .bnk. Worth telling the author about: they asked for both and only get one.
    /// </param>
    public Entry Add(GameSession session, ModBank.Bank bank, string source, out Entry replaced)
    {
        var stem = Path.GetFileNameWithoutExtension(bank.Name);
        _byStem.TryGetValue(stem, out replaced);

        var sounds = ModBank.ToSounds(session, bank);
        var changed = sounds.Rows.Count(r => r.ModTag is "MODDED" or "NEW");
        var entry = new Entry(stem, bank.Name, source, bank, sounds, changed);
        _byStem[stem] = entry;
        return entry;
    }

    public bool Remove(string stem) => stem is not null && _byStem.Remove(stem);

    public void Clear() { _byStem.Clear(); _loose.Clear(); _reaching.Clear(); }

    private static string PathKey(string mountedAt) => mountedAt.Replace('\\', '/').TrimStart('/');

    private static string LooseKey(string language, uint id) => $"{language}|{id}";

    /// <summary>
    /// Read one loose Media file against the game: what the game ships at the same path, which
    /// bank plays it, and whether the game reads it at all. Null for a file whose name is not a
    /// media id: nothing could say which sound it is. Touches no set, so it can run off the
    /// drawing thread while the window reads the set; <see cref="PutLoose"/> commits it.
    /// </summary>
    /// <remarks>Finding the bank that plays it can read every bank once.</remarks>
    public static Loose ReadLoose(GameSession session, ModBank.LooseMedia file, string source)
    {
        if (ModUpdate.MediaIdOf(file.MountedAt) is not { } id) return null;
        var language = GameSession.LanguageOf(file.MountedAt);

        byte[] game = null;
        try { game = session?.GameFileAt(file.MountedAt)?.Read(); }
        catch { /* compared as absent: the row then says NEW rather than nothing */ }
        var tag = game is null ? "NEW" : game.AsSpan().SequenceEqual(file.Bytes) ? "" : "MODDED";

        string stem = null;
        try { stem = session?.PlayingBank(id) is { } bank ? Path.GetFileNameWithoutExtension(bank) : null; }
        catch { }

        // Where the game would read it. At a path it ships, it does (a neutral file can still be
        // shadowed by a language's own copy, below). Elsewhere only for an id it ships nowhere
        // and no bank holds whole, which a sound streams from the id's own path.
        bool reaches;
        if (game is not null) reaches = true;
        else if (session?.LooseMedia(id) is not null) reaches = false;
        else if (session?.MediaBank.ContainsKey(id) == true) reaches = false;
        else reaches = stem is not null && file.MountedAt.Equals(MediaLayout.PathFor(id, language), StringComparison.OrdinalIgnoreCase);

        var shadowed = language is null && session is not null
            ? GameSession.Languages.Where(l => GameSession.LanguageOf(session.LooseMedia(id, l)?.Path) is not null).ToList()
            : [];
        return new Loose(id, language, file.MountedAt, file.Bytes, source, tag, stem) { Reaches = reaches, ShadowedIn = shadowed };
    }

    /// <summary>Put a loose file in the set, winning over any another mod put at the same path.</summary>
    /// <param name="replaced">The file it displaced, or null.</param>
    public Loose PutLoose(Loose loose, out Loose replaced)
    {
        var path = PathKey(loose.MountedAt);
        _loose.TryGetValue(path, out replaced);
        _loose[path] = loose;
        if (loose.Reaches) _reaching[LooseKey(loose.Language, loose.Id)] = loose;
        return loose;
    }

    /// <summary>Read and put in one step, for a caller already off the drawing thread with the set to itself.</summary>
    public Loose AddLoose(GameSession session, ModBank.LooseMedia file, string source, out Loose replaced)
    {
        replaced = null;
        return ReadLoose(session, file, source) is { } loose ? PutLoose(loose, out replaced) : null;
    }

    /// <summary>
    /// The loaded loose file a bank of this language plays for a medium: its own language
    /// folder's, else the neutral one unless the game ships that language its own copy, never
    /// another language's -- the order the game looks. Only a file the game reads.
    /// </summary>
    public Loose LooseFor(uint id, string language)
    {
        if (language is not null && _reaching.TryGetValue(LooseKey(language, id), out var own)) return own;
        return _reaching.TryGetValue(LooseKey(null, id), out var neutral) &&
               (language is null || !neutral.ShadowedIn.Contains(language, StringComparer.OrdinalIgnoreCase))
            ? neutral : null;
    }

    /// <summary>The changed media ids of loose files per bank stem, as a bank of this language plays them.</summary>
    public Dictionary<string, HashSet<uint>> LooseChangedByStem(string language) =>
        _loose.Values.Where(l => l.AppliesIn(language) && l.Stem is not null)
              .GroupBy(l => l.Stem, StringComparer.OrdinalIgnoreCase)
              .ToDictionary(g => g.Key, g => g.Select(l => l.Id).ToHashSet(), StringComparer.OrdinalIgnoreCase);

    /// <summary>A copy of the loose files alone, for a build that reads them on other threads.</summary>
    public ModSet LooseSnapshot()
    {
        var copy = new ModSet();
        foreach (var (key, loose) in _loose) copy._loose[key] = loose;
        foreach (var (key, loose) in _reaching) copy._reaching[key] = loose;
        return copy;
    }

    /// <summary>Sounds changed across every loaded .bnk belonging to one skin.</summary>
    public int ChangedIn(SkinEntry skin) =>
        skin is null ? 0
        : skin.Banks.Sum(b => Get(Path.GetFileNameWithoutExtension(b.Path))?.Changed ?? 0);

    /// <summary>Which of a skin's .bnk files a mod has claimed.</summary>
    public IEnumerable<string> LoadedFor(SkinEntry skin) =>
        skin is null ? []
        : skin.Banks.Select(b => Path.GetFileNameWithoutExtension(b.Path)).Where(Has);

    public int TotalChanged => _byStem.Values.Sum(e => e.Changed) + _loose.Values.Count(l => l.Changed);

    /// <summary>The distinct mods these .bnk and loose files came from, for a status line.</summary>
    public IEnumerable<string> Sources =>
        _byStem.Values.Select(e => e.SourceName).Concat(_loose.Values.Select(l => l.SourceName))
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
}
