using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MRAudioKit;

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private readonly GameSession _session = new();
    private readonly AudioPreview _preview = new();
    private readonly NoteStore _notes = NoteStore.Load();

    /// <summary>Staged custom wems. Survives switching skins — a mod can span several.</summary>
    private readonly ObservableCollection<PendingWem> _pending = [];

    private SkinSounds _sounds;
    private SkinEntry _skin;
    private string _charId;

    public MainWindow()
    {
        InitializeComponent();
        _settings.AutoDetect();
        TbPaks.Text = _settings.PaksDir;
        TbAes.Text = _settings.AesKey;
        TbUsmap.Text = _settings.UsmapPath;
        TbVgm.Text = _settings.VgmstreamPath;
        TbWwiseConsole.Text = _settings.WwiseConsolePath;
        TbWwiseProj.Text = _settings.WwiseProjectPath;
        foreach (var l in GameSession.Languages) CbLang.Items.Add(l);
        CbLang.SelectedItem = GameSession.Languages.Contains(_settings.Language)
            ? _settings.Language : GameSession.Languages[0];
        PendingGrid.ItemsSource = _pending;
        Closed += (_, _) => _preview.Dispose();
    }

    private void Say(string s) => Dispatcher.Invoke(() => Status.Text = s);

    // ---- loading -----------------------------------------------------------

    private async void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        _settings.PaksDir = TbPaks.Text.Trim();
        _settings.AesKey = TbAes.Text.Trim();
        _settings.UsmapPath = TbUsmap.Text.Trim();
        _settings.VgmstreamPath = TbVgm.Text.Trim();
        _settings.WwiseConsolePath = TbWwiseConsole.Text.Trim();
        _settings.WwiseProjectPath = TbWwiseProj.Text.Trim();
        _settings.Language = (string)CbLang.SelectedItem;
        _settings.Save();
        _preview.VgmstreamPath = _settings.VgmstreamPath;

        BtnLoad.IsEnabled = false;
        Tree.Items.Clear();
        Grid_.ItemsSource = null;
        try
        {
            await Task.Run(() => _session.Mount(_settings, Say));
            PopulateTree();
            SettingsPanel.IsExpanded = false;

            // Background, because the tree is usable immediately and this only makes
            // the "Lands in" column complete for banks you have not opened yet.
            _ = Task.Run(() => _session.IndexBanks(Say))
                    .ContinueWith(_ => Dispatcher.Invoke(RefreshPendingStatus));
        }
        catch (Exception ex)
        {
            Say("Load failed: " + ex.Message);
            MessageBox.Show(ex.Message, "Could not load the game", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { BtnLoad.IsEnabled = true; }
    }

    /// <summary>What a tree node points at. A null <see cref="Bank"/> means "the whole skin".</summary>
    private sealed record Pick(string CharId, SkinEntry Skin, string Bank);

    private string _bankFilter;

    private void PopulateTree()
    {
        Tree.Items.Clear();
        foreach (var c in _session.Characters)
        {
            var node = new TreeViewItem { Header = c.Name, Tag = c };
            foreach (var s in c.Skins)
            {
                var skinNode = new TreeViewItem
                {
                    Header = $"{s.Label}   ({s.Banks.Count} bank{(s.Banks.Count == 1 ? "" : "s")})",
                    Tag = new Pick(c.CharId, s, null),
                };
                // Third level. Which bank a wem lives in decides which file you have to
                // rebuild and ship, so it belongs in the picker, not buried in a column.
                foreach (var b in s.Banks.OrderBy(b => Path.GetFileName(b.Path), StringComparer.OrdinalIgnoreCase))
                    skinNode.Items.Add(new TreeViewItem
                    {
                        Header = $"{Path.GetFileName(b.Path)}   {b.Size / 1024.0 / 1024.0:0.0} MB",
                        Tag = new Pick(c.CharId, s, Path.GetFileName(b.Path)),
                    });
                node.Items.Add(skinNode);
            }
            Tree.Items.Add(node);
        }
    }

    private async void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem { Tag: Pick pick }) return;

        var sameSkin = _skin is not null && _skin.SkinId == pick.Skin.SkinId;
        _charId = pick.CharId;
        _skin = pick.Skin;
        _bankFilter = pick.Bank;

        try
        {
            // Switching banks within a skin is a filter, not a re-read.
            if (!sameSkin || _sounds is null)
            {
                Grid_.ItemsSource = null;
                _sounds = await Task.Run(() => SoundIndex.Build(_session, _skin, _charId, Say));
                var relinked = _notes.Apply(_sounds.Rows);
                if (relinked > 0)
                    Say($"{relinked} note(s) moved onto new media ids after a game update.");
                MarkStagedRows();
                LabelBankNodes();
            }
            ApplyFilter();
            RefreshPendingStatus();

            if (_bankFilter is null)
                Say($"{_skin.Label} — {_sounds.Rows.Count} sounds across {_skin.Banks.Count} bank(s). " +
                    "Pick a bank to work on just that file.");
            else
            {
                var n = _sounds.Rows.Count(r => r.Bank.Equals(BankStem(_bankFilter), StringComparison.OrdinalIgnoreCase));
                var embedded = _sounds.BankOfMedia.Count(kv => kv.Value.Equals(_bankFilter, StringComparison.OrdinalIgnoreCase));
                Say($"{_bankFilter} — {n} sounds, {embedded} embedded media. " +
                    "Only this file needs rebuilding for swaps made here.");
            }
        }
        catch (Exception ex) { Say("Failed to read banks: " + ex.Message); }
    }

    private static string BankStem(string fileName) => Path.GetFileNameWithoutExtension(fileName);

    /// <summary>Put real counts on the bank nodes once the skin has been read.</summary>
    private void LabelBankNodes()
    {
        foreach (TreeViewItem charNode in Tree.Items)
        foreach (TreeViewItem skinNode in charNode.Items)
        {
            if (skinNode.Tag is not Pick p || p.Skin.SkinId != _skin.SkinId) continue;
            foreach (TreeViewItem bankNode in skinNode.Items)
            {
                if (bankNode.Tag is not Pick bp || bp.Bank is null) continue;
                var file = _skin.Banks.FirstOrDefault(b => Path.GetFileName(b.Path) == bp.Bank);
                var sounds = _sounds.Rows.Count(r => r.Bank.Equals(BankStem(bp.Bank), StringComparison.OrdinalIgnoreCase));
                var media = _sounds.BankOfMedia.Count(kv => kv.Value.Equals(bp.Bank, StringComparison.OrdinalIgnoreCase));
                bankNode.Header = $"{bp.Bank}   {sounds} sounds · {media} media · " +
                                  $"{(file?.Size ?? 0) / 1024.0 / 1024.0:0.0} MB";
            }
        }
    }

    // ---- grid --------------------------------------------------------------

    private void TbFilter_TextChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_sounds is null) return;
        var q = TbFilter.Text?.Trim() ?? "";
        IEnumerable<SoundRow> rows = _sounds.Rows;
        if (_bankFilter is not null)
            rows = rows.Where(r => r.Bank.Equals(BankStem(_bankFilter), StringComparison.OrdinalIgnoreCase));
        if (CbUnnamed.IsChecked != true) rows = rows.Where(r => r.EventName is not null);
        if (q.Length > 0)
            rows = rows.Where(r =>
                r.Display.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.MediaId.ToString().Contains(q));
        Grid_.ItemsSource = rows.ToList();
    }

    private SoundRow Selected => Grid_.SelectedItem as SoundRow;

    private static bool IsNoteColumn(DataGridColumn c) =>
        c?.Header as string is "Notes" or "Extra notes";

    private void Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // A double-click in a note cell is someone opening it to type, not asking to
        // hear the sound again.
        if (IsNoteColumn(Grid_.CurrentColumn)) return;
        BtnPlay_Click(sender, e);
    }

    /// <summary>Persist a note the moment the cell loses focus — no save button.</summary>
    private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row?.Item is not SoundRow row) return;

        // CellEditEnding fires BEFORE the binding writes back, so read after it lands.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _notes.Set(row);
            _notes.Save();
            Say($"note saved for {row.MediaId} — {_notes.Count} sound(s) annotated.");
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected;
        if (row is null || _sounds is null) return;
        var data = _preview.Bytes(_session, _sounds, row);
        if (data is null) { Say($"media {row.MediaId}: no bytes found."); return; }

        var wav = _preview.Decode(data, row.MediaId, out var err);
        if (wav is null) { Say(err); return; }
        _preview.Play(wav);
        Say($"{row.Display}  —  {row.Bytes:N0} B from {row.Origin}");
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => _preview.Stop();

    private async void BtnDur_Click(object sender, RoutedEventArgs e)
    {
        if (_sounds is null) return;
        var rows = (Grid_.ItemsSource as List<SoundRow>) ?? [];
        BtnDur.IsEnabled = false;
        await Task.Run(() =>
        {
            var n = 0;
            foreach (var r in rows)
            {
                if (r.Seconds is null)
                {
                    var d = _preview.Bytes(_session, _sounds, r);
                    if (d is not null) r.Seconds = _preview.Probe(d, r.MediaId);
                }
                if (++n % 25 == 0) Say($"measuring... {n}/{rows.Count}");
            }
            Say($"measured {rows.Count} clips.");
        });
        Grid_.Items.Refresh();
        BtnDur.IsEnabled = true;
    }

    private void BtnWav_Click(object sender, RoutedEventArgs e) => Export(true);
    private void BtnWem_Click(object sender, RoutedEventArgs e) => Export(false);

    private void Export(bool asWav)
    {
        var row = Selected;
        if (row is null) return;
        var data = _preview.Bytes(_session, _sounds, row);
        if (data is null) { Say("no bytes to export."); return; }

        var dlg = new SaveFileDialog
        {
            // Round-trips into the drop pane: this name re-assigns itself on the way back in.
            FileName = asWav ? $"{row.MediaId}-{row.Display}.wav" : $"{row.MediaId}-{row.Display}.wem",
            Filter = asWav ? "WAV|*.wav" : "WEM|*.wem",
        };
        if (dlg.ShowDialog() != true) return;

        if (!asWav) { File.WriteAllBytes(dlg.FileName, data); Say("wrote " + dlg.FileName); return; }
        var wav = _preview.Decode(data, row.MediaId, out var err);
        if (wav is null) { Say(err); return; }
        File.Copy(wav, dlg.FileName, true);
        Say("wrote " + dlg.FileName);
    }

    private void BtnCell_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected;
        if (row is null) return;
        Clipboard.SetText(row.SheetCell);
        Say("copied  " + row.SheetCell);
    }

    private void BtnCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_sounds is null) return;
        var rows = (Grid_.ItemsSource as List<SoundRow>) ?? [];
        var dlg = new SaveFileDialog { FileName = $"{_skin.SkinId}.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("bnk,Decimal Wem ID-(FullName),Category,Voice Line / Event,Length,Codec,Source,Notes,Extra notes");
        foreach (var r in rows)
            sb.AppendLine(string.Join(',', new[]
            {
                r.Bank, r.SheetCell, r.Category, r.Subtitle, r.Length, r.Codec,
                r.Streamed ? "streamed" : "embedded", r.Notes, r.ExtraNotes,
            }.Select(Csv)));
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        Say($"wrote {rows.Count} rows to {dlg.FileName}");
    }

    private static string Csv(string s)
        => s is null ? "" : $"\"{s.Replace("\"", "\"\"")}\"";

    // ---- staging: the drop pane -------------------------------------------

    private void DropPane_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DropPane.BorderBrush = e.Effects == DragDropEffects.Copy
            ? (System.Windows.Media.Brush)FindResource("Accent")
            : (System.Windows.Media.Brush)FindResource("Line");
        e.Handled = true;
    }

    private void DropPane_DragLeave(object sender, DragEventArgs e)
        => DropPane.BorderBrush = (System.Windows.Media.Brush)FindResource("Line");

    private void DropPane_Drop(object sender, DragEventArgs e)
    {
        DropPane.BorderBrush = (System.Windows.Media.Brush)FindResource("Line");
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        Stage((string[])e.Data.GetData(DataFormats.FileDrop));
    }

    private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Wwise media|*.wem", Multiselect = true };
        if (dlg.ShowDialog() == true) Stage(dlg.FileNames);
    }

    /// <summary>
    /// Files or folders. Names decide the assignment: {MediaID}-{note}.wem, with
    /// everything from the first hyphen kept only as the author's own reminder.
    /// </summary>
    private void Stage(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*.wem", SearchOption.AllDirectories));
            else if (p.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
                files.Add(p);
        }

        var added = 0; var replaced = 0;
        var rejected = new List<string>();
        foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var item = PendingWem.FromFile(f, out var why);
            if (item is null) { rejected.Add(why); continue; }

            var existing = _pending.FirstOrDefault(x => x.MediaId == item.MediaId);
            if (existing is not null) { _pending.Remove(existing); replaced++; } else added++;
            _pending.Add(item);
        }

        Reorder();
        RefreshPendingStatus();
        MarkStagedRows();
        Grid_.Items.Refresh();

        var mismatched = _pending.Count(p => p.FormatTag != 0xFFFF);
        Say($"{_pending.Count} staged ({added} new, {replaced} updated)" +
            (rejected.Count > 0 ? $", {rejected.Count} rejected" : "") +
            (mismatched > 0 ? $", {mismatched} not VORBIS" : "") + ".");

        // One summary rather than a modal per file -- a 50-file drop should not be
        // 50 dialogs, but a silent codec mismatch is the exact failure worth naming.
        var problems = new List<string>(rejected);
        problems.AddRange(_pending.Where(p => p.FormatTag != 0xFFFF)
            .Select(p => $"{p.FileName} — {p.Codec}; this game's banks declare VORBIS"));
        if (problems.Count > 0)
            MessageBox.Show(string.Join(Environment.NewLine, problems.Take(30)) +
                            (problems.Count > 30 ? $"{Environment.NewLine}… and {problems.Count - 30} more" : ""),
                            "Check these files", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Reorder()
    {
        var sorted = _pending.OrderBy(p => p.MediaId).ToList();
        _pending.Clear();
        foreach (var p in sorted) _pending.Add(p);
    }

    /// <summary>
    /// Tell the author what each staged file will actually do: land inside a bank,
    /// ship as a loose streamed wem, or belong to a skin they have not opened.
    /// </summary>
    private void RefreshPendingStatus()
    {
        foreach (var p in _pending)
        {
            // The open skin first, then the global index, so a file staged before you
            // navigate anywhere still names its bank.
            var inBank = _sounds is not null &&
                         _sounds.BankOfMedia.TryGetValue(p.MediaId, out var b) ? b : null;
            var foreign = false;
            var all = _session.BanksForMedia(p.MediaId);
            if (inBank is null && all.Count > 0)
            {
                inBank = Path.GetFileName(all[0]);
                foreign = true;
            }
            // Say so when the same media sits in several banks — every one of them
            // gets rebuilt, and the author should know that before shipping.
            if (inBank is not null && all.Count > 1) inBank += $" +{all.Count - 1} more";
            p.BankList = all.Count > 1
                ? string.Join(Environment.NewLine, all.Take(12).Select(x => "  " + Path.GetFileName(x))) +
                  (all.Count > 12 ? $"{Environment.NewLine}  … and {all.Count - 12} more" : "")
                : "";
            var isLoose = _session.Provider is not null && _session.LooseMedia(p.MediaId) is not null;

            // Name the bank rather than saying "in bank": the whole point of picking a
            // bank is knowing which single file you have to rebuild and ship. "+ streamed"
            // means the sound exists TWICE -- a prefetch stub in the bank and the full
            // clip loose under Media/ -- and the build patches both.
            // The bank entry is what gets replaced. A loose Media/ file only matters
            // when there is NO bank entry to swap.
            p.Status = inBank
                    ?? (isLoose ? "loose Media wem"
                                : _session.BankIndexReady ? "unknown media id" : "indexing…");
            if (foreign) p.Status += "  (other skin)";

            // What this file is about to overwrite. The subtitle when the game ships
            // one, the event name otherwise -- so an SFX slot is still identifiable.
            var row = _sounds?.Rows.FirstOrDefault(r => r.MediaId == p.MediaId);
            p.OriginalEvent = row?.Display ?? "";
            p.Original = !string.IsNullOrWhiteSpace(row?.Subtitle) ? row.Subtitle : row?.Display ?? "";
        }
        PendingGrid.Items.Refresh();
    }

    private void MarkStagedRows()
    {
        if (_sounds is null) return;
        var byId = _pending.ToDictionary(p => p.MediaId, p => p.FullPath);
        foreach (var r in _sounds.Rows)
            r.ReplacementPath = byId.GetValueOrDefault(r.MediaId);
    }

    private void BtnRemovePending_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in PendingGrid.SelectedItems.Cast<PendingWem>().ToList())
            _pending.Remove(item);
        MarkStagedRows();
        Grid_.Items.Refresh();
        Say($"{_pending.Count} staged.");
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _pending.Clear();
        MarkStagedRows();
        Grid_.Items.Refresh();
        Say("replacements cleared.");
    }

    /// <summary>Assign the selected grid row explicitly, whatever the file is called.</summary>
    private void BtnReplace_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected;
        if (row is null) { Say("select a sound first."); return; }

        var dlg = new OpenFileDialog { Filter = "Wwise media|*.wem", Title = $"Replacement for {row.Display}" };
        if (dlg.ShowDialog() != true) return;

        var data = File.ReadAllBytes(dlg.FileName);
        var info = BnkBuilder.Inspect(data);
        if (!info.IsRiff)
        {
            MessageBox.Show($"{Path.GetFileName(dlg.FileName)} is {info.Codec}.", "Not a wem",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (info.FormatTag != 0xFFFF)
        {
            var go = MessageBox.Show(
                $"This slot declares {row.Codec}.\nYour file is {info.Codec} " +
                $"({info.SampleRate} Hz, {info.Channels}ch).\n\n" +
                "Mismatched codecs usually play as silence or noise. Use it anyway?",
                "Codec mismatch", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (go != MessageBoxResult.Yes) return;
        }

        var existing = _pending.FirstOrDefault(x => x.MediaId == row.MediaId);
        if (existing is not null) _pending.Remove(existing);
        _pending.Add(new PendingWem
        {
            MediaId = row.MediaId,
            Note = row.Display,
            FileName = Path.GetFileName(dlg.FileName),
            FullPath = dlg.FileName,
            Bytes = data.Length,
            Codec = info.Codec,
            FormatTag = info.FormatTag,
        });
        Reorder();
        RefreshPendingStatus();
        MarkStagedRows();
        Grid_.Items.Refresh();
        Say($"{_pending.Count} staged — {row.MediaId} <- {Path.GetFileName(dlg.FileName)}");
    }

    // ---- numbered test bank ------------------------------------------------

    /// <summary>
    /// Replace every sound in the chosen bank(s) with a clip that speaks a number, and
    /// write a legend. Trigger a sound in game, hear the number, look up what it was.
    /// </summary>
    private void BtnTestBank_Click(object sender, RoutedEventArgs e)
    {
        if (_skin is null) { Say("pick a skin or bank first."); return; }

        if (string.IsNullOrWhiteSpace(_settings.TestWemDir) || !Directory.Exists(_settings.TestWemDir))
        {
            var pick = new OpenFolderDialog
            {
                Title = "Folder of numbered clips  ({number}.wem, plus an optional silent one)",
            };
            if (pick.ShowDialog() != true) return;
            _settings.TestWemDir = pick.FolderName;
            _settings.Save();
        }

        var wems = TestBankBuilder.ScanFolder(_settings.TestWemDir);

        // Scope: the selected bank, or every bank of the skin.
        var scope = _bankFilter is not null
            ? _skin.Banks.Where(b => Path.GetFileName(b.Path)
                    .Equals(_bankFilter, StringComparison.OrdinalIgnoreCase)).Select(b => b.Path).ToList()
            : _skin.Banks.Select(b => b.Path).ToList();

        var perBank = CbPerBank.IsChecked == true;
        var entries = TestBankBuilder.CountEntries(_session, scope);
        var required = perBank
            ? scope.Max(p => TestBankBuilder.CountEntries(_session, [p]))
            : entries;

        // Not enough clips (or none at all)? Offer to speak them, rather than sending
        // the user off to find a set. SAPI is on every Windows install and a PCM wem
        // needs no encoder, so this costs nothing to ship.
        if (wems.ByNumber.Count < required && Tts.IsAvailable)
        {
            var ask = MessageBox.Show(
                $"This needs {required} numbered clip(s); the folder has {wems.ByNumber.Count}." +
                Environment.NewLine + Environment.NewLine +
                $"Generate 0-{required - 1} now with the Windows speech voice?" +
                Environment.NewLine +
                $"About {required * 0.05:0} second(s), written as PCM .wem files." +
                Environment.NewLine + Environment.NewLine +
                (WwiseVorbis.IsConfigured(_settings)
                    ? "Output: VORBIS, via WwiseConsole — the same format the game ships."
                    : "Output: PCM. The game's banks all declare Vorbis, so if a test bank " +
                      "plays silence, set WwiseConsole.exe and a .wproj in Setup, or use an " +
                      $"existing Vorbis clip set.  ({WwiseVorbis.WhyNot(_settings)})"),
                "Generate spoken numbers", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (ask == MessageBoxResult.Cancel) return;
            if (ask == MessageBoxResult.Yes)
            {
                try
                {
                    Func<string, (byte[], string)> enc = null;
                    if (WwiseVorbis.IsConfigured(_settings))
                    {
                        WwiseVorbis.ForceVorbisConversion(_settings.WwiseProjectPath);
                        enc = wav => { var b = WwiseVorbis.Encode(_settings, wav, out var er); return (b, er); };
                    }
                    Tts.GenerateNumbers(_settings.TestWemDir, 0, required - 1, Say, 2, enc);
                    wems = TestBankBuilder.ScanFolder(_settings.TestWemDir);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Speech generation failed",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }

        if (wems.ByNumber.Count == 0)
        {
            MessageBox.Show("No files named like 0.wem, 1.wem … in that folder, and nothing was generated.",
                            "Nothing to number with", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var lowest = wems.ByNumber.Keys.Min();
        var need = required;
        var have = wems.ByNumber.Count;
        var short_ = Math.Max(0, need - have);

        var msg = new StringBuilder();
        msg.AppendLine($"Scope: {(_bankFilter ?? _skin.Label)}   —   {scope.Count} bank(s), {entries} media entries.");
        msg.AppendLine($"Numbered clips available: {have}  (lowest {lowest}, highest {wems.Max}).");
        msg.AppendLine(perBank
            ? $"Numbering restarts at {lowest} for each bank; the largest bank needs {need}."
            : $"Numbering runs continuously from {lowest}; that needs {need}.");
        msg.AppendLine();
        if (short_ == 0)
            msg.AppendLine("Every entry gets its own number.");
        else if (wems.SilentPath is not null)
            msg.AppendLine($"{short_} entr(y/ies) go past the last clip and will be SILENCED, " +
                           "so you cannot mistake leftover vanilla audio for a result.");
        else
            msg.AppendLine($"{short_} entr(y/ies) go past the last clip and will KEEP their original " +
                           "audio — add a silent clip to the folder to avoid that.");
        msg.AppendLine();
        msg.AppendLine("Continue?");

        if (MessageBox.Show(msg.ToString(), "Numbered test bank",
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var outDlg = new OpenFolderDialog { Title = "Output folder for the test build" };
        if (outDlg.ShowDialog() != true) return;

        try
        {
            var r = TestBankBuilder.Build(_session, scope, wems, outDlg.FolderName,
                                          lowest, true, _sounds, perBank);
            Say($"test bank: {r.Numbered} numbered, {r.Silenced} silenced, {r.Banks} bank(s) written.");
            MessageBox.Show(r.Log + Environment.NewLine +
                            "Legend written to test-bank-legend.csv.",
                            "Test bank built", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Say("test bank failed: " + ex.Message); }
    }

    // ---- build -------------------------------------------------------------

    /// <summary>
    /// Rebuild every bank of this skin that embeds a staged media, and drop streamed
    /// replacements as loose wems. Output mirrors the pak layout so the folder can go
    /// straight to repak, rather than a flat bucket the author has to re-sort.
    /// </summary>
    private void BtnBuild_Click(object sender, RoutedEventArgs e)
    {
        if (_sounds is null || _skin is null) { Say("pick a skin first."); return; }
        if (_pending.Count == 0) { Say("nothing staged — drop some .wem files on the right."); return; }

        var dlg = new OpenFolderDialog { Title = "Output folder for the mod" };
        if (dlg.ShowDialog() != true) return;
        var outRoot = dlg.FolderName;

        // Which banks actually embed something staged? Ask the global index rather than
        // assuming the open skin -- a mod can span skins, and a silently unpatched bank
        // is the worst possible outcome.
        var bankPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _pending)
            foreach (var bp in _session.BanksForMedia(p.MediaId)) bankPaths.Add(bp);
        if (!_session.BankIndexReady)
            foreach (var b in _skin.Banks) bankPaths.Add(b.Path);

        // 14% of this game's media are embedded in more than one bank -- one shared SFX
        // sits in 93 of them. Rebuilding all of them is CORRECT (otherwise the sound only
        // changes for some characters) but can turn a one-line edit into a huge mod, so
        // it is the author's call rather than a silent decision.
        var skinBanks = _skin.Banks.Select(b => b.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outside = bankPaths.Where(p => !skinBanks.Contains(p)).ToList();
        var scopeNote = "";
        if (outside.Count > 0)
        {
            var shared = _pending.Select(p => p.MediaId)
                .Where(id => _session.BanksForMedia(id).Count > 1)
                .Select(id => $"  {id} in {_session.BanksForMedia(id).Count} banks")
                .Take(10).ToList();

            var msg = $"{outside.Count} bank(s) outside {_skin.SkinId} also embed the media you staged:"
                    + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, shared)
                    + Environment.NewLine + Environment.NewLine
                    + "Yes - rebuild all of them (the sound changes everywhere it is used)." + Environment.NewLine
                    + $"No  - only this skin's banks ({skinBanks.Count} file(s)).";

            var answer = MessageBox.Show(msg, "This media is shared",
                                         MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) { Say("build cancelled."); return; }
            if (answer == MessageBoxResult.No) bankPaths.RemoveWhere(p => !skinBanks.Contains(p));
            scopeNote = answer == MessageBoxResult.Yes
                ? $"scope: every bank containing a staged media ({bankPaths.Count})."
                : $"scope: this skin only ({bankPaths.Count}); {outside.Count} other bank(s) left stale.";
        }

        ModBuilder.Result built;
        try { built = ModBuilder.Build(_session, _skin, _pending.ToList(), outRoot, bankPaths, CbPrefetch.IsChecked == true); }
        catch (Exception ex) { Say("build failed: " + ex.Message); return; }

        Say($"built {built.Banks} bank(s) and {built.Loose} audio file(s) into {outRoot} - " +
            (built.Loose == 0 ? "the .bnk is the whole mod." : "ship the whole folder to repak."));
        MessageBox.Show((scopeNote.Length > 0 ? scopeNote + Environment.NewLine + Environment.NewLine : "") + built.Log,
                        "Build complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}