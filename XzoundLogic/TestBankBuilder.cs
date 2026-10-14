using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MRAudioKit;

/// <summary>
/// Builds a "numbered" soundbank: every embedded media is replaced by a clip that
/// speaks a number, so triggering a sound in-game tells you which entry you just hit.
/// A legend CSV is written beside it — hear "forty-two", look up 42, get the media id,
/// the event name and any subtitle.
///
/// This is the standard way to map unlabelled SFX to what they actually do, and the
/// numbered clips (0.wem … 1000.wem, plus a silent one) come from soundKit's 0-TESTS
/// folder or anywhere else with the same naming.
/// </summary>
public static class TestBankBuilder
{
    public sealed record NumberedWems(Dictionary<int, string> ByNumber, string SilentPath)
    {
        public int Max => ByNumber.Count == 0 ? -1 : ByNumber.Keys.Max();
    }

    private static readonly Regex NumName = new(@"^(\d+)$", RegexOptions.Compiled);

    /// <summary>A folder of {number}.wem files, plus an optional silent one.</summary>
    public static NumberedWems ScanFolder(string dir)
    {
        var byNumber = new Dictionary<int, string>();
        string silent = null;
        foreach (var f in Directory.EnumerateFiles(dir, "*.wem"))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            var m = NumName.Match(stem);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) byNumber[n] = f;
            else if (stem.Contains("silent", StringComparison.OrdinalIgnoreCase)) silent = f;
        }
        return new NumberedWems(byNumber, silent);
    }

    public sealed record Row(int Number, uint MediaId, string Bank, string Events,
                             string Category, string Subtitle, string Notes, double? Seconds);

    public sealed record Result(int Banks, int Numbered, int Silenced, int Unassigned,
                                List<Row> Legend, string Log);

    /// <summary>
    /// Number every embedded media in <paramref name="bankPaths"/>, continuing the count
    /// across banks so a number is unique within the whole test build.
    /// </summary>
    /// <summary>Media entries across the given banks — what the numbering has to cover.</summary>
    public static int CountEntries(GameSession session, IEnumerable<string> bankPaths,
                                   BankBase baseBanks = null)
    {
        var total = 0;
        foreach (var p in bankPaths)
        {
            var bytes = (baseBanks ?? BankBase.None).Read(session, p);
            if (bytes is null) continue;
            try
            {
                var didx = BnkBuilder.ReadSections(bytes).FirstOrDefault(s => s.Tag == "DIDX");
                if (didx is not null) total += BnkBuilder.ReadDidx(didx.Body).Count;
            }
            catch { }
        }
        return total;
    }

    /// <param name="skipEntries">
    /// Ignore the first N media of each bank (they get the silent clip). Lets a bank
    /// with more entries than you have clips be covered in two passes.
    /// </param>
    /// <param name="restartPerBank">
    /// Number each bank from <paramref name="startAt"/> again instead of continuing the
    /// count. There is no fixed 0-999 range — the numbering runs as far as the clips in
    /// the folder go, so a bank with more entries than you have clips either restarts
    /// per bank or runs out.
    /// </param>
    /// <param name="only">
    /// When given, only these media ids are numbered and everything else keeps its
    /// vanilla audio. That is what makes "test just these sounds" work: the rest of
    /// the character still sounds normal, so the test is usable in a real match.
    /// </param>
    /// <param name="muted">
    /// Silenced instead of numbered. When several sounds fire together only the
    /// loudest is intelligible, so muting the one you identified is how the others
    /// become audible on the next run.
    /// </param>
    /// <param name="keepNumbers">
    /// Numbers already assigned in an earlier build. Reusing them is what makes
    /// repeated runs usable: if muting one sound renumbered the rest, every note the
    /// author had already written would point at the wrong sound.
    /// </param>
    public static Result Build(GameSession session, IEnumerable<string> bankPaths,
                               NumberedWems wems, string outRoot, int startAt,
                               bool silenceOverflow, SkinSounds context,
                               bool restartPerBank = false, int skipEntries = 0,
                               IReadOnlySet<uint> only = null,
                               IReadOnlySet<uint> muted = null,
                               IReadOnlyDictionary<uint, int> keepNumbers = null,
                               BankBase baseBanks = null,
                               IReadOnlyCollection<uint> loose = null)
    {
        var log = new StringBuilder();
        var legend = new List<Row>();
        var cache = new Dictionary<string, byte[]>();
        byte[] Load(string p)
        {
            if (!cache.TryGetValue(p, out var b)) cache[p] = b = File.ReadAllBytes(p);
            return b;
        }

        var next = startAt;
        int banks = 0, numbered = 0, silenced = 0, unassigned = 0;

        // Numbers already handed out are off the table, so a rebuild cannot give two
        // sounds the same number.
        var taken = keepNumbers is null ? [] : new HashSet<int>(keepNumbers.Values);
        int NextFree()
        {
            while (taken.Contains(next)) next++;
            return next;
        }

        foreach (var bankPath in bankPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var orig = (baseBanks ?? BankBase.None).Read(session, bankPath);
            if (orig is null) continue;

            var sections = BnkBuilder.ReadSections(orig);
            var didx = sections.FirstOrDefault(s => s.Tag == "DIDX");
            var data = sections.FirstOrDefault(s => s.Tag == "DATA");
            if (didx is null) { log.AppendLine($"{Path.GetFileName(bankPath)}: no embedded media, skipped."); continue; }

            var bankName = Path.GetFileName(bankPath);
            var stem = Path.GetFileNameWithoutExtension(bankName);
            var payload = new Dictionary<uint, byte[]>();
            if (restartPerBank) next = startAt;

            // DIDX order, so the numbering is reproducible from the file itself and the
            // same bank always numbers the same way.
            var entryIndex = -1;
            foreach (var entry in BnkBuilder.ReadDidx(didx.Body))
            {
                // Out of scope: left exactly as shipped, and not counted as a miss.
                if (only is not null && !only.Contains(entry.Id)) continue;
                // A second pass over a bank bigger than the clip set: skip what the
                // first pass already numbered, so 1001 clips can cover 1056 entries.
                entryIndex++;
                if (entryIndex < skipEntries)
                {
                    if (silenceOverflow && wems.SilentPath is not null)
                        payload[entry.Id] = Load(wems.SilentPath);
                    continue;
                }
                var rowsFor = context?.Rows
                    .Where(r => r.MediaId == entry.Id &&
                                r.Bank.Equals(stem, StringComparison.OrdinalIgnoreCase))
                    .ToList() ?? [];

                // Muted: silence shaped like the original, but still listed in the
                // legend under its number so "what was 33 again?" stays answerable.
                if (muted is not null && muted.Contains(entry.Id))
                {
                    var src = data is null
                        ? null : data.Body.AsSpan((int)entry.Offset, (int)entry.Size).ToArray();
                    var quiet = src is null ? null : SilentBankBuilder.Like(src);
                    if (quiet is not null)
                    {
                        payload[entry.Id] = quiet;
                        silenced++;
                        if (keepNumbers is not null && keepNumbers.TryGetValue(entry.Id, out var had))
                            legend.Add(new Row(had, entry.Id, bankName,
                                string.Join(" | ", rowsFor.Select(r => r.Display).Distinct()),
                                rowsFor.FirstOrDefault()?.Category ?? "",
                                rowsFor.FirstOrDefault()?.Subtitle ?? "",
                                "(muted) " + (rowsFor.FirstOrDefault()?.Notes ?? ""),
                                rowsFor.FirstOrDefault()?.Seconds));
                        continue;
                    }
                }

                // An earlier build already named this one; keep that number.
                var assigned = keepNumbers is not null && keepNumbers.TryGetValue(entry.Id, out var prior)
                    ? prior : NextFree();

                if (wems.ByNumber.TryGetValue(assigned, out var path))
                {
                    payload[entry.Id] = Load(path);
                    legend.Add(new Row(
                        assigned, entry.Id, bankName,
                        string.Join(" | ", rowsFor.Select(r => r.Display).Distinct()),
                        rowsFor.FirstOrDefault()?.Category ?? "",
                        rowsFor.FirstOrDefault()?.Subtitle ?? "",
                        rowsFor.FirstOrDefault()?.Notes ?? "",
                        rowsFor.FirstOrDefault()?.Seconds));
                    numbered++;
                    taken.Add(assigned);
                    if (assigned == next) next++;
                }
                else if (silenceOverflow && wems.SilentPath is not null)
                {
                    // Past the end of the numbered clips. Silence beats leaving vanilla
                    // audio in place, which would be mistaken for a working sound.
                    payload[entry.Id] = Load(wems.SilentPath);
                    silenced++;
                }
                else unassigned++;
            }

            if (payload.Count == 0) continue;
            var built = BnkBuilder.Build(orig, payload);
            var dest = Path.Combine(outRoot, bankPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, built.Bytes);
            banks++;
            log.AppendLine($"{bankName}: {built.Replaced} of {BnkBuilder.ReadDidx(didx.Body).Count} " +
                           $"media replaced, {orig.Length:N0} B -> {built.Bytes.Length:N0} B");
        }

        // Sounds no bank holds: the loose file is what plays, so it is what gets the number or the
        // silence, written at the game's own path. After the banks, in media-id order, so the
        // numbering is reproducible; "restart per bank" counts the loose files as one more.
        var looseWritten = 0;
        if (loose is { Count: > 0 })
        {
            if (restartPerBank) next = startAt;
            foreach (var id in loose.Distinct().OrderBy(x => x))
            {
                if (only is not null && !only.Contains(id)) continue;
                var src = session.LooseMedia(id, session.Language) ?? session.LooseMedia(id);
                if (src is null) continue;
                var rowsFor = context?.Rows.Where(r => r.MediaId == id).ToList() ?? [];
                var bankLabel = (rowsFor.FirstOrDefault()?.Bank ?? Path.GetFileNameWithoutExtension(src.Path)) + " (loose file)";
                Row LegendRow(int number, string notePrefix = "") => new(number, id, bankLabel,
                    string.Join(" | ", rowsFor.Select(r => r.Display).Distinct()),
                    rowsFor.FirstOrDefault()?.Category ?? "", rowsFor.FirstOrDefault()?.Subtitle ?? "",
                    notePrefix + (rowsFor.FirstOrDefault()?.Notes ?? ""), rowsFor.FirstOrDefault()?.Seconds);

                byte[] bytes;
                if (muted is not null && muted.Contains(id))
                {
                    try { bytes = SilentBankBuilder.Like(src.Read()); } catch { bytes = null; }
                    if (bytes is null) { log.AppendLine($"loose {id}: muted, but its header could not be read."); continue; }
                    silenced++;
                    if (keepNumbers is not null && keepNumbers.TryGetValue(id, out var had)) legend.Add(LegendRow(had, "(muted) "));
                }
                else
                {
                    var assigned = keepNumbers is not null && keepNumbers.TryGetValue(id, out var prior) ? prior : NextFree();
                    if (wems.ByNumber.TryGetValue(assigned, out var clip))
                    {
                        bytes = Load(clip);
                        legend.Add(LegendRow(assigned));
                        numbered++;
                        taken.Add(assigned);
                        if (assigned == next) next++;
                    }
                    else if (silenceOverflow && wems.SilentPath is not null) { bytes = Load(wems.SilentPath); silenced++; }
                    else { unassigned++; continue; }
                }
                var dest = Path.Combine(outRoot, src.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, bytes);
                looseWritten++;
            }
            if (looseWritten > 0) log.AppendLine($"{looseWritten} loose file(s) written for sounds no bank holds.");
        }

        Directory.CreateDirectory(outRoot);
        File.WriteAllText(Path.Combine(outRoot, "test-bank-legend.csv"), LegendCsv(legend),
                          new UTF8Encoding(true));

        var header = new StringBuilder();
        header.AppendLine($"Numbered test build — {numbered} sound(s) numbered from {startAt}.");
        header.AppendLine("Trigger a sound in game, hear the number, look it up in test-bank-legend.csv.");
        if (silenced > 0) header.AppendLine($"{silenced} entries were silenced (muted, or past the last clip).");
        if (unassigned > 0) header.AppendLine($"{unassigned} entries kept their ORIGINAL audio (ran out of numbers).");
        header.AppendLine();
        header.Append(log);
        File.WriteAllText(Path.Combine(outRoot, "build-log.txt"), header.ToString(), new UTF8Encoding(true));

        return new Result(banks, numbered, silenced, unassigned, legend, header.ToString());
    }

    /// <summary>
    /// The legend as it is written to disk.
    ///
    /// This is the file the whole numbered build exists for -- hear a number in game,
    /// find the row -- so it has to be readable by whatever the author opens it with,
    /// on whatever machine. The length is formatted with the invariant culture: a
    /// German-locale build would otherwise put "3,71" inside a comma-separated file,
    /// where a reader splits it into two columns or parses it as 3.
    /// </summary>
    public static string LegendCsv(IEnumerable<Row> legend)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Number,MediaId,Bank,Event(s),Category,Subtitle,Notes,Length");
        foreach (var r in legend.OrderBy(r => r.Number))
            csv.AppendLine(string.Join(',', new[]
            {
                r.Number.ToString(CultureInfo.InvariantCulture),
                r.MediaId.ToString(CultureInfo.InvariantCulture), r.Bank, r.Events,
                r.Category, r.Subtitle, r.Notes,
                r.Seconds is null ? "" : r.Seconds.Value.ToString("0.00", CultureInfo.InvariantCulture),
            }.Select(s => $"\"{(s ?? "").Replace("\"", "\"\"")}\"")));
        return csv.ToString();
    }
}
