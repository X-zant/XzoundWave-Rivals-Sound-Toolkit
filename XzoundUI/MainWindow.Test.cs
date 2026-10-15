using System.IO;
using Avalonia.Threading;

namespace MRAudioKit.UI;

/// <summary>
/// The Test tab: the builds whose only purpose is to tell you WHICH sound is which.
///
/// A character fires several sounds at once and the .bnk names none of them, so the
/// way to find one is to replace every sound with a clip that speaks a number, hear
/// the number in game, and look it up in the legend. Everything on this tab is a
/// variation on that: number the lot, number only what you picked, silence the rest
/// so nothing else can drown it, or silence the selection outright.
///
/// The old window had four of these as four handlers, each repeating the same seven
/// steps, with the 'keep the numbers we already handed out' dictionary written out
/// character for character in two of them. Here there is one path and a description
/// of the build, which is why the numbering rules below are stated once.
/// </summary>
public partial class MainWindow
{
    private IEnumerable<ToolButton> TestTools() =>
    [
        new("Numbered test .bnk…", () => _ = TestEverythingAsync(), Key: true,
            Hint: "Every sound in the picked .bnk files speaks a number instead. " +
                  "Hear it in game, look it up in test-bank-legend.csv."),

        new("Test the selection…", () => _ = TestSelectionAsync(),
            Hint: "Numbers only the selected sounds. Everything else keeps its normal " +
                  "audio, so the build is still usable in a real match."),

        new("Test only the selection…", () => _ = TestOnlySelectionAsync(),
            Hint: "Numbers the selection and silences everything else in the same " +
                  ".bnk files, so nothing can drown what you are listening for."),

        new("Silent .bnk…", () => _ = TestSilentSelectionAsync(),
            Hint: "Replaces the selected sounds with silence of the same length and " +
                  "format, so the .bnk behaves exactly as it did and only the audio goes."),
    ];

    // ---- one description of a test build ------------------------------------

    /// <summary>
    /// What the four numbered builds differ by, and nothing else.
    /// </summary>
    /// <param name="What">Names the build in the status line and in the folder prompt.</param>
    /// <param name="Banks">The .bnk files to rewrite.</param>
    /// <param name="Only">
    /// Numbered; everything else in those banks keeps its shipped audio. Null means
    /// every entry, which is what "number the whole thing" is.
    /// </param>
    /// <param name="Muted">
    /// Silenced instead of numbered. A mute is not a test-build idea, so this set is
    /// the user's mutes plus whatever this particular build wants out of the way.
    /// </param>
    /// <param name="Keep">
    /// Numbers handed out by an earlier run, reused. Without this, muting one sound
    /// renumbers everything after it and every note the author wrote points at the
    /// wrong sound.
    /// </param>
    /// <param name="Required">How many numbered clips the build needs to cover its scope.</param>
    /// <param name="SilenceOverflow">
    /// Entries past the last clip go silent rather than keeping vanilla audio, which
    /// would be heard as a working sound and read as a failed build.
    /// </param>
    /// <param name="RestartPerBank">Number each .bnk from the start again.</param>
    /// <param name="ShortfallOk">
    /// Whether the build still makes sense with fewer clips than entries. It does when
    /// the overflow is silenced; it does not when the overflow would be vanilla audio.
    /// </param>
    private sealed record TestPlan(
        string What,
        List<string> Banks,
        IReadOnlySet<uint> Only,
        IReadOnlySet<uint> Muted,
        IReadOnlyDictionary<uint, int> Keep,
        int Required,
        bool SilenceOverflow = false,
        bool RestartPerBank = false,
        bool ShortfallOk = false,
        IReadOnlyCollection<uint> Loose = null);

    /// <summary>
    /// Resolve the clips, ask the last question, pick the output folder, build, and
    /// write the assigned numbers back onto the rows.
    /// </summary>
    /// <param name="confirm">
    /// A summary shown once the clips are known, for the build whose summary is about
    /// them. The builds whose question does not involve clips ask before they get here,
    /// so that nobody waits through a minute of speech generation for a dialog they
    /// were always going to cancel.
    /// </param>
    private async Task TestRun(TestPlan plan, Func<TestBankBuilder.NumberedWems, string> confirm = null)
    {
        if (plan.Banks.Count == 0 && (plan.Loose?.Count ?? 0) == 0) { Say("none of those sounds live in a .bnk or a loose file we can find."); return; }

        var wems = await TestClips(plan.Required, plan.ShortfallOk);
        if (wems is null) return;

        if (confirm is not null && TestClipDirForShot is null &&
            !await Ask("Numbered test .bnk", confirm(wems), "Build it", "Cancel"))
        {
            Say("nothing built.");
            return;
        }

        var outRoot = BuildFolderForShot ?? await PickFolder($"Output folder for the {plan.What}");
        if (outRoot is null) { Say("no output folder, nothing built."); return; }

        var lowest = wems.ByNumber.Keys.Min();
        var name = ModBaseName("Test");
        var work = Path.Combine(Path.GetTempPath(), "XzoundWave", "test-" + Guid.NewGuid().ToString("N"));
        (TestBankBuilder.Result Built, string Pak, string Problem) made;
        try
        {
            made = await Working($"Building the {plan.What}", () =>
            {
                var r = TestBankBuilder.Build(Session, plan.Banks, wems, work, lowest,
                                              plan.SilenceOverflow, Sounds, plan.RestartPerBank, 0,
                                              plan.Only, plan.Muted, plan.Keep, TestModBase(), plan.Loose);
                var (ok, message, pak) = TestPack(work, outRoot, name);
                return (r, ok ? pak : null, ok ? null : message);
            });
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (made.Built is null) return;   // Working has already said why.
        if (made.Problem is not null) { Say($"{plan.What}: nothing to pack ({made.Problem})."); return; }
        var result = made.Built;
        _lastBuilt.Clear();
        _lastBuilt.Add(made.Pak);
        _lastBuiltRoot = outRoot;

        var tagged = TestNumbersOnto(result.Legend);
        Refresh();

        // Stable numbering is the point of the whole feature and the one thing a build
        // cannot show you: a sound that kept its number looks exactly like a sound that
        // was numbered for the first time. The old window said so in a dialog; it is
        // said here because otherwise nothing says it at all.
        var also = new List<string>();
        if (plan.Keep?.Count > 0)
            also.Add($"{plan.Keep.Count} kept the number they already had, so earlier notes still match");
        if (tagged > 0)
            also.Add($"{tagged} sound(s) carry their number in the Test # column, kept in the project");

        Say($"{plan.What}: {result.Numbered} numbered, {result.Silenced} silenced, " +
            (result.Unassigned > 0 ? $"{result.Unassigned} left as shipped, " : "") +
            $"{Path.GetFileName(made.Pak)} in {outRoot} -- copy it into ~mods, or Install to ~mods; legend in test-bank-legend.csv" +
            (also.Count > 0 ? "; " + string.Join("; ", also) + "." : "."));
    }

    /// <summary>The render harness: the Test tab's buttons, with its own clip folder and output folder.</summary>
    internal string TestClipDirForShot;
    internal Task TestEverythingForShot() => TestEverythingAsync();
    internal Task TestSelectionForShot() => TestSelectionAsync();
    internal Task TestOnlySelectionForShot() => TestOnlySelectionAsync();
    internal Task TestSilentForShot() => TestSilentSelectionAsync();

    /// <summary>The render harness: select exactly the rows of these media ids.</summary>
    internal int SelectMediaForShot(params uint[] ids)
    {
        Grid_.SelectedItems.Clear();
        foreach (var row in (Grid_.ItemsSource as IEnumerable<SoundRow>)?.Where(r => ids.Contains(r.MediaId)).GroupBy(r => r.MediaId).Select(g => g.First()) ?? [])
            Grid_.SelectedItems.Add(row);
        return Grid_.SelectedItems.Count;
    }

    /// <summary>
    /// A test build's files packed into one .pak in outRoot, the way every build ships, with its
    /// legend and log beside the pak rather than in it.
    /// </summary>
    private static (bool Ok, string Message, string Pak) TestPack(string work, string outRoot, string name)
    {
        Directory.CreateDirectory(outRoot);
        if (!Directory.Exists(work)) return (false, "nothing was written", null);
        var files = Directory.GetFiles(work, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(work, f)).ToList();
        var mod = files.Where(f => f.Replace('\\', '/').StartsWith("Marvel/", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var f in files.Except(mod)) File.Copy(Path.Combine(work, f), Path.Combine(outRoot, Path.GetFileName(f)), true);
        if (mod.Count == 0) return (false, "no sound was changed", null);
        var outBase = Path.Combine(outRoot, name);
        var (ok, message) = ModPackager.PackagePak(work, mod, outBase);
        // A bundle under this name from an earlier build would load beside the .pak.
        if (ok) foreach (var e in new[] { ".utoc", ".ucas" }) if (File.Exists(outBase + e)) File.Delete(outBase + e);
        return (ok, message, outBase + ".pak");
    }

    /// <summary>
    /// The rows' media no bank holds -- the game plays them from a loose file -- for the rows this
    /// picks. Asked of the bank index, so empty until it is in.
    /// </summary>
    private List<uint> TestLooseIds(Func<SoundRow, bool> which) =>
        Sounds is null || Session?.BankIndexReady != true ? []
        : Sounds.Rows.Where(which)
                .Where(r => !Session.MediaBank.ContainsKey(r.MediaId) && Session.LooseMedia(r.MediaId) is not null)
                .Select(r => r.MediaId).Distinct().ToList();

    // ---- the four buttons ---------------------------------------------------

    /// <summary>
    /// Replace every sound in the picked .bnk files with a spoken number. The whole
    /// character becomes a counting exercise, which is the point: one run names
    /// everything it has clips for.
    /// </summary>
    private async Task TestEverythingAsync()
    {
        if (!await TestReady()) return;

        var scope = TestScope();
        if (scope.Count == 0) { Say("pick a skin or a .bnk in the tree first."); return; }

        // Counted before anything is asked. A question about HOW to number is not worth
        // putting when it turns out there is nothing to number, and the count is what
        // decides whether the question can matter at all.
        //
        // Nullable, because Working hands back default(T) when the work threw and a
        // plain (0, 0) is indistinguishable from a bank that genuinely embeds nothing.
        // Reporting an unreadable .bnk as "embeds no media" sends the author looking in
        // the wrong place, and overwrites the one message that said what actually failed.
        var counted = await Working("Counting media entries", () =>
        {
            var baseBanks = TestModBase();
            var all = TestBankBuilder.CountEntries(Session, scope, baseBanks);
            var biggest = scope.Max(p => TestBankBuilder.CountEntries(Session, [p], baseBanks));
            return ((int All, int Biggest)?)(all, biggest);
        });
        if (counted is null) return;   // Working has already said why.
        var scopeStems = scope.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loose = TestLooseIds(r => scopeStems.Contains(r.Bank));
        if (counted.Value.All == 0 && loose.Count == 0)
        {
            Say("those .bnk files embed no media and stream no loose file, so there is nothing to number.");
            return;
        }

        // The old window kept this as a checkbox that was read whether or not it could
        // matter. One .bnk cannot restart per .bnk, and neither can several that hold
        // the same entries between them, so the question is only put when the answer
        // changes how many clips the build needs.
        var perBank = scope.Count > 1 && counted.Value.Biggest < counted.Value.All && await Ask(
            "Numbered test .bnk",
            $"The selection covers {scope.Count} .bnk files." + Environment.NewLine + Environment.NewLine +
            "Number each of them from the start again? That needs only as many clips as " +
            "the largest .bnk holds, but the same number then means a different sound in " +
            "each file." + Environment.NewLine + Environment.NewLine +
            "Numbering straight through instead makes every number unique across the build.",
            "Restart in each .bnk", "Number straight through");

        var required = perBank ? Math.Max(counted.Value.Biggest, loose.Count) : counted.Value.All + loose.Count;

        var plan = new TestPlan(
            What: "numbered test .bnk",
            Banks: scope,
            Only: null,
            // A mute means silence in every build, so the user's mutes are honoured
            // here too rather than only in the builds that grew out of the mute button.
            Muted: TestMutedIds(),
            Keep: TestKeptNumbers(),
            Required: required,
            SilenceOverflow: true,
            RestartPerBank: perBank,
            ShortfallOk: true,
            Loose: loose);

        var label = PickedBankPaths() is { Count: 1 } one
            ? Path.GetFileName(one[0])
            : PickedSkin?.Label ?? $"{scope.Count} selected .bnk files";

        await TestRun(plan, wems =>
        {
            var have = wems.ByNumber.Count;
            var lowest = wems.ByNumber.Keys.Min();
            var short_ = Math.Max(0, required - have);
            var msg = new System.Text.StringBuilder();
            msg.AppendLine($"Scope: {label}   —   {scope.Count} .bnk(s), {counted.Value.All} media entries" +
                           (loose.Count > 0 ? $" and {loose.Count} sound(s) played from a loose file." : "."));
            msg.AppendLine($"Numbered clips available: {have}  (lowest {lowest}, highest {wems.Max}).");
            msg.AppendLine(perBank
                ? $"Numbering restarts at {lowest} for each .bnk; the largest .bnk needs {required}."
                : $"Numbering runs continuously from {lowest}; that needs {required}.");
            msg.AppendLine();
            if (short_ == 0)
                msg.AppendLine("Every entry gets its own number.");
            else if (wems.SilentPath is not null)
                msg.AppendLine($"{short_} entr(y/ies) go past the last clip and will be SILENCED, " +
                               "so you cannot mistake leftover vanilla audio for a result.");
            else
                msg.AppendLine($"{short_} entr(y/ies) go past the last clip and will KEEP their original " +
                               "audio — add a silent clip to the folder to avoid that.");
            return msg.ToString();
        });
    }

    /// <summary>
    /// Numbers only the selected sounds. Everything else keeps its shipped audio, so
    /// the build stays usable in a real match instead of turning the whole character
    /// into a counting exercise.
    /// </summary>
    private async Task TestSelectionAsync()
    {
        if (!await TestReady()) return;
        var ids = TestSelectedIds();
        if (ids is null) return;

        await TestRun(new TestPlan(
            What: "test .bnk",
            Banks: TestBanksFor(ids),
            Only: ids,
            Muted: TestMutedIds(ids),
            Keep: TestKeptNumbers(ids),
            Required: ids.Count,
            Loose: TestLooseIds(r => ids.Contains(r.MediaId))));
    }

    /// <summary>
    /// Number the selection and silence everything else in the same banks, so only the
    /// sounds you picked can be heard. With several sounds firing at once this is the
    /// fastest way to find out which of them is which.
    /// </summary>
    private async Task TestOnlySelectionAsync()
    {
        if (!await TestReady()) return;
        var ids = TestSelectedIds();
        if (ids is null) return;

        var banks = TestBanksFor(ids);
        var selectedLoose = TestLooseIds(r => ids.Contains(r.MediaId));
        if (banks.Count == 0 && selectedLoose.Count == 0) { Say("none of those sounds live in a .bnk or a loose file we can find."); return; }

        var others = await Working("Reading the .bnk files", () => TestOthersIn(banks, ids));
        if (others is null) return;
        // The loose-only sounds of the same banks are silenced as well: they are the same banks' sounds.
        var stems = banks.Select(Path.GetFileNameWithoutExtension)
                         .Concat(Sounds.Rows.Where(r => selectedLoose.Contains(r.MediaId)).Select(r => r.Bank))
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var otherLoose = TestLooseIds(r => stems.Contains(r.Bank) && !ids.Contains(r.MediaId));
        foreach (var id in otherLoose) others.Add(id);

        // Anything the user has muted stays muted, whether or not it was selected.
        foreach (var id in TestMutedIds()) others.Add(id);

        if (TestClipDirForShot is null && !await Ask("Test only the selection",
                $"Number {ids.Count} selected sound(s) and SILENCE the other {others.Count} " +
                $"in {banks.Count} .bnk(s)." + Environment.NewLine + Environment.NewLine +
                "Only what you picked will be audible.",
                "Build it", "Cancel"))
        {
            Say("nothing built.");
            return;
        }

        await TestRun(new TestPlan(
            What: "test .bnk",
            Banks: banks,
            // Everything is in scope and the silencing is done by the muted set, which
            // is what keeps a legend entry for each silenced sound rather than leaving
            // them out of the build's account of itself.
            Only: null,
            Muted: others,
            Keep: TestKeptNumbers(ids),
            Required: ids.Count,
            Loose: selectedLoose.Concat(otherLoose).ToList()));
    }

    /// <summary>Replaces the selected sounds with silence of the same length and format.</summary>
    private async Task TestSilentSelectionAsync()
    {
        if (!await TestReady()) return;
        var ids = TestSelectedIds();
        if (ids is null) return;

        var tested = SelectedRows.Where(r => r.TestNumber is not null)
                                 .Select(r => r.MediaId).Distinct().Count();
        if (tested > 0 && TestClipDirForShot is null && !await Ask("Already tested",
                $"{tested} of the selected sound(s) carry a test number. Silencing them " +
                "means you will not hear their number in game any more.",
                "Silence them", "Cancel"))
        {
            Say("nothing built.");
            return;
        }

        var banks = TestBanksFor(ids);
        var loose = TestLooseIds(r => ids.Contains(r.MediaId));
        if (banks.Count == 0 && loose.Count == 0) { Say("none of those sounds live in a .bnk or a loose file we can find."); return; }

        var outRoot = BuildFolderForShot ?? await PickFolder("Output folder for the silent build");
        if (outRoot is null) { Say("no output folder, nothing built."); return; }

        var work = Path.Combine(Path.GetTempPath(), "XzoundWave", "silent-" + Guid.NewGuid().ToString("N"));
        (SilentBankBuilder.Result Built, string Pak, string Problem) made;
        try
        {
            made = await Working("Building the silent .bnk", () =>
            {
                var r = SilentBankBuilder.Build(Session, banks, ids, work, Sounds, TestModBase(), loose);
                var (ok, message, pak) = TestPack(work, outRoot, ModBaseName("Silent"));
                return (r, ok ? pak : null, ok ? null : message);
            });
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (made.Built is null) return;
        if (made.Problem is not null) { Say($"silent build: nothing to pack ({made.Problem})."); return; }
        var result = made.Built;
        _lastBuilt.Clear();
        _lastBuilt.Add(made.Pak);
        _lastBuiltRoot = outRoot;

        // Marked the way the tool has always marked them, so the row says what was done
        // to it, in its Notes, and kept in the project like any note.
        var marked = new List<SoundRow>();
        foreach (var row in Sounds.Rows.Where(x => ids.Contains(x.MediaId)))
        {
            if ((row.Notes ?? "").Contains("(Mute)", StringComparison.OrdinalIgnoreCase)) continue;
            row.Notes = string.IsNullOrWhiteSpace(row.Notes) ? "(Mute)" : row.Notes + " (Mute)";
            marked.Add(row);
        }
        RowStateChanged(marked);
        Refresh();

        Say($"silent build: {result.Silenced} sound(s) silenced -- {Path.GetFileName(made.Pak)} in {outRoot}, " +
            (result.NotFound > 0 ? $"{result.NotFound} not found in a bank or a loose file, skipped; " : "") +
            "copy it into ~mods, or Install to ~mods; legend in silent-bank-legend.csv" +
            (marked.Count > 0 ? $"; {marked.Count} row(s) marked (Mute)." : "."));
    }

    // ---- the parts all four share -------------------------------------------

    /// <summary>
    /// A mounted game, rows on screen, and the author's agreement when a loaded mod is
    /// what the build will sit on top of. False means something has already been said
    /// and the caller should stop.
    /// </summary>
    private async Task<bool> TestReady()
    {
        if (Session?.Provider is null) { Say("load the game first, on Setup."); return false; }
        if (Sounds is null || Sounds.Rows.Count == 0)
        {
            Say("pick a skin or a .bnk in the tree first.");
            return false;
        }
        return !await TestBuildOnModRefused();
    }

    /// <summary>
    /// True when the author backed out of building on top of a loaded mod. Worth saying
    /// once: the surprising reading is the other one, where a build quietly ignores the
    /// mod on screen and writes the game's audio instead.
    /// </summary>
    private async Task<bool> TestBuildOnModRefused()
    {
        // Only banks are a base. A loaded loose file is a file of its own, which the build
        // neither carries nor overwrites unless something is staged on the same sound.
        if (Mods.Count == 0) return false;

        // The same key the old window stored, so somebody who has already ticked this
        // away is not asked again by the new one. Never reuse a key for a different
        // message: silencing the old meaning is not agreement to a new one.
        if (Config?.SuppressedWarnings?.Contains("build-uses-a-loaded-mod-as-its-base",
                                                 StringComparer.OrdinalIgnoreCase) == true)
            return false;

        var names = string.Join(Environment.NewLine,
            Mods.All.Select(e => e.BankName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        var go = await Ask("Building on a loaded mod",
            "These .bnk files come from a mod and will be the base for this build:" +
            Environment.NewLine + Environment.NewLine + names + Environment.NewLine + Environment.NewLine +
            "Whatever that mod changed stays in and your work goes on top. Close the mod " +
            "first to build against the game's original audio instead.",
            "Build on the mod", "Cancel");

        if (!go) Say("nothing built — close the mod to build against the game's own audio.");
        return !go;
    }

    /// <summary>
    /// Where a builder reads the .bnk it is about to rewrite: the mod's bytes when one
    /// is loaded for that bank, so a build layers onto it instead of throwing it away.
    /// </summary>
    private BankBase TestModBase() =>
        !Mods.Any ? BankBase.None
        : new BankBase(Mods.All.Select(e => new KeyValuePair<string, byte[]>(e.Stem, e.Bank.Bytes)));

    /// <summary>The .bnk files this build covers: what the tree picked, or the skin's own.</summary>
    private List<string> TestScope()
    {
        var scope = PickedBankPaths();
        if (scope.Count == 0 && PickedSkin is { } skin)
            scope = skin.Banks.Select(b => b.Path).ToList();
        return scope;
    }

    /// <summary>The distinct media ids of the selected rows, or null once it has said why not.</summary>
    private HashSet<uint> TestSelectedIds()
    {
        var rows = SelectedRows;
        if (rows.Count == 0) { Say("select one or more sounds in the list first."); return null; }
        return rows.Select(r => r.MediaId).Distinct().ToHashSet();
    }

    /// <summary>Banks embedding any of these ids, which are the only ones worth rewriting.</summary>
    private List<string> TestBanksFor(IReadOnlyCollection<uint> ids)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
            foreach (var b in Session.BanksForMedia(id)) paths.Add(b);
        foreach (var b in TestModBankPathsFor(ids)) paths.Add(b);
        // Without the global index there is nothing to ask, so fall back to what the
        // tree has picked rather than writing no banks at all.
        if (paths.Count == 0) foreach (var p in TestScope()) paths.Add(p);
        return paths.ToList();
    }

    /// <summary>
    /// The .bnk files a loaded mod embeds these ids in. A mod can add a media id the
    /// game has never shipped, and the game's index cannot know about it: without this
    /// the build would leave the .bnk that actually plays it untouched.
    /// </summary>
    private IEnumerable<string> TestModBankPathsFor(IEnumerable<uint> ids)
    {
        if (!Mods.Any) yield break;
        var want = ids.ToHashSet();
        foreach (var entry in Mods.All)
        {
            if (!entry.Sounds.Rows.Any(r => want.Contains(r.MediaId))) continue;
            if (TestGamePathForStem(entry.Stem) is { } path) yield return path;
        }
    }

    // stem -> the game's own path for that .bnk. Built once; a mod names a .bnk but
    // not where the game keeps it, and the output has to mirror the game's layout.
    private Dictionary<string, string> _testBankPathByStem;

    private string TestGamePathForStem(string stem)
    {
        if (_testBankPathByStem is null)
        {
            _testBankPathByStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Session.Provider.Files.Keys)
            {
                if (!path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)) continue;
                var key = Path.GetFileNameWithoutExtension(path);
                // A vo bank exists once per language; keep the active one, which is the
                // same rule BanksForMedia applies.
                var lang = GameSession.Languages.FirstOrDefault(l =>
                    path.Contains($"/{l}/", StringComparison.OrdinalIgnoreCase));
                if (_testBankPathByStem.ContainsKey(key) && lang is not null &&
                    !lang.Equals(Session.Language, StringComparison.OrdinalIgnoreCase)) continue;
                _testBankPathByStem[key] = path;
            }
        }
        return _testBankPathByStem.GetValueOrDefault(stem);
    }

    /// <summary>
    /// Everything embedded in these banks that is not in <paramref name="keepAudible"/>.
    /// </summary>
    private HashSet<uint> TestOthersIn(IEnumerable<string> banks, IReadOnlySet<uint> keepAudible)
    {
        var baseBanks = TestModBase();
        var others = new HashSet<uint>();
        foreach (var path in banks)
        {
            // Read through the mod overlay, so the bank is the one the author can see.
            // A mod can embed ids the shipped bank never had, and those have to be
            // silenced too or "only the selection" is not only the selection.
            var bytes = baseBanks.Read(Session, path);
            if (bytes is null) continue;
            try
            {
                var didx = BnkBuilder.ReadSections(bytes).FirstOrDefault(s => s.Tag == "DIDX");
                if (didx is null) continue;
                foreach (var entry in BnkBuilder.ReadDidx(didx.Body))
                    if (!keepAudible.Contains(entry.Id)) others.Add(entry.Id);
            }
            catch { }   // one unreadable .bnk must not cost the whole build.
        }
        return others;
    }

    /// <summary>
    /// The numbers already handed out, so a rebuild gives every sound the number it had.
    /// This is the whole reason test numbers are worth writing down: muting one sound
    /// must not renumber the rest, because the author's notes point at numbers.
    /// </summary>
    private Dictionary<uint, int> TestKeptNumbers(IReadOnlySet<uint> ids = null) =>
        Sounds is null ? []
        : Sounds.Rows.Where(r => r.TestNumber is not null && (ids is null || ids.Contains(r.MediaId)))
                     .GroupBy(r => r.MediaId)
                     .ToDictionary(g => g.Key, g => g.First().TestNumber.Value);

    /// <summary>
    /// Muted ids, optionally narrowed to a selection. A mute is a build instruction and
    /// not a test-build one, so every builder is told about them.
    /// </summary>
    private HashSet<uint> TestMutedIds(IReadOnlySet<uint> ids = null) =>
        Sounds is null ? []
        : Sounds.Rows.Where(r => r.Muted && (ids is null || ids.Contains(r.MediaId)))
                     .Select(r => r.MediaId).ToHashSet();

    /// <summary>
    /// Write each assigned number back onto its sound, so hearing "42" in game leads to
    /// the row it came from, and into the project, so the numbers survive closing the app
    /// as they did in the old window. Existing notes are left alone; only the number is
    /// written (Project.RecordTestNumbers).
    /// </summary>
    private int TestNumbersOnto(IEnumerable<TestBankBuilder.Row> legend)
    {
        if (Sounds is null) return 0;
        var n = _project.RecordTestNumbers(legend.Select(e => (e.Number, e.MediaId)), Sounds.Rows);
        RefreshProjectLabel();
        return n;
    }

    // ---- the clips ----------------------------------------------------------

    /// <summary>
    /// Make sure the clip folder holds enough numbered clips, offering to speak them if
    /// not. Null when the author backs out, or when the build cannot work with what is
    /// there.
    /// </summary>
    /// <param name="shortfallOk">
    /// True when entries past the last clip are silenced anyway, so running short costs
    /// coverage rather than correctness. False when they would keep their vanilla audio,
    /// which sounds exactly like a build that did nothing.
    /// </param>
    private async Task<TestBankBuilder.NumberedWems> TestClips(int required, bool shortfallOk)
    {
        if (TestClipDirForShot is not null)
        {
            // The render harness: its own clip folder, spoken without asking, never put in the settings.
            Directory.CreateDirectory(TestClipDirForShot);
            var have = TestBankBuilder.ScanFolder(TestClipDirForShot);
            if (have.ByNumber.Count < required && NumberSpeech.CanSpeak)
                await Working("Speaking the numbers", () => NumberSpeech.Generate(TestClipDirForShot, 0, required - 1, _ => { }));
            return TestBankBuilder.ScanFolder(TestClipDirForShot);
        }
        if (string.IsNullOrWhiteSpace(Config.TestWemDir) || !Directory.Exists(Config.TestWemDir))
        {
            var dir = await PickFolder("Folder of numbered clips  ({number}.wem, plus an optional silent one)");
            if (dir is null) { Say("no clip folder, nothing built."); return null; }
            Config.TestWemDir = dir;
            Config.Save();
        }

        var wems = TestBankBuilder.ScanFolder(Config.TestWemDir);
        if (wems.ByNumber.Count >= required && required > 0) return wems;

        // Not enough clips, or none at all. Offer to speak them rather than sending the
        // author off to find a set: the Windows voice is on every install of it and a
        // Vorbis wem needs no encoder, so this costs nothing to have.
        if (!NumberSpeech.CanSpeak)
        {
            if (shortfallOk && wems.ByNumber.Count > 0)
            {
                Say($"{wems.ByNumber.Count} clip(s) for {required} entr(y/ies); the rest will be silenced.");
                return wems;
            }
            Say($"that needs {required} numbered clip(s) and {Config.TestWemDir} has " +
                $"{wems.ByNumber.Count}. Spoken numbers need Windows, so add clips named " +
                "0.wem, 1.wem … to that folder.");
            return null;
        }

        var speak = await Ask("Generate spoken numbers",
            $"This needs {required} numbered clip(s); the folder has {wems.ByNumber.Count}." +
            Environment.NewLine + Environment.NewLine +
            $"Generate 0-{required - 1} now with the Windows speech voice? " +
            $"About {required * 0.05:0} second(s)." + Environment.NewLine + Environment.NewLine +
            "Output: VORBIS — the same format the game ships. No Wwise install needed.",
            "Generate them",
            shortfallOk && wems.ByNumber.Count > 0 ? "Silence the rest" : "Cancel");

        if (!speak)
        {
            if (shortfallOk && wems.ByNumber.Count > 0) return wems;
            Say("nothing built.");
            return null;
        }

        // Generation reports its progress, and the status line belongs to the drawing
        // thread while this is running on another one.
        await Working("Speaking the numbers", () =>
            NumberSpeech.Generate(Config.TestWemDir, 0, required - 1,
                                  p => Dispatcher.UIThread.Post(() => Say(p))));

        wems = TestBankBuilder.ScanFolder(Config.TestWemDir);
        if (wems.ByNumber.Count == 0)
        {
            Say($"no files named like 0.wem, 1.wem … in {Config.TestWemDir}, and nothing was generated.");
            return null;
        }

        // Generation can fail partway — no voice installed, the folder read-only, COM
        // refusing on the tenth clip — and Working turns that into a status line rather
        // than an exception, so the shortfall is the only thing left to notice it by.
        // Building anyway would leave the uncovered entries playing their shipped audio,
        // which is indistinguishable from a build that did nothing at all.
        if (!shortfallOk && wems.ByNumber.Count < required)
        {
            Say($"{wems.ByNumber.Count} numbered clip(s) in {Config.TestWemDir} for " +
                $"{required} sound(s) — nothing built, because the ones past the last clip " +
                "would keep their normal audio and read as a build that did nothing.");
            return null;
        }
        return wems;
    }
}
