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
        yield return new ToolButton("Open .bnk / .pak…", () => _ = ModOpenAsync(), Key: true,
            Hint: "Open .bnk files, or a mod .pak to pick them from. What a mod supplies " +
                  "wins over the game's copy of that .bnk.");

        yield return new ToolButton("Close mods", () => _ = ModCloseAsync(),
            Hint: "Drop every loaded mod and go back to the game's own .bnk files.");

        yield return new ToolButton("Merge mods…", () => _ = ModMergeAsync(),
            Hint: "Combine two mods. The priority one wins any sound both of them changed.");

        yield return new ToolButton("Recompress .bnk…", () => _ = ModRecompressAsync(),
            Hint: "Re-encode a .bnk's PCM audio to Vorbis, the way the game's own entries are.");

        yield return new ToolButton("Extract modded wems…", () => _ = ModExtractAsync(),
            Hint: "Write out only the sounds a loaded mod actually replaced.");
    }

    // ---- opening somebody else's bank ---------------------------------------

    private async Task ModOpenAsync()
    {
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

        // Two mods at once is the ordinary way a stack gets loaded, and it is also
        // where they are likeliest to claim the same .bnk. Each file reports itself on
        // the status line, but the next file's report overwrites it: a displaced .bnk
        // named in a line nobody can still read has not been named. So the whole pick
        // is collected and said once, at the end.
        var taken = new List<ModSet.Entry>();
        var displaced = new List<ModSet.Entry>();

        foreach (var file in files) await ModOpenOneAsync(file, taken, displaced);

        // One file has already said everything there is to say about itself, and a pick
        // that loaded nothing should keep whatever the failure said.
        if (files.Count == 1 || taken.Count == 0) return;

        // By stem, because two files in one pick can claim the same .bnk and only the
        // last one is still in the set: counting both would double what it changed.
        var stems = taken.Select(t => t.Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var live = Mods.All.Where(e => stems.Contains(e.Stem)).ToList();

        // Selecting nothing would empty the list under a line claiming a load, so the
        // last file's selection stands if for any reason these have no node.
        var nodes = ModNodesFor(stems);
        if (nodes.Count > 0)
        {
            await ModWarmAsync(nodes);
            ModSelect(nodes);
        }

        // Same distinction the single-file line makes: a mod whose banks the game has
        // no counterpart for cannot be diffed at all, and "0 changed" would read there
        // as "these mods do nothing".
        var compared = live.Any(e => e.Sounds.Rows.Any(r => r.ModTag is not null));

        Say($"loaded {live.Count} .bnk from {files.Count} file(s) — " +
            (compared ? $"{live.Sum(e => e.Changed):N0} sound(s) changed from the shipped .bnk files"
                      : "no shipped .bnk to compare against") +
            (displaced.Count > 0
                ? "  —  replaced " + string.Join(", ", displaced.Select(d => $"{d.BankName} from {d.SourceName}"))
                : "") +
            ".  Builds now use these as their base.");
    }

    /// <summary>
    /// Open a .bnk directly, or mount a .pak and ask which of the banks inside it to
    /// load. More than one can be loaded at a time; each overlays the game's copy of
    /// that .bnk and nothing else.
    /// </summary>
    /// <param name="taken">Collects what this file put into the set, for the caller's report.</param>
    /// <param name="displaced">Collects what that pushed aside, for the same reason.</param>
    private async Task ModOpenOneAsync(string path, List<ModSet.Entry> taken,
                                       List<ModSet.Entry> displaced)
    {
        var name = Path.GetFileName(path);
        var opened = await Working($"Opening {name}", () => ModBank.Open(path, Config));
        if (opened is null) return;   // Working has already said why

        if (opened.Banks.Count == 0)
        {
            Say($"{name} holds no .bnk files" +
                (opened.Skipped.Count > 0
                    ? $" — {opened.Skipped.Count} container(s) could not be opened."
                    : "."));
            return;
        }

        // A .pak usually holds several banks. Guessing which one was meant is how
        // someone ends up looking at a bank they did not open.
        var banks = opened.Banks.Count == 1
            ? opened.Banks
            : await ModPickBanksAsync(opened.Banks, name);
        if (banks is null || banks.Count == 0) return;

        await ModTakeAsync(banks, path, opened.Skipped, opened.Banks.Count, taken, displaced);
    }

    /// <summary>
    /// Take the chosen banks into the mod set, name anything this displaces, and show
    /// them.
    /// </summary>
    private async Task ModTakeAsync(List<ModBank.Bank> banks, string source,
                                    List<string> skipped, int siblings,
                                    List<ModSet.Entry> taken, List<ModSet.Entry> pushedAside)
    {
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

        var nodes = ModNodesFor(loaded.Select(l => l.Stem));
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
        if (!Mods.Any) { Say("no mods are open."); return; }

        var sources = Mods.Sources.ToList();
        Mods.Clear();

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

        PretendModded(
            Mods.All.ToDictionary(e => e.Stem, e => e.Changed, StringComparer.OrdinalIgnoreCase),
            expand);
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
    private async Task ModMergeAsync()
    {
        if (Session?.Provider is null)
        {
            Say("load the game first — a merge is diffed against the game's own .bnk files.");
            return;
        }

        var priority = await PickFile("PRIORITY mod — this one wins any sound both mods changed",
                                      "Mod (.bnk or .pak)", "*.bnk", "*.pak", "*.utoc");
        if (priority is null) return;

        var secondary = await PickFile("SECONDARY mod", "Mod (.bnk or .pak)",
                                       "*.bnk", "*.pak", "*.utoc");
        if (secondary is null) return;

        if (string.Equals(priority, secondary, StringComparison.OrdinalIgnoreCase))
        { Say("those are the same file."); return; }

        var outRoot = await PickFolder("Output folder for the merged mod");
        if (outRoot is null) return;

        var result = await Working("Merging", () => ModMerge.Build(
            Session, priority, secondary, outRoot, Config,
            Path.Combine(outRoot, "_extracted"),
            m => Dispatcher.UIThread.Post(() => Say(m))));
        if (result is null) return;   // Working has already said why

        Say($"merged {result.Banks} .bnk — {result.FromPriority} from priority, " +
            $"{result.FromSecondary} from secondary, {result.Collisions.Count} collision(s)" +
            (result.Skipped is { Count: > 0 } ? $", {result.Skipped.Count} container(s) skipped" : "") +
            $".  Written to {outRoot}, each mod's own changed sounds in _extracted.");

        // The collisions are the only part of a merge that is a judgement call: they
        // are the sounds where the two authors disagreed and one was overruled. Worth
        // a list someone reads rather than a number they scroll past.
        if (result.Collisions.Count == 0) return;

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
    /// Re-encode a .bnk's PCM audio to Vorbis. Handy after building a mod by hand, or
    /// with tools that embed uncompressed audio: the game's own entries are Vorbis and
    /// a PCM voice pack runs to hundreds of megabytes for the same sound.
    /// </summary>
    private async Task ModRecompressAsync()
    {
        var source = await PickFile("Pick a .bnk to recompress", "Soundbank", "*.bnk");
        if (source is null) return;

        var folder = await PickFolder("Where to write the recompressed .bnk");
        if (folder is null) return;

        var dest = Path.Combine(folder, Path.GetFileName(source));
        // Compare what the file system would resolve, not the two strings two different
        // pickers happened to hand back for the same place.
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(source),
                          StringComparison.OrdinalIgnoreCase))
        {
            // Writing over the input would leave nothing to fall back on when the
            // conversion is not what the author wanted.
            Say("pick a different folder — that would overwrite the .bnk you chose.");
            return;
        }

        var done = await Working($"Recompressing {Path.GetFileName(source)}", () =>
        {
            var original = File.ReadAllBytes(source);
            var r = BankRecompress.Run(original, m => Dispatcher.UIThread.Post(() => Say(m)));
            if (r.Converted == 0) return (r, original.LongLength, written: false);

            Directory.CreateDirectory(folder);
            File.WriteAllBytes(dest, r.Bytes);
            return (r, original.LongLength, written: true);
        });

        if (done.r is null) return;   // Working has already said why
        var (result, before, written) = done;

        if (!written)
        {
            Say($"nothing to convert in {Path.GetFileName(source)} — " +
                string.Join("; ", result.Notes.DefaultIfEmpty("every entry is already compressed")) + ".");
            return;
        }

        var saved = before - result.Bytes.LongLength;
        var factor = result.MediaAfter > 0 ? (double)result.MediaBefore / result.MediaAfter : 0;
        Say($"recompressed {result.Converted} entry(s) to Vorbis" +
            (result.Skipped > 0 ? $", {result.Skipped} already compressed" : "") +
            (result.Failed > 0 ? $", {result.Failed} left as PCM" : "") +
            $" — {before:N0} B -> {result.Bytes.LongLength:N0} B" +
            (saved > 0
                ? $" ({saved:N0} B smaller" + (factor > 1 ? $", audio {factor:0.0}x" : "") + ")"
                : "") +
            $".  Written to {dest}.");
    }

    // ---- extracting what a mod changed ---------------------------------------

    /// <summary>
    /// Write out only the sounds a loaded mod actually replaced. Everything it left
    /// alone is the game's own audio and is not worth extracting. With several mods
    /// loaded this covers every one of them the selection is showing.
    /// </summary>
    private async Task ModExtractAsync()
    {
        if (!Mods.Any || Sounds is null)
        {
            Say("open a .bnk or a mod .pak first — what a mod changed is only known " +
                "once it has been compared with the game's own.");
            return;
        }

        // One row per media id: a sound several events reach is still one file.
        var changed = Sounds.Rows
            .Where(r => r.ModTag is "MODDED" or "NEW")
            .GroupBy(r => r.MediaId)
            .Select(g => g.First())
            .ToList();
        if (changed.Count == 0)
        {
            Say("nothing on screen differs from the shipped .bnk files.");
            return;
        }

        var folder = await PickFolder($"Where to put {changed.Count} changed sound(s)");
        if (folder is null) return;

        // One folder named after the .bnk when there is only one, so the single-mod
        // output is what it always was; a name covering the set when there are several.
        var stems = changed.Select(r => r.Bank).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dir = Path.Combine(folder,
            stems.Count == 1 ? ModSafeName(stems[0]) : $"{stems.Count}-bnk-modded");

        var sounds = Sounds;
        var done = await Working($"Extracting {changed.Count} changed sound(s)", () =>
        {
            Directory.CreateDirectory(dir);
            int written = 0, failed = 0;
            foreach (var row in changed)
            {
                if (!sounds.Raw.TryGetValue(row.MediaId, out var bytes)) { failed++; continue; }

                // {mediaId}-{note} is what the staging pane reads back: the id is what
                // lands the file on the right sound again.
                var note = ModSafeName(row.Display);
                var file = string.IsNullOrEmpty(note) ? $"{row.MediaId}.wem" : $"{row.MediaId}-{note}.wem";
                // One unwritable name must not stop the other two hundred.
                try { File.WriteAllBytes(Path.Combine(dir, file), bytes); written++; }
                catch { failed++; }
            }
            return (written, failed);
        });

        // Every row either writes or fails, so nothing at all means the run itself
        // fell over and Working has already said why.
        if (done.written == 0 && done.failed == 0) return;

        Say($"extracted {done.written} changed sound(s) to {dir}" +
            (done.failed > 0 ? $", {done.failed} failed" : "") +
            ". Named MediaID-event, so they drop straight back in.");
    }

    /// <summary>A name a file system will take.</summary>
    private static string ModSafeName(string name)
    {
        name ??= "";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
