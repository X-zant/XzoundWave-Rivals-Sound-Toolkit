using System.IO;
using System.Text.RegularExpressions;

namespace MRAudioKit;

/// <summary>
/// SoundKit's 0-PAK-NAME-LIST.txt: which built files go into which named mod, and in which folder.
///
/// One rule per line, <c>&lt;search&gt;:&lt;folder&gt;:&lt;pakname&gt;</c>, or <c>&lt;search&gt;:&lt;folderAndPak&gt;</c> when the
/// two are the same; blank lines and lines starting with # are skipped, and a line with any other
/// number of fields or an empty one is skipped too. The first rule that matches a file takes it.
///   A bank matches when its name (bnk_vo_1014001) contains the search text, ignoring case.
///   A loose Media wem matches an all-digit search equal to its id; failing that -- this is what
///   the game data adds over SoundKit, which knows a loose wem by its id alone -- it matches the
///   way the bank that plays it would, so a rule for bnk_amb_asgarde01 takes that map's loose files.
/// Files of one folder and pak name, from any rules, go into one mod. A pak name gets the patch
/// suffix _9999999_P unless it already ends in one.
/// </summary>
public sealed class PakNameList
{
    public sealed record Rule(int Line, string Search, string Folder, string PakName)
    {
        public bool IsMediaId => Search.All(char.IsDigit);
    }

    public string Path { get; init; } = "";
    public IReadOnlyList<Rule> Rules { get; init; } = [];
    public IReadOnlyList<string> Skipped { get; init; } = [];

    public static PakNameList Read(string path)
    {
        var rules = new List<Rule>();
        var skipped = new List<string>();
        var n = 0;
        foreach (var raw in File.ReadLines(path))
        {
            n++;
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(':').Select(p => p.Trim()).ToArray();
            if (parts.Length is < 2 or > 3 || parts.Any(p => p.Length == 0))
            {
                skipped.Add($"line {n}: \"{line}\" is not <search>:<folder>:<pakname> or <search>:<folderAndPak>");
                continue;
            }
            var folder = parts[1];
            if (folder.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0 || folder.Split('\\', '/').Any(s => s is ".." or "."))
            {
                skipped.Add($"line {n}: the folder \"{folder}\" is not a folder name");
                continue;
            }
            var pak = parts.Length == 3 ? parts[2] : parts[1];
            pak = string.Concat(pak.Split(System.IO.Path.GetInvalidFileNameChars()));
            if (pak.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) pak = pak[..^4];
            rules.Add(new Rule(n, parts[0], folder, PakBase(pak)));
        }
        return new PakNameList { Path = path, Rules = rules, Skipped = skipped };
    }

    /// <summary>A pak name with the patch suffix that makes it load after the game's: _9999999_P, unless it has one.</summary>
    public static string PakBase(string name) =>
        Regex.IsMatch(name, @"_\d+_P$", RegexOptions.IgnoreCase) ? name : name + "_9999999_P";

    /// <summary>
    /// The rule for a built file (its path in the mod), or null. <paramref name="playingBank"/> answers,
    /// for a loose wem's media id, the bank that plays it; it is asked only when no id rule matched.
    /// </summary>
    public Rule Match(string relPath, Func<uint, string> playingBank)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(relPath);
        if (relPath.EndsWith(".wem", StringComparison.OrdinalIgnoreCase) && uint.TryParse(name, out var id))
        {
            if (Rules.FirstOrDefault(r => r.IsMediaId && r.Search == name) is { } byId) return byId;
            var bank = playingBank?.Invoke(id) is { } b ? System.IO.Path.GetFileNameWithoutExtension(b) : null;
            return bank is null ? null : Rules.FirstOrDefault(r => bank.Contains(r.Search, StringComparison.OrdinalIgnoreCase));
        }
        return Rules.FirstOrDefault(r => name.Contains(r.Search, StringComparison.OrdinalIgnoreCase));
    }
}
