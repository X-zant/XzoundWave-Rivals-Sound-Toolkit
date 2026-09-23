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
        public string SourceName => Path.GetFileName(Source);
    }

    private readonly Dictionary<string, Entry> _byStem = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byStem.Count;
    public IReadOnlyCollection<Entry> All => _byStem.Values;
    public bool Any => _byStem.Count > 0;

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

    public void Clear() => _byStem.Clear();

    /// <summary>Sounds changed across every loaded .bnk belonging to one skin.</summary>
    public int ChangedIn(SkinEntry skin) =>
        skin is null ? 0
        : skin.Banks.Sum(b => Get(Path.GetFileNameWithoutExtension(b.Path))?.Changed ?? 0);

    /// <summary>Which of a skin's .bnk files a mod has claimed.</summary>
    public IEnumerable<string> LoadedFor(SkinEntry skin) =>
        skin is null ? []
        : skin.Banks.Select(b => Path.GetFileNameWithoutExtension(b.Path)).Where(Has);

    public int TotalChanged => _byStem.Values.Sum(e => e.Changed);

    /// <summary>The distinct mods these .bnk files came from, for a status line.</summary>
    public IEnumerable<string> Sources =>
        _byStem.Values.Select(e => e.SourceName)
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
}
