using System.IO;

namespace MRAudioKit;

/// <summary>
/// Where a builder gets the .bnk it is about to modify.
///
/// Normally that is the game's own file. When a mod for that .bnk is loaded it is the
/// mod's bytes instead, so a build layers onto what the mod already changed rather than
/// silently throwing it away. Everything downstream is unchanged: a .bnk is a .bnk, and
/// the builders never cared where the bytes came from.
///
/// Keyed by stem (<c>bnk_vo_1029001</c>), because that is what a mod replaces and what
/// the game loads -- the full pak path differs between the two and cannot be the key.
/// </summary>
public sealed class BankBase
{
    private readonly Dictionary<string, byte[]> _byStem;

    public BankBase(IEnumerable<KeyValuePair<string, byte[]>> overrides = null)
    {
        _byStem = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in overrides ?? []) _byStem[kv.Key] = kv.Value;
    }

    /// <summary>The game's own files, with nothing layered over them.</summary>
    public static readonly BankBase None = new();

    public int Count => _byStem.Count;

    /// <summary>
    /// The bytes to build from, or null when the path cannot be read at all -- callers
    /// skip that bank rather than failing the whole build over one unreadable file.
    /// </summary>
    public byte[] Read(GameSession session, string bankPath)
    {
        if (_byStem.TryGetValue(Path.GetFileNameWithoutExtension(bankPath), out var modded))
            return modded;
        if (session?.Provider is null) return null;
        // Through the session, which resolves a path several containers ship to the copy
        // the engine loads. Asking the provider directly returns whichever container
        // registered the path first, so a build could rewrite a different copy of a bank
        // than the one whose rows the author was reading.
        var file = session.Bank(bankPath);
        if (file is null && !session.Provider.Files.TryGetValue(bankPath, out file)) return null;
        try { return file.Read(); } catch { return null; }
    }

    /// <summary>Which of these paths this overlay supplies, for a line in a log.</summary>
    public IEnumerable<string> Covering(IEnumerable<string> bankPaths) =>
        bankPaths.Where(p => _byStem.ContainsKey(Path.GetFileNameWithoutExtension(p)));
}
