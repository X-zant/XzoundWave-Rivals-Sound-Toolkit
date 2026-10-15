using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MRAudioKit;

/// <summary>
/// Sorts a pile of finished wems into one folder per mod: SoundKit's WEM Auto-Organizer (its
/// Dev Option 204), reading the same 0-WEM-FOLDER-NAME-LIST rules, or with no rules at all.
///
/// SoundKit knows a wem only by its file name, so a rule like vo_1047 matches a file only when
/// the event is written into the name. Here every wem's media id is looked up in the game: the
/// events that play it and the banks they sit in are matched as well as the name, so 12345.wem
/// sorts the same as 12345-vo_10470004_select_01_play.wem. Files are copied, never moved, and a
/// [DELETE] rule leaves its matches out of the output instead of deleting them.
///
/// Rules, one per line (blank and # lines skipped):
///   &lt;folder&gt;:&lt;pattern&gt;:&lt;Dest&gt;   &lt;folder&gt;:&lt;pattern&gt;:&lt;Parent&gt;:&lt;Dest&gt;   &lt;folder&gt;:&lt;pattern&gt;:[DELETE]
/// &lt;folder&gt; is a numbered folder of the input (1, 2, 3 ... or "1-Low"); with no numbered folders
/// the whole input is folder 1. &lt;pattern&gt; is vo_ + 4-5 digits, display_ or sfx_ + 4-7 digits, any of
/// them followed by more text for a narrower match, or a bare media id for that wem alone. Exact
/// ids go first, then patterns with extra text (longest first), then more digits first, then file
/// order; a wem goes to the first rule that matches it. With no rule file, every wem goes to a
/// folder named for the character, skin and bank that play it.
/// </summary>
public static class WemOrganizer
{
    public sealed record Rule(int Line, int Folder, string Pattern, string Parent, string Dest, bool Delete,
                              bool Exact, bool Extended, int Digits);

    private static readonly Regex Shaped = new(@"^(?:vo_(\d{4,5})|display_(\d{4,7})|sfx_(\d{4,7}))(?!\d)(.*)$", RegexOptions.IgnoreCase);

    public static (List<Rule> Rules, List<string> Skipped) ReadRules(string file)
    {
        var rules = new List<Rule>();
        var skipped = new List<string>();
        var n = 0;
        foreach (var raw in File.ReadLines(file))
        {
            n++;
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split(':', 4).Select(x => x.Trim()).ToArray();
            string Skip(string why) { skipped.Add($"line {n}: {why}  ({line})"); return null; }
            if (f.Length < 3) { Skip("needs <folder>:<pattern>:<Dest>"); continue; }
            if (!int.TryParse(f[0], out var folder) || folder < 0) { Skip("the folder must be a number"); continue; }
            var pattern = f[1];
            bool exact = false, extended = false;
            int digits;
            if (Regex.IsMatch(pattern, @"^\d{4,10}$")) { exact = true; digits = pattern.Length; }
            else if (Shaped.Match(pattern) is { Success: true } m)
            {
                digits = (m.Groups[1].Success ? m.Groups[1] : m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value.Length;
                extended = m.Groups[4].Value.Length > 0;
                if (m.Groups[4].Value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Skip("the text after the digits has a character a file name cannot"); continue; }
            }
            else
            {
                Skip(Regex.IsMatch(pattern, @"^vo_\d", RegexOptions.IgnoreCase) ? "vo_ takes 4 or 5 digits (the hero, then a form digit), then any text"
                     : Regex.IsMatch(pattern, @"^(display|sfx)_\d", RegexOptions.IgnoreCase) ? "display_ and sfx_ take 4 to 7 digits, then any text"
                     : "the pattern must be vo_, display_ or sfx_ and digits, or a media id");
                continue;
            }

            var delete = f.Length == 3 && f[2].Equals("[DELETE]", StringComparison.OrdinalIgnoreCase);
            if (!delete && f.Skip(2).Any(x => x.Equals("[DELETE]", StringComparison.OrdinalIgnoreCase))) { Skip("[DELETE] goes in the third field alone"); continue; }
            string parent = null, dest = null;
            if (!delete)
            {
                parent = f.Length == 4 && f[2].Length > 0 ? f[2] : null;
                dest = f.Length == 4 ? f[3] : f[2];
                if (string.IsNullOrWhiteSpace(dest)) { Skip("no destination folder"); continue; }
                if (new[] { parent, dest }.Any(x => x is not null && (x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || x is "." or "..")))
                { Skip("a folder name has a character a folder name cannot"); continue; }
            }
            rules.Add(new Rule(n, folder, pattern, parent, dest, delete, exact, extended, digits));
        }
        return (rules, skipped);
    }

    public sealed record Result(int Copied, int LeftOut, int Unmatched, int Unknown, string Report);

    /// <summary>
    /// Copy every wem under <paramref name="input"/> into <paramref name="output"/>, sorted by
    /// <paramref name="rulesFile"/>, or by character, skin and bank when it is null.
    /// </summary>
    public static Result Run(GameSession session, string input, string rulesFile, string output, Action<string> progress = null)
    {
        progress ??= _ => { };
        if (!session.BankIndexReady) { progress("indexing the game's banks..."); session.IndexBanks(_ => { }); }
        var report = new StringBuilder();
        report.AppendLine($"Organizing the wems in {input}");
        report.AppendLine(rulesFile is null ? "by character, skin and bank (no rule list)" : $"by {rulesFile}");
        report.AppendLine();

        // The pools: each numbered folder of the input, or the whole input as folder 1.
        var numbered = Directory.EnumerateDirectories(input)
            .Select(d => (Dir: d, M: Regex.Match(Path.GetFileName(d), @"^(\d+)(?:-.*)?$")))
            .Where(x => x.M.Success).Select(x => (x.Dir, Folder: int.Parse(x.M.Groups[1].Value))).ToList();
        var pools = numbered.Count > 0 ? numbered : [(input, 1)];
        // Any case of the extension: a folder made on Windows can hold "x.WEM", and Linux matches a pattern by case.
        var anyCase = new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive };
        var files = pools.SelectMany(p => Directory.EnumerateFiles(p.Dir, "*.wem", anyCase)
                                                   // Not what an earlier run put in an output folder inside the input.
                                                   .Where(f => Path.GetRelativePath(output, f).StartsWith(".."))
                                                   .Select(f => (Path: f, p.Folder)))
                         .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        progress($"looking up {files.Count} wem(s) in the game...");

        var facts = new Facts(session);
        var placed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // source -> folder under output
        int copied = 0, leftOut = 0, unmatched = 0, unknown = 0;

        string Copy(string source, string folder, int pool)
        {
            var dir = Directory.CreateDirectory(Path.Combine(output, folder)).FullName;
            var name = Path.GetFileName(source);
            var dest = Path.Combine(dir, name);
            // Two sources of one name in one folder: the second gets its pool, then a number.
            for (var i = 1; File.Exists(dest); i++)
                dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + (i == 1 ? $"__src{pool}" : $"__src{pool}_{i}") + Path.GetExtension(name));
            File.Copy(source, dest);
            return dest;
        }

        if (rulesFile is not null)
        {
            var (rules, skipped) = ReadRules(rulesFile);
            foreach (var s in skipped) report.AppendLine("  skipped " + s);
            report.AppendLine($"{rules.Count} rule(s)");
            var ordered = rules.OrderBy(r => r.Exact ? 0 : 1).ThenBy(r => r.Extended ? 0 : 1)
                               .ThenByDescending(r => r.Extended ? r.Pattern.Length : 0)
                               .ThenByDescending(r => r.Digits).ThenBy(r => r.Line).ToList();
            var remaining = files.ToList();
            foreach (var rule in ordered)
            {
                var hits = remaining.Where(f => f.Folder == rule.Folder && Matches(rule, f.Path, facts)).ToList();
                foreach (var hit in hits)
                {
                    remaining.Remove(hit);
                    if (rule.Delete) { leftOut++; continue; }
                    Copy(hit.Path, rule.Parent is null ? rule.Dest : Path.Combine(rule.Parent, rule.Dest), hit.Folder);
                    copied++;
                }
                report.AppendLine($"  line {rule.Line,3}  {rule.Folder}:{rule.Pattern}  ->  " +
                                  (rule.Delete ? "[DELETE] (left out)" : rule.Parent is null ? rule.Dest : Path.Combine(rule.Parent, rule.Dest)) +
                                  $"  {hits.Count} wem(s)");
            }
            foreach (var (path, folder) in remaining)
            {
                Copy(path, Path.Combine("_unmatched", folder.ToString()), folder);
                unmatched++;
            }
            if (unmatched > 0) report.AppendLine($"{unmatched} wem(s) no rule matched, copied to _unmatched");
        }
        else
        {
            foreach (var (path, folder) in files)
            {
                var into = facts.Folder(path) ?? "_unknown";
                if (into == "_unknown") unknown++;
                Copy(path, numbered.Count > 0 ? Path.Combine(folder.ToString(), into) : into, folder);
                copied++;
            }
            if (unknown > 0) report.AppendLine($"{unknown} wem(s) whose id the game does not play, copied to _unknown");
        }
        report.AppendLine();
        report.AppendLine($"{copied} copied, {leftOut} left out by [DELETE], {unmatched} unmatched.");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "organize-report.txt"), report.ToString(), new UTF8Encoding(true));
        return new Result(copied, leftOut, unmatched, unknown, report.ToString());
    }

    private static bool Matches(Rule rule, string path, Facts facts)
    {
        var name = Path.GetFileName(path);
        var id = facts.IdOf(path);
        if (rule.Exact) return id?.ToString() == rule.Pattern;
        // The name, then what the game says about the id: the events that play it and their banks.
        return name.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase) ||
               (id is { } m && facts.Names(m).Any(n => n.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>What the game says about a media id: the events and banks that play it, and where it belongs.</summary>
    private sealed class Facts(GameSession session)
    {
        private readonly Dictionary<uint, List<string>> _names = [];
        private readonly Dictionary<string, ModUpdate.Lines> _lines = new(StringComparer.OrdinalIgnoreCase);
        private GameSnapshot _snapshot;

        public uint? IdOf(string path) =>
            Regex.Match(Path.GetFileNameWithoutExtension(path), @"^(\d+)") is { Success: true } m && uint.TryParse(m.Groups[1].Value, out var id) ? id : null;

        public IReadOnlyList<string> Names(uint id)
        {
            if (_names.TryGetValue(id, out var known)) return known;
            var names = new List<string>();
            _snapshot ??= session.Snapshot();
            foreach (var p in _snapshot.PlayersOf(id, null))
            {
                var bank = _snapshot.Banks[p.Bank];
                names.Add(Path.GetFileNameWithoutExtension(bank));
                if (p.Kind != GameSnapshot.Sound) continue;
                if (!_lines.TryGetValue(bank, out var lines))
                {
                    try { lines = ModUpdate.Lines.Of(session.Bank(bank).Read(), bank); } catch { lines = null; }
                    _lines[bank] = lines;
                }
                if (lines?.SoundEvents.TryGetValue(p.Id, out var events) == true)
                    foreach (var e in events)
                        if (session.EventName(e) is { } name) names.Add(name);
            }
            return _names[id] = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>"Hero-Skin-bnk_vo_1047001" for the bank that plays a wem, or the bank's own name for one no skin owns.</summary>
        public string Folder(string path)
        {
            if (IdOf(path) is not { } id || session.PlayingBank(id) is not { } bank) return null;
            var stem = Path.GetFileNameWithoutExtension(bank);
            var skin = Regex.Matches(stem, @"\d{7}").LastOrDefault()?.Value;
            if (skin is null) return Safe(stem);
            var hero = skin[..4];
            var heroName = session.HeroName(hero);
            var skinName = session.SkinName(hero, skin);
            return Safe($"{(heroName == hero ? hero : heroName)}-{(skinName == skin ? skin : skinName)}-{stem}").Replace(' ', '_');
        }

        private static string Safe(string s) => string.Concat(s.Split(Path.GetInvalidFileNameChars()));
    }
}
