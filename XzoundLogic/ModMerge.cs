using System.IO;
using System.Text;

namespace MRAudioKit;

/// <summary>
/// Combines two mods into one bank.
///
/// Each mod is diffed against the bank the game ships, so only what its author
/// actually replaced is carried over -- everything they left alone stays vanilla
/// rather than being baked in from whichever mod happened to be read last.
///
/// Where both mods replaced the same sound, the priority mod wins. Those collisions
/// are the interesting part and are reported by event name, not just counted: they
/// are what someone will want to overrule by hand.
/// </summary>
public static class ModMerge
{
    public sealed record Collision(uint MediaId, string Bank, string Events, string Subtitle,
                                   int PriorityBytes, int SecondaryBytes);

    public sealed record Result(int Banks, int FromPriority, int FromSecondary,
                                List<Collision> Collisions, List<string> Skipped, string Log)
    {
        /// <summary>Loose Media files written, each the audio of a sound no bank holds.</summary>
        public int Loose { get; init; }
    }

    /// <param name="extractTo">
    /// When given, each mod's own changed wems are also written here under a folder
    /// per mod, so they can be listened to or reused afterwards.
    /// </param>
    public static Result Build(GameSession session, string priorityPath, string secondaryPath,
                               string outRoot, Settings settings, string extractTo = null,
                               Action<string> progress = null)
    {
        var log = new StringBuilder();
        var skipped = new List<string>();

        var looseA = new List<ModBank.LooseMedia>();
        var looseB = new List<ModBank.LooseMedia>();
        var a = Load(session, priorityPath, settings, "priority", skipped, progress, looseA);
        var b = Load(session, secondaryPath, settings, "secondary", skipped, progress, looseB);
        if (a.Count == 0 && b.Count == 0 && looseA.Count == 0 && looseB.Count == 0)
            throw new InvalidDataException("neither mod changes a sound: no bank with changed embedded media, " +
                                           "and no loose Media wem that differs from the game's.");

        var collisions = new List<Collision>();
        int fromA = 0, fromB = 0, banks = 0;

        // One sound changed by both mods by different routes -- one in a bank, the other as a
        // loose file -- is a collision like any other, and the priority mod's route wins: its
        // loose file over the secondary's bank entry (which goes back to the shipped one), its
        // bank entry over the secondary's loose file (which is left out).
        var looseIdsA = looseA.Select(f => ModUpdate.MediaIdOf(f.MountedAt)).OfType<uint>().ToHashSet();
        var looseIdsB = looseB.Select(f => ModUpdate.MediaIdOf(f.MountedAt)).OfType<uint>().ToHashSet();
        var bankIdsA = new HashSet<uint>();
        int LooseSize(List<ModBank.LooseMedia> files, uint id) =>
            files.FirstOrDefault(f => ModUpdate.MediaIdOf(f.MountedAt) == id)?.Bytes.Length ?? 0;

        // A bank at a time, so a mod that touches banks the other does not still
        // contributes them whole.
        foreach (var bankName in a.Keys.Concat(b.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                                  .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            a.TryGetValue(bankName, out var ca);
            b.TryGetValue(bankName, out var cb);

            // Secondary first, then priority overwrites -- that IS the precedence rule.
            var payload = new Dictionary<uint, byte[]>();
            if (cb is not null)
                foreach (var (id, bytes) in cb.Changed)
                {
                    if (looseIdsA.Contains(id))
                    {
                        var row = cb.Rows.Rows.FirstOrDefault(r => r.MediaId == id);
                        collisions.Add(new Collision(id, bankName, row?.Display ?? "", row?.Subtitle ?? "",
                                                     LooseSize(looseA, id), bytes.Length));
                        continue;
                    }
                    payload[id] = bytes;
                }
            if (ca is not null)
                foreach (var (id, bytes) in ca.Changed)
                {
                    bankIdsA.Add(id);
                    if (looseIdsB.Contains(id))
                    {
                        var row = ca.Rows.Rows.FirstOrDefault(r => r.MediaId == id);
                        collisions.Add(new Collision(id, bankName, row?.Display ?? "", row?.Subtitle ?? "",
                                                     bytes.Length, LooseSize(looseB, id)));
                    }
                    if (cb is not null && cb.Changed.ContainsKey(id))
                    {
                        var row = (ca.Rows.Rows.FirstOrDefault(r => r.MediaId == id));
                        collisions.Add(new Collision(id, bankName,
                            row?.Display ?? "", row?.Subtitle ?? "",
                            bytes.Length, cb.Changed[id].Length));
                    }
                    payload[id] = bytes;
                }

            fromA += ca?.Changed.Count ?? 0;
            fromB += cb?.Changed.Count ?? 0;

            if (extractTo is not null)
            {
                // The ROLE leads the folder name. Two mods for the same character
                // usually ship a bank of the same name, so naming the folders after
                // the files alone puts both sets in one folder and one overwrites
                // the other -- silently losing exactly what was being extracted.
                Dump(ca, Path.Combine(extractTo, "priority - " + Safe(Name(priorityPath))), bankName);
                Dump(cb, Path.Combine(extractTo, "secondary - " + Safe(Name(secondaryPath))), bankName);
            }

            if (payload.Count == 0) continue;

            // Rebuild from the SHIPPED bank, not from either mod's copy: starting from
            // one mod's file would silently inherit everything else that mod changed.
            //
            // It must be the SAME shipped bank the diff was taken against. A VO bank
            // exists once per language, and merging into the wrong language's copy
            // matched 2 of 279 media ids -- a bank that looks built and is empty.
            var vanillaPath = ca?.ShippedPath ?? cb?.ShippedPath;
            if (vanillaPath is null)
            {
                log.AppendLine($"{bankName}: no shipped bank to merge into, skipped.");
                continue;
            }

            byte[] orig;
            try { orig = session.Bank(vanillaPath).Read(); }
            catch (Exception ex) { log.AppendLine($"{bankName}: {ex.Message}"); continue; }

            var built = BnkBuilder.Build(orig, payload);
            var dest = Path.Combine(outRoot, vanillaPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, built.Bytes);
            banks++;
            log.AppendLine($"{bankName}: {built.Replaced} merged " +
                           $"({ca?.Changed.Count ?? 0} priority, {cb?.Changed.Count ?? 0} secondary), " +
                           $"{orig.Length:N0} B -> {built.Bytes.Length:N0} B");
        }

        // Loose Media files: each replaces the game's file at its own path, whatever bank plays
        // it, so they merge by path -- secondary first, priority over it, the same rule. The same
        // id under two language folders is two files, not a collision.
        var looseOut = new Dictionary<string, ModBank.LooseMedia>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in looseB)
            if (!(ModUpdate.MediaIdOf(f.MountedAt) is { } id && bankIdsA.Contains(id)))
                looseOut[Norm(f.MountedAt)] = f;
        foreach (var f in looseA)
        {
            if (looseOut.TryGetValue(Norm(f.MountedAt), out var other))
            {
                var id = ModUpdate.MediaIdOf(f.MountedAt) ?? 0;
                string bank = null;
                try { bank = id == 0 ? null : session.PlayingBank(id); } catch { }
                var lang = GameSession.LanguageOf(f.MountedAt);
                collisions.Add(new Collision(id,
                    (bank is null ? "Media" : Path.GetFileNameWithoutExtension(bank) + " (loose)") + (lang is null ? "" : $" [{lang}]"),
                    "", "", f.Bytes.Length, other.Bytes.Length));
            }
            looseOut[Norm(f.MountedAt)] = f;
        }
        foreach (var f in looseOut.Values.OrderBy(f => f.MountedAt, StringComparer.OrdinalIgnoreCase))
        {
            var dest = Path.Combine(outRoot, f.MountedAt.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, f.Bytes);
        }
        fromA += looseA.Count;
        fromB += looseB.Count;
        if (looseOut.Count > 0)
            log.AppendLine($"loose Media files: {looseOut.Count} written ({looseA.Count} priority, {looseB.Count} secondary).");
        if (extractTo is not null)
        {
            DumpLoose(looseA, Path.Combine(extractTo, "priority - " + Safe(Name(priorityPath))));
            DumpLoose(looseB, Path.Combine(extractTo, "secondary - " + Safe(Name(secondaryPath))));
        }

        var csv = new StringBuilder();
        csv.AppendLine("MediaId,Bank,Event(s),Subtitle,PriorityBytes,SecondaryBytes");
        foreach (var c in collisions.OrderBy(c => c.Bank).ThenBy(c => c.MediaId))
            csv.AppendLine(string.Join(',', new[]
            {
                c.MediaId.ToString(), c.Bank, c.Events, c.Subtitle,
                c.PriorityBytes.ToString(), c.SecondaryBytes.ToString(),
            }.Select(x => $"\"{(x ?? "").Replace("\"", "\"\"")}\"")));
        Directory.CreateDirectory(outRoot);
        File.WriteAllText(Path.Combine(outRoot, "merge-collisions.csv"), csv.ToString(),
                          new UTF8Encoding(true));

        var header = new StringBuilder();
        header.AppendLine($"Merged {Name(priorityPath)} (priority) with {Name(secondaryPath)}.");
        header.AppendLine($"{fromA} sound(s) changed by the priority mod, {fromB} by the secondary.");
        header.AppendLine(collisions.Count == 0
            ? "No sound was changed by both."
            : $"{collisions.Count} sound(s) changed by BOTH — the priority mod won each. " +
              "See merge-collisions.csv.");
        foreach (var c in collisions.Take(10))
            header.AppendLine($"  {c.MediaId}  {c.Events}" +
                              (string.IsNullOrEmpty(c.Subtitle) ? "" : $"  \"{c.Subtitle}\""));
        if (skipped.Count > 0)
            header.AppendLine($"Skipped: {string.Join(", ", skipped)}");
        header.AppendLine();
        header.Append(log);
        File.WriteAllText(Path.Combine(outRoot, "build-log.txt"), header.ToString(), new UTF8Encoding(true));

        return new Result(banks, fromA, fromB, collisions, skipped, header.ToString()) { Loose = looseOut.Count };
    }

    // ---- helpers -----------------------------------------------------------

    /// <param name="loose">Receives the loose Media files the mod ships that differ from the game's at the same path.</param>
    private static Dictionary<string, ModBank.Comparison> Load(
        GameSession session, string path, Settings settings, string role,
        List<string> skipped, Action<string> progress, List<ModBank.LooseMedia> loose)
    {
        var map = new Dictionary<string, ModBank.Comparison>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path)) return map;

        progress?.Invoke($"reading the {role} mod…");
        ModBank.Opened opened;
        try { opened = ModBank.Open(path, settings, withMedia: true); }
        catch (Exception ex) { skipped.Add($"{Name(path)}: {ex.Message}"); return map; }
        skipped.AddRange(opened.Skipped);

        // A file byte for byte the game's own at its path changes nothing and is not carried.
        var same = 0;
        foreach (var f in opened.Media)
        {
            byte[] game = null;
            try { game = session.GameFileAt(f.MountedAt)?.Read(); } catch { }
            if (game is not null && game.AsSpan().SequenceEqual(f.Bytes)) { same++; continue; }
            loose.Add(f);
        }
        if (same > 0) progress?.Invoke($"{role}: {same} loose Media file(s) are the game's own bytes, left out");

        foreach (var bank in opened.Banks)
        {
            progress?.Invoke($"{role}: diffing {bank.Name}…");
            var cmp = ModBank.Compare(session, bank);
            if (cmp.Changed.Count == 0)
            {
                skipped.Add($"{bank.Name} ({role}): nothing changed from the shipped bank");
                continue;
            }
            map[bank.Name] = cmp;
        }
        return map;
    }

    private static void Dump(ModBank.Comparison c, string root, string bankName)
    {
        if (c is null || c.Changed.Count == 0) return;
        var dir = Path.Combine(root, Path.GetFileNameWithoutExtension(bankName));
        Directory.CreateDirectory(dir);
        foreach (var (id, bytes) in c.Changed)
        {
            // Named so it can be dropped straight back into the staging pane.
            var row = c.Rows.Rows.FirstOrDefault(r => r.MediaId == id);
            var note = Safe(row?.Display ?? "");
            var name = string.IsNullOrEmpty(note) ? $"{id}.wem" : $"{id}-{note}.wem";
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
        }
    }

    /// <summary>A mod's loose Media files, under its role folder, in the language folder they came from.</summary>
    private static void DumpLoose(List<ModBank.LooseMedia> files, string root)
    {
        foreach (var f in files)
        {
            var lang = GameSession.LanguageOf(f.MountedAt);
            var dir = Path.Combine(root, "Media", lang ?? "");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, Path.GetFileName(f.MountedAt)), f.Bytes);
        }
    }

    private static string Norm(string mountedAt) => mountedAt.Replace('\\', '/').TrimStart('/');

    private static string Name(string path) => Path.GetFileNameWithoutExtension(path ?? "");

    private static string Safe(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 60 ? s[..60] : s;
    }
}
