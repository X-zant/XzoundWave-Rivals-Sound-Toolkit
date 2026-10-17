using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;

namespace MRAudioKit.UI;

/// <summary>
/// The Mods tab: somebody else's .bnk files, standing in front of the game's own.
///
/// Everything here works on the whole mod SET rather than on one opened bank. Three
/// mods loaded at once is the ordinary case, and the only rule that matters is the
/// one the game itself follows: a .bnk belongs to whoever claimed it last, and what
/// that mod supplies wins over the shipped copy.
/// </summary>
public partial class MainWindow
{
    /// <summary>The grouping row that holds loaded .bnk files the game does not ship.</summary>
    private const string ModStandInLabel = "Opened mod .bnk files";

    private IEnumerable<ToolButton> ModTools()
    {
        yield return new ToolButton("Open mod", null, Key: true,
            Hint: "Open a mod to see what it changes: its sounds show in the list, tagged MODDED or NEW, and they are " +
                  "what builds sit on top of. Dropping a mod on the window does the same.", Menu:
            [
                new(".bnk / .pak / .utoc file…", () => _ = ModOpenAsync(),
                    Hint: "Open .bnk files, or a mod .pak to pick them from. What a mod supplies wins over the game's copy of that .bnk."),
                new("Unpacked mod folder…", () => _ = ModOpenFolderAsync(),
                    Hint: "Open an unpacked mod: the .bnk files and loose Media wems under a folder laid out as the " +
                          "game's (Marvel/Content/WwiseAudio/...)."),
            ]);

        yield return new ToolButton("Close mods", () => _ = ModCloseAsync(),
            Hint: "Drop every loaded mod and go back to the game's own .bnk files.");

        var onlyChanged = (_settings.HiddenRowClasses ?? [.. RowClasses.HiddenByDefault]).Contains("unchanged");
        yield return new ToolButton(onlyChanged ? "Showing only changed ✓" : "Show only changed", ToggleOnlyChanged,
            Hint: "Hide what an opened mod left as the game shipped it. The same switch as Settings > Rows.");

        yield return new ToolButton("Mod tools", null,
            Hint: "Work on mod files: bring one up to date, combine two, take one apart, sort wems, shrink one.", Menu:
            [
                new("Update mod…", () => _ = ModUpdateAsync(),
                    Hint: "Bring a mod made for an older patch onto the game's current .bnk files, as a .pak: the game's " +
                          "banks as they are now, carrying only the audio the mod changed, each line placed by its Sound."),
                new("Merge mods…", () => _ = ModMergeAsync(),
                    Hint: "Combine two mods into one .pak. The priority one wins any sound both of them changed."),
                ToolButton.Separator,
                new("Extract modded wems…", () => _ = ModExtractAsync(),
                    Hint: "Write out the sounds a loaded mod changed, and the game's originals of them beside, " +
                          "in folders named after the character, skin and .bnk."),
                new("Organize wems…", () => _ = ModOrganizeAsync(),
                    Hint: "Copy a folder of finished wems into one folder per mod: by a SoundKit rule list " +
                          "(0-WEM-FOLDER-NAME-LIST.txt), matched against what the game says plays each wem, or by the " +
                          "character, skin and bank that play it. Nothing is moved or deleted."),
                ToolButton.Separator,
                new("Recompress…", () => _ = ModRecompressAsync(),
                    Hint: "Re-encode PCM audio to Vorbis, the way the game's own is: in .bnk files, loose .wem files, " +
                          "or a whole mod .pak, which comes back as a .pak of the same name with every file it ships."),
            ]);
    }

    // ---- opening somebody else's bank ---------------------------------------

    private async Task ModOpenAsync()
    {
        if (StillReopeningMods()) return;
        // Without the game mounted there is nothing to compare against, and every row
        // in the mod would read as NEW -- which is the one answer that looks right and
        // is not.
        if (Session?.Provider is null)
        {
            Say("load the game first — a mod's rows are read against the game's own .bnk files.");
            return;
        }

        var files = await PickFiles("Open .bnk files, or a mod .pak to pick them from",
                                    ".bnk or mod .pak", "*.bnk", "*.pak", "*.utoc");
        if (files.Count == 0) return;
        await ModOpenManyAsync(OneOpenPerContainer(files).Select(f => new ModOpen(f, null)).ToList());
    }

    /// <summary>
    /// One open of a mod this session: the file, and the .bnk files it put into the set, by
    /// <see cref="BankKey"/> (null: ask, as a pick or a drop does). Kept in the order they
    /// happened, which is the order a project reopens them in: the one opened last wins a
    /// .bnk or a loose file two of them ship, so replaying the opens in order gives back the
    /// same winners.
    /// </summary>
    private sealed record ModOpen(string Path, IReadOnlyCollection<string> Banks);

    private readonly List<ModOpen> _modOpens = [];

    /// <summary>
    /// Which bank of a mod this is: its path inside the mod, language folder included, or its
    /// name when it never said. A voice mod ships one bnk_vo_ per language under the same file
    /// name, so the name alone would bring every language's copy back.
    /// </summary>
    private static string BankKey(ModBank.Bank bank) => bank.MountedAt ?? bank.Name;

    /// <summary>
    /// Record an open that put something into the set. Each open is its own entry, because a
    /// file opened again for one more .bnk did not take back the ones another mod has since
    /// claimed. An earlier open of the same file is dropped only when this one took everything
    /// it took: replaying this one re-takes all of it, later, which is what happened.
    /// </summary>
    private void NoteModOpened(string path, IReadOnlyCollection<string> banks)
    {
        _modOpens.RemoveAll(m => AppPaths.SamePath(m.Path, path) &&
                                 (m.Banks ?? []).All(b => banks.Contains(b, StringComparer.OrdinalIgnoreCase)));
        _modOpens.Add(new ModOpen(path, banks));
        if (_restoringMods > 0) return;
        _project.Touch();
        RefreshProjectLabel();
    }

    /// <summary>Above zero while a project's mods are being reopened, which is not an edit of the project.</summary>
    private int _restoringMods;

    /// <summary>
    /// A mod's .pak, .ucas and .utoc are one mod: opening any of them mounts all three. A drop or a
    /// pick of the usual three files is therefore one open, not two of the same mod.
    /// </summary>
    private static List<string> OneOpenPerContainer(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(AppPaths.Comparer);
        return paths.Where(p => seen.Add(ModBank.IsPak(p)
                                    ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(p) ?? "",
                                                             System.IO.Path.GetFileNameWithoutExtension(p))
                                    : p))
                    .ToList();
    }

    /// <summary>
    /// Open several mod files and say once what they did together. The Open mod pick, a drop
    /// of several files and a project's own list all come through here.
    /// </summary>
    /// <param name="carryOn">Asked before each file: false stops, when the project being restored is no longer the open one.</param>
    /// <returns>The files that opened nothing (a failure, an empty mod, a pick backed out of).</returns>
    private async Task<List<string>> ModOpenManyAsync(IReadOnlyList<ModOpen> opens, Func<bool> carryOn = null)
    {
        // Two mods at once is the ordinary way a stack gets loaded, and it is also
        // where they are likeliest to claim the same .bnk. Each file reports itself on
        // the status line, but the next file's report overwrites it: a displaced .bnk
        // named in a line nobody can still read has not been named. So the whole pick
        // is collected and said once, at the end.
        var taken = new List<ModSet.Entry>();
        var displaced = new List<ModSet.Entry>();
        var takenLoose = new List<ModSet.Loose>();
        var failed = new List<string>();
        var opened = 0;

        foreach (var open in opens)
        {
            if (carryOn is not null && !carryOn()) break;
            if (await ModOpenOneAsync(open.Path, taken, displaced, takenLoose, open.Banks)) opened++;
            else failed.Add(open.Path);
        }

        // One file has already said everything there is to say about itself.
        if (opens.Count == 1) return failed;
        if (taken.Count + takenLoose.Count == 0)
        {
            // Several files and not one opened: each one's own line was written over by the
            // next file's progress, so they are named here, together.
            if (failed.Count > 0)
                Say($"none of the {failed.Count} file(s) opened: " +
                    string.Join(", ", failed.Select(System.IO.Path.GetFileName)) +
                    ". Open them one at a time to see why each did not.");
            return failed;
        }

        // By stem, because two files in one pick can claim the same .bnk and only the
        // last one is still in the set: counting both would double what it changed.
        var stems = taken.Select(t => t.Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var live = Mods.All.Where(e => stems.Contains(e.Stem)).ToList();

        // Selecting nothing would empty the list under a line claiming a load, so the
        // last file's selection stands if for any reason these have no node.
        var nodes = ModNodesFor(stems.Concat(LooseStems(takenLoose)));
        if (nodes.Count > 0)
        {
            await ModWarmAsync(nodes);
            ModSelect(nodes);
        }

        // Same distinction the single-file line makes: a mod whose banks the game has
        // no counterpart for cannot be diffed at all, and "0 changed" would read there
        // as "these mods do nothing".
        var compared = live.Any(e => e.Sounds.Rows.Any(r => r.ModTag is not null));

        // A file that opened nothing is named here: its own failure line was written over by
        // the next file's progress, and this line is the one that stays.
        Say($"loaded {live.Count} .bnk" + (takenLoose.Count > 0 ? $" and {takenLoose.Count} loose wem(s)" : "") +
            $" from {opened} file(s) — " +
            (compared ? $"{live.Sum(e => e.Changed):N0} sound(s) changed from the shipped .bnk files"
                      : "no shipped .bnk to compare against") +
            (displaced.Count > 0
                ? "  —  replaced " + string.Join(", ", displaced.Select(d => $"{d.BankName} from {d.SourceName}"))
                : "") +
            (failed.Count > 0 ? "  —  not opened: " + string.Join(", ", failed.Select(System.IO.Path.GetFileName)) : "") +
            ".  Builds now use these as their base.");
        return failed;
    }

    /// <summary>
    /// Open a .bnk directly, or mount a .pak and ask which of the banks inside it to
    /// load. More than one can be loaded at a time; each overlays the game's copy of
    /// that .bnk and nothing else.
    /// </summary>
    /// <param name="taken">Collects what this file put into the set, for the caller's report.</param>
    /// <param name="displaced">Collects what that pushed aside, for the same reason.</param>
    /// <param name="wanted">
    /// The .bnk files to take, by <see cref="BankKey"/>, when they are already known -- a
    /// project reopening what it had -- so a .pak of several is not asked about again. Null asks.
    /// </param>
    /// <returns>Whether anything went into the set: a .bnk with audio in it, or a loose file.</returns>
    private async Task<bool> ModOpenOneAsync(string path, List<ModSet.Entry> taken,
                                             List<ModSet.Entry> displaced, List<ModSet.Loose> takenLoose = null,
                                             IReadOnlyCollection<string> wanted = null)
    {
        // A picked folder comes back ending in a separator, which leaves it no file name.
        path = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(path);
        // With its loose Media files: a mod can be nothing else (the audio of sounds no bank
        // holds, which the game reads by path), and one that ships banks too was otherwise
        // shown without half of what it changes.
        var opened = await Working($"Opening {name}", () => ModBank.Open(path, Config, withMedia: true));
        if (opened is null) return false;   // Working has already said why

        if (opened.Banks.Count == 0 && opened.Media.Count == 0)
        {
            Say($"{name} holds no .bnk files and no loose Media wems" +
                (opened.Skipped.Count > 0
                    ? $" — {opened.Skipped.Count} container(s) could not be opened."
                    : "."));
            return false;
        }

        // A .pak usually holds several banks. Guessing which one was meant is how
        // someone ends up looking at a bank they did not open. Asked before the loose
        // files are taken, so backing out of it loads nothing at all.
        List<ModBank.Bank> banks = [];
        if (wanted is not null)
        {
            // Known already, so nothing to ask. One that is no longer in the file is said
            // rather than dropped quietly; a mod updated since is the usual reason.
            banks = opened.Banks.Where(b => wanted.Contains(BankKey(b), StringComparer.OrdinalIgnoreCase)).ToList();
            var gone = wanted.Where(w => !opened.Banks.Any(b => BankKey(b).Equals(w, StringComparison.OrdinalIgnoreCase))).ToList();
            if (gone.Count > 0)
            {
                _modsNotFound.AddRange(gone.Select(g => $"{Path.GetFileName(g)} in {name}"));
                _modsBanksGone.Add(path);
            }
            if (banks.Count == 0 && opened.Media.Count == 0) return false;
        }
        else if (opened.Banks.Count > 0)
        {
            banks = opened.Banks.Count == 1
                ? opened.Banks
                : await ModPickBanksAsync(opened.Banks, name);
            if (banks is null || banks.Count == 0) return false;
        }

        var loose = await ModTakeLooseAsync(opened.Media, name);
        if (loose is null) return false;   // Working has already said why
        takenLoose?.AddRange(loose);

        // What this file put into the set, as opposed to what it was asked for: a .bnk with no
        // embedded audio is skipped, and a read can fail part way.
        var mine = new List<ModSet.Entry>();
        if (banks.Count > 0)
            await ModTakeAsync(banks, path, opened.Skipped, opened.Banks.Count, mine, displaced, loose);
        else
            await ModShowLooseAsync(loose, name, opened.Skipped);
        taken?.AddRange(mine);

        if (mine.Count == 0 && loose.Count == 0) return false;
        NoteModOpened(path, mine.Select(e => BankKey(e.Bank)).ToList());
        return true;
    }

    /// <summary>What a project's mods asked for that is no longer there, collected while they reopen.</summary>
    private readonly List<string> _modsNotFound = [];

    /// <summary>
    /// The files among them that opened but lacked some of their banks: reported by the banks,
    /// not again as a file that could not be opened, which would read as a broken file.
    /// </summary>
    private readonly HashSet<string> _modsBanksGone = new(AppPaths.Comparer);

    /// <summary>What reopening a list of mods came to.</summary>
    /// <param name="Missing">Files that are not there any more.</param>
    /// <param name="Failed">Files that are there and opened nothing.</param>
    /// <param name="BanksGone">.bnk files no longer in a file that did open.</param>
    private sealed record ModRestored(List<string> Missing, List<string> Failed, List<string> BanksGone, int Opened)
    {
        public bool Clean => Missing.Count + Failed.Count + BanksGone.Count == 0;

        /// <summary>Every problem, one per line, for a dialog.</summary>
        public IEnumerable<string> Lines =>
            Missing.Select(m => $"{m}  (not found)")
                   .Concat(Failed.Select(f => $"{f}  (could not be opened)"))
                   .Concat(BanksGone.Select(b => $"{b}  (no longer in the mod)"));
    }

    /// <summary>
    /// Make the open mods exactly these, in this order: what a project had open when it was
    /// saved, or what was open before a reload of the game closed it. Each is taken as it was,
    /// the same .bnk files and no question asked.
    /// </summary>
    /// <param name="carryOn">False once another project has replaced the one being restored.</param>
    private async Task<ModRestored> ModRestoreAsync(IReadOnlyList<ModOpen> opens, Func<bool> carryOn = null)
    {
        _modsNotFound.Clear();
        _modsBanksGone.Clear();
        _modsAfterLoad = null;   // realised here, whatever was parked
        if (Mods.Any || _modOpens.Count > 0)
        {
            Mods.Clear();
            _modOpens.Clear();
            ModStandIns();
            ModMarkTree(expand: false);
        }
        bool Here(ModOpen o) => File.Exists(o.Path) || Directory.Exists(o.Path);
        var missing = opens.Where(o => !Here(o)).Select(o => o.Path).ToList();
        var present = opens.Where(Here).ToList();
        if (present.Count == 0) return new ModRestored(missing, [], [], 0);

        _restoringMods++;
        List<string> failed;
        try { failed = await ModOpenManyAsync(present, carryOn); }
        finally { _restoringMods--; }
        return new ModRestored(missing, [.. failed.Where(f => !_modsBanksGone.Contains(Path.TrimEndingDirectorySeparator(f)))],
                               [.. _modsNotFound], present.Count - failed.Count);
    }

    /// <summary>"Open mod folder…": an unpacked mod, read where it sits.</summary>
    private async Task ModOpenFolderAsync()
    {
        if (StillReopeningMods()) return;
        if (Session?.Provider is null)
        {
            Say("load the game first — a mod's rows are read against the game's own .bnk files.");
            return;
        }
        var folder = await PickFolder("Open an unpacked mod folder");
        if (folder is null) return;
        await ModOpenOneAsync(folder, [], []);
    }

    /// <summary>
    /// Take a mod's loose Media files into the set, each compared with the game's file at the
    /// same path and placed under the bank that plays it. Null when the reading failed.
    /// </summary>
    private async Task<List<ModSet.Loose>> ModTakeLooseAsync(List<ModBank.LooseMedia> media, string name)
    {
        if (media.Count == 0) return [];
        var session = Session;
        // Read off this thread -- the first file whose bank is not in the embedded index reads
        // every bank once to learn which Sound or music track plays what -- and put in the set
        // on it, where everything that reads the set runs.
        var read = await Working($"Reading {media.Count} loose wem(s) from {name} and finding the sound each one is", () =>
            media.Select(m => ModSet.ReadLoose(session, m, name)).Where(l => l is not null).ToList());
        if (read is null) return null;
        foreach (var loose in read) Mods.PutLoose(loose, out _);
        return read;
    }

    /// <summary>
    /// A loose-file load in words: what changed, and what of it no row can show -- a file the
    /// game never reads, one for another language, one nothing in the game plays.
    /// </summary>
    private string LooseSummary(IReadOnlyCollection<ModSet.Loose> loose)
    {
        if (loose.Count == 0) return "";
        var language = Session?.Language;
        var changed = loose.Count(l => l.Changed);
        var added = loose.Count(l => l.Tag == "NEW" && l.Reaches);
        var same = loose.Count - changed;
        var unread = loose.Count(l => l.Changed && !l.Reaches && l.Stem is not null);
        var foreign = loose.Where(l => l.Changed && l.Reaches && !l.AppliesIn(language)).ToList();
        var unplayed = loose.Count(l => l.Changed && l.Stem is null);
        return $"{loose.Count} loose wem(s), {changed} changed from the game's" +
               (added > 0 ? $" ({added} where the game streams a file it does not ship)" : "") +
               (same > 0 ? $", {same} the game's own bytes" : "") +
               (unread > 0 ? $", {unread} at a path the game does not read for that sound, so they change nothing" : "") +
               (foreign.Count > 0
                   ? $", {foreign.Count} for {string.Join("/", foreign.Select(l => l.Language ?? "the shared folder").Distinct())}, " +
                     "which this language does not play -- switch the language in Setup to see them"
                   : "") +
               (unplayed > 0 ? $", {unplayed} that nothing in the game plays, so no row shows them" : "");
    }

    /// <summary>The banks whose rows these loose files change, in the language on screen.</summary>
    private IEnumerable<string> LooseStems(IEnumerable<ModSet.Loose> loose) =>
        loose.Where(l => l.AppliesIn(Session?.Language) && l.Stem is not null).Select(l => l.Stem);

    /// <summary>A mod of loose Media files only: mark the banks that play them and show those.</summary>
    private async Task ModShowLooseAsync(List<ModSet.Loose> loose, string name, List<string> skipped)
    {
        ModStandIns();
        ModMarkTree(expand: true);

        var nodes = ModNodesFor(LooseStems(loose));
        if (nodes.Count > 0)
        {
            await ModWarmAsync(nodes);
            ModSelect(nodes);
        }

        Say($"loaded {name}: {(loose.Count == 0 ? "no loose wem named by a media id" : LooseSummary(loose))}" +
            (skipped is { Count: > 0 } ? $"  —  {skipped.Count} container(s) skipped" : "") +
            (nodes.Count > 0 ? $".  Showing the {nodes.Count} .bnk that play them." : "."));
    }

    /// <summary>
    /// Take the chosen banks into the mod set, name anything this displaces, and show
    /// them.
    /// </summary>
    private async Task ModTakeAsync(List<ModBank.Bank> banks, string source,
                                    List<string> skipped, int siblings,
                                    List<ModSet.Entry> taken, List<ModSet.Entry> pushedAside,
                                    List<ModSet.Loose> loose = null)
    {
        loose ??= [];
        var read = await Working($"Reading {banks.Count} .bnk from {Path.GetFileName(source)}", () =>
        {
            var empty = new List<string>();
            var loaded = new List<ModSet.Entry>();
            var displaced = new List<ModSet.Entry>();

            foreach (var bank in banks)
            {
                // A bank with no embedded media has nothing to show: its audio lives in
                // loose Media/ files, so loading it would put an empty list on screen.
                if (ModBank.ToSounds(Session, bank).Rows.Count == 0) { empty.Add(bank.Name); continue; }

                // Only one mod can own a .bnk, the same way only one wins in game, and
                // the newest loaded is the one that does. The loser is named below
                // rather than dropped quietly: both were asked for and only one is got.
                loaded.Add(Mods.Add(Session, bank, source, out var replaced));
                if (replaced is not null) displaced.Add(replaced);
            }
            return (empty, loaded, displaced);
        });

        if (read.loaded is null)
        {
            // Working has already said why. Banks taken before it threw are in the set
            // all the same, and a mod the set holds but the tree does not show is a mod
            // that silently changes what the next build writes.
            if (Mods.Any) { ModStandIns(); ModMarkTree(expand: true); }
            return;
        }
        var (empty, loaded, displaced) = read;

        if (loaded.Count == 0 && loose.Count > 0)
        {
            // Its banks embed nothing, so its sounds are the loose files: show those.
            await ModShowLooseAsync(loose, Path.GetFileName(source), skipped);
            return;
        }
        if (loaded.Count == 0)
        {
            Say($"nothing to show in {string.Join(", ", empty)} — no embedded media. " +
                "A .bnk that only carries events keeps its audio in loose Media/ files." +
                (skipped is { Count: > 0 }
                    ? $"  {skipped.Count} container(s) could not be opened at all."
                    : ""));
            return;
        }

        taken.AddRange(loaded);
        pushedAside.AddRange(displaced);

        // Marking rebinds the tree, which drops the selection, so it has to happen
        // before anything is selected rather than after.
        ModStandIns();
        ModMarkTree(expand: true);

        var nodes = ModNodesFor(loaded.Select(l => l.Stem).Concat(LooseStems(loose)));
        await ModWarmAsync(nodes);
        ModSelect(nodes);

        var changed = loaded.Sum(l => l.Changed);
        // Zero changed means two different things. A mod whose banks the game has no
        // counterpart for cannot be diffed at all, and saying "0 changed" there would
        // read as "this mod does nothing".
        var compared = loaded.Any(l => l.Sounds.Rows.Any(r => r.ModTag is not null));

        Say($"loaded {loaded.Count} .bnk from {Path.GetFileName(source)} — " +
            (compared ? $"{changed:N0} sound(s) changed from the shipped .bnk files"
                      : "no shipped .bnk to compare against") +
            (siblings > loaded.Count ? $"  ({siblings} .bnk files in that .pak)" : "") +
            (displaced.Count > 0
                ? "  —  replaced " + string.Join(", ", displaced.Select(d => $"{d.BankName} from {d.SourceName}"))
                : "") +
            (empty.Count > 0 ? $"  —  {empty.Count} with no embedded media skipped" : "") +
            (loose.Count > 0 ? "  —  " + LooseSummary(loose) : "") +
            (skipped is { Count: > 0 } ? $"  —  {skipped.Count} container(s) skipped" : "") +
            ".  Builds now use these as their base.");

        // The one dialog this tab earns. Silently dropping a container someone expected
        // to see is worse than an extra click: they would conclude the mod simply had
        // no audio in it.
        if (skipped is { Count: > 0 })
            await Tell("Some containers skipped",
                "These could not be opened and were left out:" + Environment.NewLine +
                string.Join(Environment.NewLine, skipped) + Environment.NewLine + Environment.NewLine +
                "Everything else loaded normally.");
    }

    /// <summary>A pak usually holds several banks; ask which ones rather than guessing.</summary>
    private async Task<List<ModBank.Bank>> ModPickBanksAsync(List<ModBank.Bank> banks, string source)
    {
        if (PickBanksForShot is not null) return PickBanksForShot(banks);
        List<ModBank.Bank> answer = null;

        var list = new ListBox
        {
            ItemsSource = banks,
            SelectionMode = SelectionMode.Multiple,
            // A record's own ToString would print the byte array, so the row says what
            // the file is called and nothing else.
            ItemTemplate = new FuncDataTemplate<ModBank.Bank>(
                (bank, _) => new TextBlock { Text = bank.Name }, true),
            SelectedIndex = 0,
        };

        var all = new Button { Content = "Select all", Width = 96 };
        var ok = new Button { Content = "Open", Width = 96, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 96, IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(all);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var head = new TextBlock
        {
            Text = $"{banks.Count} .bnk files in {source} — Ctrl+click or Shift+click to take several",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
        };

        var body = new DockPanel { Margin = new Avalonia.Thickness(14) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(head);
        body.Children.Add(buttons);
        body.Children.Add(list);

        var dialog = new Window
        {
            Title = "Pick .bnk files",
            Content = body,
            Width = 520,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        List<ModBank.Bank> Taken() => list.SelectedItems.OfType<ModBank.Bank>().ToList();

        all.Click += (_, _) => list.SelectAll();
        ok.Click += (_, _) => { answer = Taken(); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItems is { Count: > 0 }) { answer = Taken(); dialog.Close(); }
        };

        await dialog.ShowDialog(this);
        return answer;
    }

    // ---- closing ------------------------------------------------------------

    private async Task ModCloseAsync()
    {
        if (StillReopeningMods()) return;
        if (!Mods.Any)
        {
            // Nothing in the set, but the project may still list some (parked for a game that
            // is not loaded yet): closing means it stops listing them too.
            if (_modOpens.Count + (_modsAfterLoad?.Count ?? 0) > 0)
            {
                _modOpens.Clear();
                _modsAfterLoad = null;
                _project.Touch();
                RefreshProjectLabel();
                Say("closed the project's mods; none were open yet.");
                return;
            }
            Say("no mods are open.");
            return;
        }

        var sources = Mods.Sources.ToList();
        Mods.Clear();
        // The open mods are the project's: closing them is an edit of it.
        _modOpens.Clear();
        _modsAfterLoad = null;
        _project.Touch();
        RefreshProjectLabel();

        var keep = Tree.SelectedItems.OfType<Node>().ToList();

        ModStandIns();                  // Mods is empty now, so the stand-ins go
        ModMarkTree(expand: false);

        // A stand-in node had nothing behind it but its mod, so it is gone from the
        // tree; keeping it selected would select an object nothing points at.
        var live = ModTreeNodes().ToHashSet();
        keep = keep.Where(live.Contains).ToList();

        if (keep.Count == 0)
        {
            // Nothing left to show. The rows on screen came from the mod, so they go
            // with it rather than lingering as a list of sounds nothing is reading.
            Grid_.ItemsSource = null;
            _sounds = null;
            Refresh();
            Say($"closed {sources.Count} mod(s) — {string.Join(", ", sources)}.");
            return;
        }

        await ModWarmAsync(keep);
        ModSelect(keep);
        Say("closed — back to the game's own .bnk files.");
    }

    // ---- the tree ------------------------------------------------------------

    /// <summary>
    /// Put the real counts on the tree: what each loaded .bnk actually changes, rolled
    /// up to its skin and its character, so a collapsed hero still says how much is in
    /// there.
    /// </summary>
    private void ModMarkTree(bool expand)
    {
        // Clear first. PretendModded only WRITES the counts it is handed: a .bnk whose
        // stem is not in the dictionary is skipped, not zeroed. So a closed mod would
        // leave its fill and its number on the .bnk row while the skin and character
        // above it — which are recomputed from the sum — correctly went back to
        // nothing, and the tree would disagree with itself.
        foreach (var node in ModTreeNodes()) node.Changed = 0;

        // A bank's own changes and the loose files it streams, added: both are sounds of
        // that bank a mod changes.
        // By media id, so a sound a mod changes both ways -- a prefetch stub in the bank and
        // the whole clip loose -- is one sound.
        var ids = Mods.LooseChangedByStem(Session?.Language);
        foreach (var e in Mods.All)
        {
            if (!ids.TryGetValue(e.Stem, out var set)) ids[e.Stem] = set = [];
            set.UnionWith(e.Sounds.Rows.Where(r => r.ModTag is "MODDED" or "NEW").Select(r => r.MediaId));
        }
        PretendModded(ids.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.OrdinalIgnoreCase), expand);
    }

    /// <summary>
    /// A home in the tree for loaded .bnk files the game does not ship.
    ///
    /// The tree is built from the game, so a brand-new .bnk has no node -- and a node
    /// is the only thing that can be selected, which is the only way rows reach the
    /// list. Without this such a mod would load and then be invisible, which reads as
    /// the mod having done nothing.
    /// </summary>
    private void ModStandIns()
    {
        foreach (var old in _roots.Where(ModIsStandInGroup).ToList()) _roots.Remove(old);

        // Read AFTER the old stand-ins are gone: a bank that only had one is not a
        // bank the game ships.
        var known = ModTreeNodes().Where(n => n.Bank is not null)
                                  .Select(n => Path.GetFileNameWithoutExtension(n.Bank))
                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = Mods.All.Where(e => !known.Contains(e.Stem)).ToList();
        if (orphans.Count == 0) return;

        // Three levels deep, like the rest of the tree: the marking walks roots, their
        // children and their grandchildren, so a bank hung any shallower never gets a
        // count put on it.
        var bySource = orphans
            .GroupBy(e => e.SourceName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Node
            {
                What = Node.Kind.Group,
                Label = g.Key,
                Expanded = true,
                Children = g.Select(e => new Node
                {
                    What = Node.Kind.Bank,
                    Label = e.BankName,
                    Bank = e.BankName,
                }).ToList(),
            }).ToList();

        _roots.Add(new Node
        {
            What = Node.Kind.Group,
            Label = $"{ModStandInLabel}   ({orphans.Count})",
            Expanded = true,
            Children = bySource,
        });
    }

    private static bool ModIsStandInGroup(Node node) =>
        node.What == Node.Kind.Group &&
        node.Label.StartsWith(ModStandInLabel, StringComparison.Ordinal);

    /// <summary>Point the selection at these .bnk files, wherever in the tree they sit.</summary>
    private void ModSelect(List<Node> nodes)
    {
        Tree.SelectedItems.Clear();
        foreach (var node in nodes) Tree.SelectedItems.Add(node);
    }

    private List<Node> ModNodesFor(IEnumerable<string> stems)
    {
        var want = stems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ModTreeNodes()
            .Where(n => n.Bank is not null && want.Contains(Path.GetFileNameWithoutExtension(n.Bank)))
            .ToList();
    }

    /// <summary>Every node in the tree, however deeply nested.</summary>
    private IEnumerable<Node> ModTreeNodes()
    {
        var stack = new Stack<Node>(_roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Children) stack.Push(child);
        }
    }

    /// <summary>
    /// Read the skins these nodes belong to before the selection is set.
    ///
    /// Selecting a node makes the window read its skin off the drawing thread and
    /// report what it found when it lands -- which would overwrite whatever this load
    /// or close has to say about itself. Reading first leaves that handler nothing to
    /// wait for, so the report is the last thing said rather than the first.
    /// </summary>
    private async Task ModWarmAsync(List<Node> nodes)
    {
        var skins = nodes.Where(n => n.Skin is not null)
                         .Select(n => (n.Skin, n.CharId))
                         .DistinctBy(x => x.Skin.SkinId)
                         .Where(x => !_skinCache.ContainsKey(x.Skin.SkinId))
                         .ToList();
        if (skins.Count == 0) return;

        await Working($"Reading {skins.Count} skin(s)", () =>
        {
            foreach (var (skin, charId) in skins)
                // One unreadable skin must not sink the load. The mod's own rows do not
                // come from here; only the game's, which it is being shown beside.
                try { _skinCache[skin.SkinId] = SoundIndex.Build(Session, skin, charId ?? "", _ => { }); }
                catch { }
            return true;
        });
    }

    // ---- merging two mods ----------------------------------------------------

    /// <summary>
    /// Combine two mods. Each is diffed against the shipped bank so only what its
    /// author actually replaced is carried over, and the priority mod wins any sound
    /// both of them changed.
    /// </summary>
    /// <summary>"Update mod…", pressed by the render harness: this mod into this folder.</summary>
    internal Task UpdateForShot(string mod, string outRoot)
    {
        MergePicksForShot = [mod, null];
        BuildFolderForShot = outRoot;
        return ModUpdateAsync();
    }

    /// <summary>
    /// A mod made for an older patch, rebuilt on the game's current banks (ModUpdate, the
    /// --updatemod verb) and packed: the current banks with only the mod's audio carried
    /// onto them, the report beside.
    /// </summary>
    private async Task ModUpdateAsync()
    {
        if (Session?.Provider is null) { Say("load the game first — a mod is brought onto the game's current .bnk files."); return; }
        if (!Session.BankIndexReady) { Say("still reading .bnk files -- give it a moment, then update."); return; }
        var mod = MergePicksForShot?[0] ?? await PickFile("Mod to update (.pak, .bnk or .utoc)", "Mod", "*.pak", "*.bnk", "*.utoc");
        if (mod is null) return;
        var outRoot = BuildFolderForShot ?? await PickFolder("Output folder for the updated mod");
        if (outRoot is null) return;
        Directory.CreateDirectory(outRoot);

        var stem = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(mod), @"_\d+_P$", "",
                                                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var outputBase = Path.Combine(outRoot, ModSafeName(stem).Replace(' ', '_') + "_updated_9999999_P");
        var history = await Working("reading the game's history", () => GameHistory.Load());
        string madeFor = null;
        ModUpdate.Result result;
        while (true)
        {
            var work = Path.Combine(Path.GetTempPath(), "XzoundWave", "update-" + Guid.NewGuid().ToString("N"));
            result = await UpdateOnceAsync(mod, work, outRoot, outputBase, history, madeFor);
            // The records disagree about some of the mod's files: which version was it made for?
            if (result is null || result.Versions.Count < 2 || madeFor is not null || MergePicksForShot is not null) break;
            madeFor = await PickOneAsync("Which version was this mod made for?",
                $"{Path.GetFileName(mod)} has loose files whose ids meant different sounds in these versions of the game. " +
                "Pick the one the mod was made for and they are placed by what they were in it.", result.Versions);
            if (madeFor is null) break;
        }
        if (result is null) return;   // Working has already said why
        await ProgressSettled();
        Say(result.Outputs.Count == 0
            ? $"nothing to carry: {result.Refused} sound(s) not carried. Why, line by line, in {ModUpdate.ReportName}." +
              (result.Unplaced > 0 ? $" The {result.Unplaced} loose file(s) are in {ModUpdate.Result.UnplacedFolder}, to stage by hand." : "")
            : $"updated: {Path.GetFileName(outputBase)}.pak in {outRoot} -- {result.Banks} .bnk and {result.Loose} loose file(s), " +
              $"{result.Carried} sound(s) carried, {result.Refused} not" +
              (result.LooseMoved + result.LooseSame + result.LooseUnverified > 0
                  ? $" (loose: {result.LooseMoved} moved to their sound's new id, {result.LooseSame} still the same sound, " +
                    $"{result.LooseUnverified} kept unverified{(result.MadeFor is null ? "" : $", read as made for {result.MadeFor}")})"
                  : "") +
              (result.Unplaced > 0 ? $"; {result.Unplaced} in {ModUpdate.Result.UnplacedFolder} to stage by hand" : "") +
              (result.LayersPutBack + result.LayersNotPutBack > 0
                  ? $"; added sounds: {result.LayersPutBack} put back, {result.LayersNotPutBack} not" : "") +
              $". Copy it into ~mods in place of the old mod; details in {ModUpdate.ReportName}.");
    }

    /// <summary>One run of the update into a work folder, packed into outputBase, the report and the unplaced files beside it.</summary>
    private async Task<ModUpdate.Result> UpdateOnceAsync(string mod, string work, string outRoot, string outputBase,
                                                         GameHistory history, string madeFor)
    {
        ModUpdate.Result result;
        try
        {
            result = await Working($"updating {Path.GetFileName(mod)}", () =>
            {
                var r = ModUpdate.Build(Session, mod, work, Config, m => Dispatcher.UIThread.Post(() => Say(m)),
                                        history: history, madeFor: madeFor);
                if (r.Outputs.Count > 0)
                {
                    var (ok, message) = ModPackager.PackagePak(work, r.Outputs, outputBase);
                    if (!ok) throw new InvalidDataException(message);
                    foreach (var e in new[] { ".utoc", ".ucas" }) if (File.Exists(outputBase + e)) File.Delete(outputBase + e);
                }
                foreach (var name in new[] { ModUpdate.ReportName, ModUpdate.EventsName })
                    if (File.Exists(Path.Combine(work, name))) File.Copy(Path.Combine(work, name), Path.Combine(outRoot, name), true);
                var spare = Path.Combine(work, ModUpdate.Result.UnplacedFolder);
                var spareOut = Path.Combine(outRoot, ModUpdate.Result.UnplacedFolder);
                if (Directory.Exists(spareOut)) Directory.Delete(spareOut, true);
                if (Directory.Exists(spare))
                    foreach (var f in Directory.EnumerateFiles(spare, "*", SearchOption.AllDirectories))
                    {
                        var to = Path.Combine(spareOut, Path.GetRelativePath(spare, f));
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.Copy(f, to, true);
                    }
                return r;
            });
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>A short list to pick one from; null when backed out of.</summary>
    private async Task<string> PickOneAsync(string title, string text, IReadOnlyList<string> options)
    {
        string answer = null;
        var list = new ListBox { ItemsSource = options, SelectionMode = SelectionMode.Single, MaxHeight = 260 };
        var ok = new Button { Content = "Use this version", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new Window
        {
            Title = title, Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16), Spacing = 10,
                Children =
                {
                    new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    list,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
                },
            },
        };
        ok.Click += (_, _) => { answer = list.SelectedItem as string; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        list.DoubleTapped += (_, _) => { answer = list.SelectedItem as string; dialog.Close(); };
        await dialog.ShowDialog(this);
        return answer;
    }

    /// <summary>"Merge mods…", pressed by the render harness: these two mods into this folder.</summary>
    internal Task MergeForShot(string priority, string secondary, string outRoot)
    {
        MergePicksForShot = [priority, secondary];
        BuildFolderForShot = outRoot;
        return ModMergeAsync();
    }
    internal string[] MergePicksForShot;

    /// <summary>The render harness: open one mod, close them all, and the set they are in.</summary>
    internal Task OpenModForShot(string path) => ModOpenOneAsync(path, [], []);

    /// <summary>The render harness: how a .pak of several banks is answered, in place of its dialog.</summary>
    internal Func<List<ModBank.Bank>, List<ModBank.Bank>> PickBanksForShot;

    /// <summary>The render harness: files dropped on the window, as a drop of several opens them.</summary>
    internal Task DropOpenForShot(List<string> paths) => DropOpen(paths);

    /// <summary>The render harness: Load game pressed again, which remounts and must bring the mods back.</summary>
    internal Task ReloadForShot() => SetupLoadAsync();
    internal Task CloseModsForShot() => ModCloseAsync();
    internal ModSet ModsForShot => Mods;

    /// <summary>The render harness: show the tab of this name (Browse, Test, Build, Mods, Setup).</summary>
    internal void SelectTabForShot(string name)
    {
        var items = Tabs.Items.Cast<object>().ToList();
        var at = items.FindIndex(i => ((i as ContentControl)?.Content ?? i)?.ToString()?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
        if (at >= 0) { Tabs.SelectedIndex = at; Dispatcher.UIThread.RunJobs(); }
        else Console.WriteLine($"no tab {name}");
    }

    /// <summary>A mod opened and "Extract modded wems…" pressed, by the render harness.</summary>
    internal async Task ExtractForShot(string mod, string folder)
    {
        await ModOpenOneAsync(mod, [], []);
        BuildFolderForShot = folder;
        await ModExtractAsync();
    }

    private async Task ModMergeAsync()
    {
        if (Session?.Provider is null)
        {
            Say("load the game first — a merge is diffed against the game's own .bnk files.");
            return;
        }

        var priority = MergePicksForShot?[0] ?? await PickFile("PRIORITY mod — this one wins any sound both mods changed",
                                      "Mod (.bnk or .pak)", "*.bnk", "*.pak", "*.utoc");
        if (priority is null) return;

        var secondary = MergePicksForShot?[1] ?? await PickFile("SECONDARY mod", "Mod (.bnk or .pak)",
                                       "*.bnk", "*.pak", "*.utoc");
        if (secondary is null) return;

        if (string.Equals(priority, secondary, StringComparison.OrdinalIgnoreCase))
        { Say("those are the same file."); return; }

        var outRoot = BuildFolderForShot ?? await PickFolder("Output folder for the merged mod");
        if (outRoot is null) return;
        Directory.CreateDirectory(outRoot);

        // Named after both mods, without the patch suffix either may already carry.
        static string Stem(string path) =>
            System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(path), @"_\d+_P$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var outputBase = Path.Combine(outRoot, ModSafeName($"{Stem(priority)}+{Stem(secondary)}").Replace(' ', '_') + "_9999999_P");

        // The merged banks are written where nothing ships from, and packed: the output folder
        // gets the .pak, the merge's own logs, and each mod's changed sounds in _extracted.
        var work = Path.Combine(Path.GetTempPath(), "XzoundWave", "merge-" + Guid.NewGuid().ToString("N"));
        ModMerge.Result result;
        try
        {
            result = await Working("Merging", () =>
            {
                var r = ModMerge.Build(Session, priority, secondary, work, Config,
                                       Path.Combine(outRoot, "_extracted"),
                                       m => Dispatcher.UIThread.Post(() => Say(m)));
                var files = Directory.GetFiles(work, "*", SearchOption.AllDirectories)
                                     .Select(f => Path.GetRelativePath(work, f)).ToList();
                var mod = files.Where(f => f.Replace('\\', '/').StartsWith("Marvel/", StringComparison.OrdinalIgnoreCase)).ToList();
                var (ok, message) = ModPackager.PackagePak(work, mod, outputBase);
                if (!ok) throw new InvalidDataException(message);
                foreach (var e in new[] { ".utoc", ".ucas" }) if (File.Exists(outputBase + e)) File.Delete(outputBase + e);
                foreach (var f in files.Except(mod)) File.Copy(Path.Combine(work, f), Path.Combine(outRoot, f), true);
                return r;
            });
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (result is null) return;   // Working has already said why
        await ProgressSettled();

        Say($"merged {result.Banks} .bnk" + (result.Loose > 0 ? $" and {result.Loose} loose wem(s)" : "") +
            $" — {result.FromPriority} from priority, " +
            $"{result.FromSecondary} from secondary, {result.Collisions.Count} collision(s)" +
            (result.Skipped is { Count: > 0 } ? $", {result.Skipped.Count} container(s) skipped" : "") +
            $".  {Path.GetFileName(outputBase)}.pak in {outRoot} -- copy it into ~mods; each mod's own changed sounds in _extracted.");

        // The collisions are the only part of a merge that is a judgement call: they
        // are the sounds where the two authors disagreed and one was overruled. Worth
        // a list someone reads rather than a number they scroll past. (Not in a render,
        // where nobody is there to close it: merge-collisions.csv holds the same list.)
        if (result.Collisions.Count == 0 || MergePicksForShot is not null) return;

        var lines = result.Collisions.Take(25)
            .Select(c => $"{c.Bank}  {c.MediaId}  {c.Events}".TrimEnd());
        await Tell("Sounds both mods changed",
            $"The priority mod won {result.Collisions.Count} sound(s) the secondary also replaced:" +
            Environment.NewLine + Environment.NewLine +
            string.Join(Environment.NewLine, lines) +
            (result.Collisions.Count > 25
                ? Environment.NewLine + $"… and {result.Collisions.Count - 25} more"
                : "") +
            Environment.NewLine + Environment.NewLine +
            "Both versions are in _extracted, under the mod each came from, so one can " +
            "be put back by hand.");
    }

    // ---- recompressing -------------------------------------------------------

    /// <summary>
    /// Re-encode PCM audio to Vorbis: in .bnk files, loose .wem files, or a whole mod .pak. Handy
    /// after building a mod by hand, or with tools that write uncompressed audio: the game's own
    /// is Vorbis, and a PCM voice pack runs to hundreds of megabytes for the same sound.
    /// </summary>
    private async Task ModRecompressAsync()
    {
        var picked = RecompressPicksForShot ?? await PickFiles("Pick .bnk files, loose .wem files, or a mod .pak to recompress",
                                                               ".bnk, .wem or mod .pak", "*.bnk", "*.wem", "*.pak", "*.utoc");
        if (picked is null || picked.Count == 0) return;

        var folder = BuildFolderForShot ?? await PickFolder("Where to write the recompressed files");
        if (folder is null) return;
        // Writing over an input would leave nothing to fall back on when the conversion is not
        // what the author wanted. Compared as the file system resolves them, by this host's rule.
        if (picked.FirstOrDefault(p => AppPaths.SamePath(Path.Combine(folder, Path.GetFileName(ModBank.IsPak(p) ? Path.ChangeExtension(p, ".pak") : p)),
                                                          ModBank.IsPak(p) ? Path.ChangeExtension(p, ".pak") : p)) is { } clash)
        {
            Say($"pick a different folder — that would overwrite {Path.GetFileName(clash)}.");
            return;
        }

        var config = Config;
        var r = await Working($"Recompressing {picked.Count} file(s)", () =>
            BankRecompress.RunPicked(picked, config, folder, m => Dispatcher.UIThread.Post(() => Say(m))));
        if (r is null) return;   // Working has already said why
        await ProgressSettled();

        var saved = r.Before - r.After;
        Say(r.Written.Count == 0
            ? "nothing written — " + string.Join("; ", r.Lines.DefaultIfEmpty("no PCM audio in what was picked")) + "."
            : $"recompressed {r.Converted} PCM sound(s) to Vorbis" +
              (r.Kept > 0 ? $", {r.Kept} already compressed" : "") +
              (r.Failed > 0 ? $", {r.Failed} left as they were" : "") +
              $" — {r.Before:N0} B -> {r.After:N0} B" + (saved > 0 ? $" ({saved:N0} B smaller)" : "") +
              $".  {r.Written.Count} file(s) written to {folder}: " +
              string.Join(", ", r.Written.Take(4).Select(Path.GetFileName)) + (r.Written.Count > 4 ? ", …" : "") +
              (r.Lines.Any(l => l.Contains("not audio") || l.Contains("would not open"))
                  ? ".  Not done: " + string.Join("; ", r.Lines.Where(l => l.Contains("not audio") || l.Contains("would not open"))) : "") +
              ".");
    }

    /// <summary>"Recompress…", pressed by the render harness: these inputs into this folder.</summary>
    internal Task RecompressForShot(IReadOnlyList<string> picks, string folder)
    {
        RecompressPicksForShot = [.. picks];
        BuildFolderForShot = folder;
        return ModRecompressAsync();
    }
    internal List<string> RecompressPicksForShot;

    // ---- extracting what a mod changed ---------------------------------------

    /// <summary>
    /// Write out only the sounds a loaded mod actually replaced. Everything it left
    /// alone is the game's own audio and is not worth extracting. With several mods
    /// loaded this covers every one of them the selection is showing.
    /// </summary>
    /// <summary>Hide or show the rows an opened mod left alone (the "unchanged" row kind).</summary>
    private void ToggleOnlyChanged()
    {
        var hidden = _settings.HiddenRowClasses ??= [.. RowClasses.HiddenByDefault];
        if (!hidden.Remove("unchanged")) hidden.Add("unchanged");
        _settings.Save();
        ShowRows();
        ShowToolbar();
    }

    /// <summary>
    /// The game's bank an opened one replaces, language folder included: where the mod put
    /// it, or, for a bare .bnk that never said, the active language's (the regular build's rule).
    /// </summary>
    private string GameBankPathFor(string bank)
    {
        var stem = Path.GetFileNameWithoutExtension(bank ?? "");
        return Mods.Get(stem)?.Bank.MountedAt ?? BuildGamePathForStem(stem);
    }

    /// <summary>
    /// A folder name a person can read for a bank: character, skin, then the bank, as in
    /// "Hela-Default-bnk_vo_1024001". A bank that belongs to no skin keeps its own name.
    /// </summary>
    private string ReadableBankFolder(string bank)
    {
        var stem = Path.GetFileNameWithoutExtension(bank ?? "");
        var skin = System.Text.RegularExpressions.Regex.Matches(stem, @"\d{7}").LastOrDefault()?.Value;
        if (skin is null) return ModSafeName(stem);
        var hero = skin[..4];
        var heroName = Session.HeroName(hero);
        var skinName = Session.SkinName(hero, skin);
        return ModSafeName($"{(heroName == hero ? hero : heroName)}-{(skinName == skin ? skin : skinName)}-{stem}").Replace(' ', '_');
    }

    private async Task ModExtractAsync()
    {
        if (!Mods.Any)
        {
            Say("open a .bnk or a mod .pak first — what a mod changed is only known " +
                "once it has been compared with the game's own.");
            return;
        }

        // One row per media id: a sound several events reach is still one file. A loose file
        // a mod ships is the bank row's audio when that row is on screen, and is written below
        // with the others when it is not.
        var changed = (Sounds?.Rows ?? [])
            .Where(r => r.ModTag is "MODDED" or "NEW")
            .GroupBy(r => r.MediaId)
            .Select(g => g.First())
            .ToList();
        var language = Session?.Language;
        var onScreen = changed.Select(r => Mods.LooseFor(r.MediaId, language)).Where(l => l is not null).ToHashSet();
        var looseLeft = Mods.LooseFiles.Where(l => l.Changed && !onScreen.Contains(l)).ToList();
        if (changed.Count == 0 && looseLeft.Count == 0)
        {
            Say("nothing on screen differs from the shipped .bnk files.");
            return;
        }

        var folder = BuildFolderForShot ?? await PickFolder($"Where to put {changed.Count + looseLeft.Count} changed sound(s)");
        if (folder is null) return;

        // Modded\<Character>-<Skin>-<bank>\ holds the mod's sounds and Original\ the same names
        // with the game's, so the two can be compared file by file. Worked out here, on this
        // thread: the names come from the session and the paths from the opened mods.
        var plan = changed.GroupBy(r => r.Bank, StringComparer.OrdinalIgnoreCase)
                          .Select(g => (Folder: ReadableBankFolder(g.Key), GamePath: GameBankPathFor(g.Key), Rows: g.ToList()))
                          .ToList();
        // Under the bank that plays each one; one nothing in the game plays keeps the Media
        // folder it came from. Another language's copy gets a folder of its own, so two
        // languages of one line never write to one name.
        var loosePlan = looseLeft.Select(l => (Folder: (l.Stem is null ? "Media" : ReadableBankFolder(l.Stem)) +
                                                       (l.Language is null || l.Language.Equals(language, StringComparison.OrdinalIgnoreCase)
                                                           ? "" : "-" + ModSafeName(l.Language)),
                                               Loose: l))
                                 .ToList();
        var sounds = Sounds;
        var session = Session;
        var done = await Working($"Extracting {changed.Count} changed sound(s)", () =>
        {
            int written = 0, originals = 0, failed = 0;
            foreach (var (bankFolder, gamePath, rows) in plan)
            {
                var modded = Directory.CreateDirectory(Path.Combine(folder, "Modded", bankFolder)).FullName;
                string original = null;
                foreach (var row in rows)
                {
                    if (!sounds.Raw.TryGetValue(row.MediaId, out var bytes)) { failed++; continue; }

                    // {mediaId}-{note} is what the staging pane reads back: the id is what
                    // lands the file on the right sound again.
                    var note = ModSafeName(row.Display);
                    var file = string.IsNullOrEmpty(note) ? $"{row.MediaId}.wem" : $"{row.MediaId}-{note}.wem";
                    // One unwritable name must not stop the other two hundred.
                    try { File.WriteAllBytes(Path.Combine(modded, file), bytes); written++; }
                    catch { failed++; continue; }

                    // A sound the mod added has no original to set beside it.
                    if (row.ModTag != "MODDED" || AudioPreview.GameBytes(session, row, gamePath) is not { } stock) continue;
                    original ??= Directory.CreateDirectory(Path.Combine(folder, "Original", bankFolder)).FullName;
                    try { File.WriteAllBytes(Path.Combine(original, file), stock); originals++; }
                    catch { failed++; }
                }
            }
            foreach (var (bankFolder, l) in loosePlan)
            {
                var file = $"{l.Id}.wem";
                try
                {
                    File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "Modded", bankFolder)).FullName, file), l.Bytes);
                    written++;
                }
                catch { failed++; continue; }
                // The game's file at the very path the mod's replaces.
                byte[] stock = null;
                try { stock = session.GameFileAt(l.MountedAt)?.Read(); } catch { }
                if (stock is null) continue;
                try
                {
                    File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "Original", bankFolder)).FullName, file), stock);
                    originals++;
                }
                catch { failed++; }
            }
            return (written, originals, failed);
        });

        // Every row either writes or fails, so nothing at all means the run itself
        // fell over and Working has already said why.
        if (done.written == 0 && done.failed == 0) return;

        Say($"extracted {done.written} changed sound(s) to {Path.Combine(folder, "Modded")} and " +
            $"{done.originals} of the game's originals to {Path.Combine(folder, "Original")}" +
            (done.failed > 0 ? $", {done.failed} failed" : "") +
            $" ({string.Join(", ", plan.Select(p => p.Folder).Concat(loosePlan.Select(p => p.Folder)).Distinct())})." +
            " Named by media id, so they drop straight back in.");
    }

    /// <summary>
    /// Let the progress lines a worker posted land before the result is said. Posted lines queue
    /// behind the await that ends the work, so when the last step after them is short -- a merge
    /// of loose files only -- one arrived after the result and the status line ended on
    /// "reading the secondary mod…" over a merge that had finished.
    /// </summary>
    private static async Task ProgressSettled() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

    /// <summary>
    /// SoundKit's WEM Auto-Organizer, read the same way and matched with what the game says: a folder of
    /// wems (numbered folders 1, 2, 3 ... inside it kept apart) copied into one folder per mod.
    /// </summary>
    private async Task ModOrganizeAsync()
    {
        if (Session?.Provider is null) { Say("load the game first — wems are sorted by what the game says plays them."); return; }
        var input = OrganizePicksForShot?[0] ?? await PickFolder("The folder of wems to organize (numbered folders 1, 2, 3 ... inside it are kept apart)");
        if (input is null) return;
        string rules = OrganizePicksForShot?[1];
        if (OrganizePicksForShot is null &&
            await Ask("Organize wems",
                      "Sort them by a SoundKit rule list (0-WEM-FOLDER-NAME-LIST.txt), or into a folder for the character, " +
                      "skin and bank that play each one?", "Use a rule list", "By character"))
        {
            rules = await PickFile("SoundKit's 0-WEM-FOLDER-NAME-LIST.txt", "Rule list", "*.txt");
            if (rules is null) return;
        }
        var output = BuildFolderForShot ?? await PickFolder("Where to put the organized copies");
        if (output is null) return;
        var inside = Path.GetRelativePath(Path.GetFullPath(input), Path.GetFullPath(output));
        if (AppPaths.SamePath(input, output) || !inside.StartsWith(".."))
        { Say("pick an output folder outside the one being organized."); return; }

        var session = Session;
        var r = await Working("organizing the wems", () =>
            WemOrganizer.Run(session, input, rules, output, m => Dispatcher.UIThread.Post(() => Say(m))));
        if (r is null) return;   // Working has already said why
        await ProgressSettled();
        Say($"organized: {r.Copied} wem(s) copied into {output}" +
            (r.LeftOut > 0 ? $", {r.LeftOut} left out by [DELETE] rules" : "") +
            (r.Unmatched > 0 ? $", {r.Unmatched} no rule matched (in _unmatched)" : "") +
            (r.Unknown > 0 ? $", {r.Unknown} the game does not play (in _unknown)" : "") +
            ". What went where is in organize-report.txt.");
    }

    /// <summary>"Organize wems…", pressed by the render harness: input folder, rule list (or null), output folder.</summary>
    internal Task OrganizeForShot(string input, string rules, string output)
    {
        OrganizePicksForShot = [input, rules];
        BuildFolderForShot = output;
        return ModOrganizeAsync();
    }
    internal string[] OrganizePicksForShot;

    /// <summary>A name a file system will take.</summary>
    private static string ModSafeName(string name)
    {
        name ??= "";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
