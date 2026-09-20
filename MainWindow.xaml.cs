using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace MRAudioKit;

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private readonly GameSession _session = new();
    private readonly AudioPreview _preview = new();
    private bool _autoplayHooked;
    private Project _project = Project.New();

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
        ApplyColumnVisibility();
        SyncRowSwitches();
        // A failure to play used to be swallowed entirely, so "no sound" looked the
        // same as a broken decoder. Say it once, where the user is already looking.
        _preview.PlaybackFailed += msg => Dispatcher.BeginInvoke(new Action(() =>
            Say("playback failed: " + msg)));
        RefreshUsmapInfo();
        DropHelp.IsExpanded = _settings.ShowDropHelp;
        CbTransProvider.ItemsSource = LiveTranslate.Providers;
        CbTransProvider.SelectedItem =
            LiveTranslate.Providers.Contains(_settings.TranslateProvider ?? "")
                ? _settings.TranslateProvider : LiveTranslate.ProviderNone;
        TbTransKey.Text = _settings.DeepLKey;
        TbTransUrl.Text = _settings.LocalTranslateUrl;
        TbTransModel.Text = _settings.LocalTranslateModel;
        foreach (var l in GameSession.Languages) CbLang.Items.Add(l);
        CbLang.SelectedItem = GameSession.Languages.Contains(_settings.Language)
            ? _settings.Language : GameSession.Languages[0];
        PendingGrid.ItemsSource = _pending;
        RefreshProjectLabel();
        Closing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; };
        Closed += (_, _) => _preview.Dispose();

        // Reopen whatever was last worked on, quietly -- a missing or moved project
        // should not be an error dialog on startup.
        if (!string.IsNullOrWhiteSpace(_settings.LastProject) && File.Exists(_settings.LastProject))
            OpenProject(_settings.LastProject, announce: false);
    }

    private void Say(string s) => Dispatcher.Invoke(() => Status.Text = s);

    // ---- loading -----------------------------------------------------------

    private async void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        _settings.PaksDir = TbPaks.Text.Trim();
        _settings.AesKey = TbAes.Text.Trim();
        _settings.UsmapPath = TbUsmap.Text.Trim();
        _settings.TranslateProvider = CbTransProvider.SelectedItem as string ?? LiveTranslate.ProviderNone;
        _settings.DeepLKey = TbTransKey.Text.Trim();
        _settings.LocalTranslateUrl = TbTransUrl.Text.Trim();
        _settings.LocalTranslateModel = TbTransModel.Text.Trim();
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
            Tabs.SelectedIndex = 0;   // Setup is done; start on Browse

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

        // Everything that is not a character: ambience, music, UI, maps. One node so
        // the hero list stays the first thing you see.
        if (_session.OtherBanks.Count == 0) return;
        var others = new TreeViewItem
        {
            Header = $"Other banks   ({_session.OtherBanks.Sum(g => g.Skins.Count)})",
        };
        foreach (var g in _session.OtherBanks)
        {
            var groupNode = new TreeViewItem { Header = g.Name };
            foreach (var b in g.Skins)
                groupNode.Items.Add(new TreeViewItem
                {
                    Header = $"{b.Label}   {b.Banks[0].Size / 1024.0 / 1024.0:0.0} MB",
                    Tag = new Pick("", b, b.Label),
                });
            others.Items.Add(groupNode);
        }
        Tree.Items.Add(others);
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
                var relinked = _project.ApplyNotes(_sounds.Rows);
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

    /// <summary>
    /// The toolbar box and Settings > Rows are two views of one setting, so this writes
    /// the setting rather than keeping a second piece of state that could disagree.
    /// </summary>
    private void CbUnnamed_Click(object sender, RoutedEventArgs e)
    {
        var hidden = _settings.HiddenRowClasses ??= [.. RowClasses.HiddenByDefault];
        hidden.RemoveAll(h => h.Equals("unnamed", StringComparison.OrdinalIgnoreCase));
        if (CbUnnamed.IsChecked != true) hidden.Add("unnamed");
        _settings.Save();
        ApplyFilter();
    }

    /// <summary>Put the toolbar box back in step after the settings panel moved it.</summary>
    private void SyncRowSwitches()
    {
        var hidden = _settings.HiddenRowClasses ??= [.. RowClasses.HiddenByDefault];
        CbUnnamed.IsChecked = !hidden.Contains("unnamed", StringComparer.OrdinalIgnoreCase);
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings, Grid_, () =>
        {
            SyncRowSwitches();
            DropHelp.IsExpanded = _settings.ShowDropHelp;
            ApplyFilter();
        }) { Owner = this };
        win.ShowDialog();
        SyncRowSwitches();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_sounds is null) return;
        var q = TbFilter.Text?.Trim() ?? "";
        IEnumerable<SoundRow> rows = _sounds.Rows;
        if (_bankFilter is not null)
            rows = rows.Where(r => r.Bank.Equals(BankStem(_bankFilter), StringComparison.OrdinalIgnoreCase));
        rows = RowClasses.Apply(rows, _settings.HiddenRowClasses ??= [.. RowClasses.HiddenByDefault]);
        if (q.Length > 0)
            rows = rows.Where(r =>
                r.Display.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.MediaId.ToString().Contains(q) ||
                r.TestLabel.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                SoundTags.Matches(r.Tags, q) ||
                // clips are spoken zero-padded, so "042" should find #42 as well
                (int.TryParse(q.TrimStart('#'), out var qn) && r.TestNumber == qn));
        if (SoundRow.ShowTranslatedCategory) TranslateCategories();
        Grid_.ItemsSource = rows.ToList();
        RefreshStats();
    }

    private SoundRow Selected => Grid_.SelectedItem as SoundRow;

    /// <summary>
    /// Counts for the rows on screen, in the same colours the rows use. It follows
    /// the filter deliberately: after typing a filter the useful question is "how
    /// many of THESE are muted", not how many exist in total.
    /// </summary>
    private void RefreshStats()
    {
        var rows = (Grid_.ItemsSource as List<SoundRow>) ?? [];
        if (rows.Count == 0)
        {
            foreach (var t in new[] { StatTotal, StatDefault, StatStaged, StatModded, StatMuted, StatVolume })
                t.Text = "";
            return;
        }

        var staged = rows.Count(r => r.ReplacementPath is not null);
        var muted = rows.Count(r => r.Muted);
        var modded = rows.Count(r => r.ModTag is "MODDED" or "NEW");
        var plain = rows.Count(r => r.ReplacementPath is null && !r.Muted &&
                                    r.ModTag is not ("MODDED" or "NEW"));

        StatTotal.Text = $"{rows.Count:N0} shown";
        StatDefault.Text = $"{plain:N0} default";
        StatStaged.Text = staged > 0 ? $"{staged:N0} replaced" : "";
        StatModded.Text = modded > 0 ? $"{modded:N0} modded" : "";
        StatMuted.Text = muted > 0 ? $"{muted:N0} muted" : "";

        var scaled = rows.Count(r => Math.Abs(r.EffectiveVolume(_project.BulkVolume) - 1.0) > 0.001);
        StatVolume.Text = scaled > 0 ? $"{scaled:N0} scaled" : "";
    }

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
            _project.SetNote(row);
            RefreshProjectLabel();
            Say($"note saved for {row.MediaId} — {_project.Notes.Count} sound(s) annotated " +
                $"in \"{_project.Name}\". Save the project to keep it.");
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

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        StopAutoplay(null);
        _preview.Stop();
    }

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
        RefreshStats();
        BtnDur.IsEnabled = true;
    }

    private void BtnWav_Click(object sender, RoutedEventArgs e) => Export(true);
    private void BtnWem_Click(object sender, RoutedEventArgs e) => Export(false);

    private void Export(bool asWav)
    {
        var row = Selected;
        var rows = SelectedRows;
        if (rows.Count == 0) { Say("select one or more sounds first."); return; }

        // More than one selected asks for a folder instead of a filename -- naming
        // fifty files by hand is not a thing anyone wants to do.
        if (rows.Count > 1)
        {
            var pick = new OpenFolderDialog { Title = $"Export {rows.Count} {(asWav ? "WAV" : "WEM")} file(s) to" };
            if (pick.ShowDialog() != true) return;

            int done = 0, failed = 0;
            foreach (var r in rows)
            {
                var target = Path.Combine(pick.FolderName, Safe($"{r.MediaId}-{r.Display}") + (asWav ? ".wav" : ".wem"));
                if (WriteOne(r, asWav, target)) done++; else failed++;
            }
            Say($"exported {done} file(s) to {pick.FolderName}" + (failed > 0 ? $", {failed} failed" : "") + ".");
            return;
        }

        var one = rows[0];
        var dlg = new SaveFileDialog
        {
            // Round-trips into the drop pane: this name re-assigns itself on the way back in.
            FileName = Safe($"{one.MediaId}-{one.Display}") + (asWav ? ".wav" : ".wem"),
            Filter = asWav ? "WAV|*.wav" : "WEM|*.wem",
        };
        if (dlg.ShowDialog() != true) return;
        Say(WriteOne(one, asWav, dlg.FileName) ? "wrote " + dlg.FileName : "nothing to export.");
    }

    /// <summary>Strip what Windows will not accept in a filename.</summary>
    private static string Safe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private bool WriteOne(SoundRow row, bool asWav, string target)
    {
        var data = _preview.Bytes(_session, _sounds, row);
        if (data is null) return false;
        if (!asWav) { File.WriteAllBytes(target, data); return true; }
        var wav = _preview.Decode(data, row.MediaId, out _);
        if (wav is null) return false;
        File.Copy(wav, target, true);
        return true;
    }

    private void BtnCell_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) { Say("select one or more sounds first."); return; }
        Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(r => r.SheetCell)));
        Say(rows.Count == 1 ? "copied  " + rows[0].SheetCell : $"copied {rows.Count} sheet cells.");
    }

    private void BtnCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_sounds is null) return;
        var rows = (Grid_.ItemsSource as List<SoundRow>) ?? [];
        var dlg = new SaveFileDialog { FileName = $"{_skin.SkinId}.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("bnk,Decimal Wem ID-(FullName),Category,Voice Line / Event,Length,Codec,Source,Tags,Notes,Extra notes");
        foreach (var r in rows)
            sb.AppendLine(string.Join(',', new[]
            {
                r.Bank, r.SheetCell, r.Category, r.Subtitle, r.Length, r.Codec,
                r.Streamed ? "streamed" : "embedded", SoundTags.Flat(r.Tags), r.Notes, r.ExtraNotes,
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
        var dropped = (string[])e.Data.GetData(DataFormats.FileDrop);

        // A bank or a pak is something to LOOK AT, not a replacement to stage. The
        // extension says which, so the same drop target serves both.
        var openable = dropped.Where(ModBank.CanOpen).ToList();
        if (openable.Count > 0)
        {
            if (openable.Count > 1)
                Say($"opening {Path.GetFileName(openable[0])} — drop one bank or pak at a time.");
            OpenModPath(openable[0]);
            return;
        }
        Stage(dropped);
    }

    private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = AudioDecode.DialogFilter, Multiselect = true };
        if (dlg.ShowDialog() == true) Stage(dlg.FileNames);
    }

    /// <summary>
    /// Files or folders. Names decide the assignment: {MediaID}-{note}.{ext}, with
    /// everything from the first hyphen kept only as the author's own reminder.
    /// Anything that is not already a wem is converted on the way in.
    /// </summary>
    private async void Stage(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                                        .Where(AudioDecode.CanDecode));
            else if (AudioDecode.CanDecode(p))
                files.Add(p);
        }
        if (files.Count == 0) { Say("Nothing to stage — no audio files in that drop."); return; }

        // Converting an mp3 means decoding and re-encoding it, which is far too slow
        // to do on the UI thread for a folder full of them.
        var needsWork = files.Any(f => !AudioDecode.IsWem(f));
        if (needsWork) Say($"Converting {files.Count} file(s)…");

        var results = await Task.Run(() => files
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(f => { var item = PendingWem.FromFile(f, out var why); return (item, why); })
            .ToList());

        var added = 0; var replaced = 0;
        var rejected = new List<string>();
        foreach (var (item, why) in results)
        {
            if (item is null) { rejected.Add(why); continue; }

            var existing = _pending.FirstOrDefault(x => x.MediaId == item.MediaId);
            if (existing is not null) { _pending.Remove(existing); replaced++; } else added++;
            _pending.Add(item);
        }

        Reorder();
        RefreshPendingStatus();
        MarkStagedRows();
        Grid_.Items.Refresh();
        RefreshStats();

        var mismatched = _pending.Count(p => p.FormatTag != 0xFFFF);
        var convertedNow = results.Count(r => !string.IsNullOrEmpty(r.item?.Converted));
        Say($"{_pending.Count} staged ({added} new, {replaced} updated)" +
            (convertedNow > 0 ? $", {convertedNow} converted to Vorbis" : "") +
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
        RefreshStats();
        Say($"{_pending.Count} staged.");
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _pending.Clear();
        MarkStagedRows();
        Grid_.Items.Refresh();
        RefreshStats();
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
        RefreshStats();
        Say($"{_pending.Count} staged — {row.MediaId} <- {Path.GetFileName(dlg.FileName)}");
    }

    // ---- numbered test bank ------------------------------------------------

    /// <summary>
    /// Replace every sound in the chosen bank(s) with a clip that speaks a number, and
    /// write a legend. Trigger a sound in game, hear the number, look up what it was.
    /// </summary>
    private void BtnTestBank_Click(object sender, RoutedEventArgs e)
    {
        if (BlockedByOpenedBank()) return;
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
                $"About {required * 0.05:0} second(s)." +
                Environment.NewLine + Environment.NewLine +
                "Output: VORBIS — the same format the game ships. No Wwise install needed.",
                "Generate spoken numbers", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (ask == MessageBoxResult.Cancel) return;
            if (ask == MessageBoxResult.Yes)
            {
                try
                {
                    Tts.GenerateNumbers(_settings.TestWemDir, 0, required - 1, Say);
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

            // Write each assigned number back onto its sound, so hearing "42" in game
            // leads straight to the row it came from. Persisted with the project.
            var tagged = _project.RecordTestNumbers(
                r.Legend.Select(x => (x.Number, x.MediaId)), _sounds.Rows);
            Grid_.Items.Refresh();
            RefreshStats();
            RefreshProjectLabel();

            Say($"test bank: {r.Numbered} numbered, {r.Silenced} silenced, {r.Banks} bank(s) written; " +
                $"{tagged} sound(s) tagged with their number in \"{_project.Name}\".");
            MessageBox.Show(r.Log + Environment.NewLine +
                            "Legend written to test-bank-legend.csv." + Environment.NewLine +
                            $"{tagged} sound(s) now carry their number in the Test # column — " +
                            "save the project to keep it.",
                            "Test bank built", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Say("test bank failed: " + ex.Message); }
    }

    // ---- opening somebody else's bank ---------------------------------------

    /// <summary>The bank currently open from a file or mod pak, or null for the game's own.</summary>
    private ModBank.Bank _openedBank;

    private void BtnOpenBank_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open a soundbank, or a mod pak to pick one from",
            Filter = "Soundbank or mod pak|*.bnk;*.pak;*.utoc|Soundbank|*.bnk|Mod pak|*.pak;*.utoc|All files|*.*",
        };
        if (dlg.ShowDialog() == true) OpenModPath(dlg.FileName);
    }

    /// <summary>
    /// Open a .bnk directly, or mount a .pak and let the author pick which bank inside
    /// it to look at.
    /// </summary>
    private void OpenModPath(string path)
    {
        ModBank.Opened opened;
        try { opened = ModBank.Open(path, _settings); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not open", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var banks = opened.Banks;
        var bank = banks.Count == 1 ? banks[0] : PickBank(banks, Path.GetFileName(path));
        if (bank is null) return;
        ShowBank(bank, banks.Count, opened.Skipped);
    }

    private void ShowBank(ModBank.Bank bank, int siblings, List<string> skipped)
    {
        var sounds = ModBank.ToSounds(_session, bank);
        if (sounds.Rows.Count == 0)
        {
            MessageBox.Show($"{bank.Name} has no embedded media — nothing to show.\n\n" +
                            "Banks that only carry events keep their audio in loose Media/ files.",
                            "Nothing to show", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _openedBank = bank;
        _sounds = sounds;
        _bankFilter = null;
        _project.ApplyNotes(_sounds.Rows);
        MarkStagedRows();
        ApplyFilter();
        BtnCloseBank.IsEnabled = true;

        var named = _sounds.Rows.Count(r => r.EventName is not null);
        var changed = _sounds.Rows.Count(r => r.ModTag is "MODDED" or "NEW");
        var compared = _sounds.Rows.Any(r => r.ModTag is not null);
        Say($"opened {bank.Label} — {_sounds.Rows.Count} media, {named} matched to shipped events" +
            (compared ? $", {changed} changed from the shipped bank" : ", no shipped bank to compare against") +
            (siblings > 1 ? $"  ({siblings} banks in that pak)" : "") +
            (skipped is { Count: > 0 } ? $"  —  {skipped.Count} container(s) skipped" : "") +
            ".  Building is disabled until you close it.");

        // Silently dropping a container the author expected to see would be worse
        // than an extra dialog: they would think the mod simply had no audio.
        if (skipped is { Count: > 0 })
            MessageBox.Show(
                "These could not be opened and were left out:" + Environment.NewLine +
                string.Join(Environment.NewLine, skipped) + Environment.NewLine + Environment.NewLine +
                "Everything else loaded normally.",
                "Some containers skipped", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>A pak usually holds several banks; ask which one rather than guessing.</summary>
    private ModBank.Bank PickBank(List<ModBank.Bank> banks, string source)
    {
        var list = new ListBox
        {
            ItemsSource = banks,
            DisplayMemberPath = nameof(ModBank.Bank.Name),
            SelectedIndex = 0,
            Margin = new Thickness(0, 0, 0, 10),
            Background = (System.Windows.Media.Brush)FindResource("Bg2"),
            Foreground = (System.Windows.Media.Brush)FindResource("Fg"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Line"),
        };
        var ok = new Button { Content = "Open", Width = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new DockPanel { Margin = new Thickness(14) };
        var head = new TextBlock
        {
            Text = $"{banks.Count} banks in {source}",
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = (System.Windows.Media.Brush)FindResource("Fg"),
        };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(head);
        body.Children.Add(buttons);
        body.Children.Add(list);

        var win = new Window
        {
            Title = "Pick a bank",
            Content = body,
            Width = 460,
            Height = 380,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("Bg"),
        };
        ok.Click += (_, _) => { win.DialogResult = true; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is not null) win.DialogResult = true; };

        return win.ShowDialog() == true ? list.SelectedItem as ModBank.Bank : null;
    }

    private async void BtnCloseBank_Click(object sender, RoutedEventArgs e)
    {
        if (_openedBank is null) return;
        _openedBank = null;
        BtnCloseBank.IsEnabled = false;

        if (_skin is null) { _sounds = null; Grid_.ItemsSource = null; Say("closed."); return; }

        Grid_.ItemsSource = null;
        _sounds = await Task.Run(() => SoundIndex.Build(_session, _skin, _charId, Say));
        _project.ApplyNotes(_sounds.Rows);
        MarkStagedRows();
        ApplyFilter();
        Say($"back to {_skin.Label} — {_sounds.Rows.Count} sounds.");
    }

    /// <summary>
    /// True when an action that writes banks was asked for while looking at somebody
    /// else's. Those actions rebuild the GAME's banks, which is not what is on screen.
    /// </summary>
    private bool BlockedByOpenedBank()
    {
        if (_openedBank is null) return false;
        Say($"viewing {_openedBank.Name} — close it first to build against the game's banks.");
        return true;
    }

    private void CbTransProvider_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TransHint is null) return;
        TransHint.Text = LiveTranslate.Describe(CbTransProvider.SelectedItem as string ?? "");
    }

    /// <summary>
    /// Install the offline dictionary, or say it is already here. Kept separate from
    /// the translation providers because it needs no account, no server and no
    /// network after the one download.
    /// </summary>
    private async void BtnDict_Click(object sender, RoutedEventArgs e)
    {
        if (Cedict.IsAvailable)
        {
            MessageBox.Show(
                $"Installed: {Cedict.Count:N0} entries." + Environment.NewLine +
                Cedict.FoundAt() + Environment.NewLine + Environment.NewLine + Cedict.Licence,
                "Offline dictionary", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ask = MessageBox.Show(
            "Download an offline Chinese-English dictionary (about 4 MB)?" +
            Environment.NewLine + Environment.NewLine +
            "It covers the labels the game and the built-in glossary miss, and once it " +
            "is here nothing is ever sent anywhere." + Environment.NewLine + Environment.NewLine +
            Cedict.Licence + Environment.NewLine + Cedict.Source,
            "Offline dictionary", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        try
        {
            await Cedict.DownloadAsync(m => Dispatcher.BeginInvoke(new Action(() => Say(m))));
            if (_sounds is not null)
            {
                foreach (var r in _sounds.Rows) r.CategoryEnglish = null;
                if (SoundRow.ShowTranslatedCategory) TranslateCategories();
                Grid_.Items.Refresh();
            }
            Say($"offline dictionary installed — {Cedict.Count:N0} entries.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Download failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            Say("dictionary download failed: " + ex.Message);
        }
    }

    // ---- setup pickers ---------------------------------------------------------

    /// <summary>
    /// Start a picker where the box already points, so browsing continues from where
    /// the author is rather than from wherever Windows last left off.
    /// </summary>
    private static string StartIn(string current, bool isFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(current)) return null;
            var dir = isFile ? Path.GetDirectoryName(current) : current;
            return Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    private void BrowsePaks_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "The game's Paks folder" };
        if (StartIn(TbPaks.Text, false) is { } d) dlg.InitialDirectory = d;
        if (dlg.ShowDialog() == true) TbPaks.Text = dlg.FolderName;
    }

    private void BrowseUsmap_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Mappings file", Filter = "Mappings|*.usmap|All files|*.*" };
        if (StartIn(TbUsmap.Text, true) is { } d) dlg.InitialDirectory = d;
        if (dlg.ShowDialog() == true) { TbUsmap.Text = dlg.FileName; RefreshUsmapInfo(); }
    }

    /// <summary>What the loaded mappings are, shown where the vgmstream box used to be.</summary>
    private void RefreshUsmapInfo()
    {
        var path = TbUsmap.Text?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            UsmapInfo.Text = "none set — event names will show as hashes";
            return;
        }
        var name = Path.GetFileName(path);
        var build = UsmapFetch.BuildOf(name);
        var release = UsmapFetch.ReleaseOf(name);
        var exists = File.Exists(path);
        UsmapInfo.Text =
            (build is null ? name : $"build {build}" + (release is null ? "" : $" · {release}"))
            + (exists ? "" : "  — FILE MISSING")
            + (UsmapFetch.IsManaged(path) ? "" : "  · your own file");
    }

    private async void BtnFetchUsmap_Click(object sender, RoutedEventArgs e)
    {
        _settings.UsmapPath = TbUsmap.Text.Trim();
        var custom = !string.IsNullOrEmpty(_settings.UsmapPath)
                     && !UsmapFetch.IsManaged(_settings.UsmapPath);
        if (custom)
        {
            var answer = MessageBox.Show(
                "You have picked your own usmap:" + Environment.NewLine +
                Path.GetFileName(_settings.UsmapPath) + Environment.NewLine + Environment.NewLine +
                "Fetch the newest from the community depot and use that instead?" +
                Environment.NewLine + Environment.NewLine +
                "Your file is not deleted — Browse can select it again.",
                "Replace your usmap?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }

        BtnFetchUsmap.IsEnabled = false;
        try
        {
            var r = await UsmapFetch.UpdateAsync(_settings, force: true,
                progress: m => Dispatcher.BeginInvoke(new Action(() => Say(m))));
            if (r.Path is not null) TbUsmap.Text = r.Path;
            RefreshUsmapInfo();
            Say("usmap: " + r.Message);
            if (r.Changed)
                MessageBox.Show(
                    r.Message + Environment.NewLine + Environment.NewLine +
                    "Load the game again to read names with the new mappings.",
                    "Mappings updated", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not fetch the usmap",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { BtnFetchUsmap.IsEnabled = true; }
    }

    private void BrowseVgm_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "vgmstream-cli.exe",
            Filter = "vgmstream-cli|vgmstream-cli.exe|Programs|*.exe|All files|*.*",
        };
        if (StartIn(_settings.VgmstreamPath, true) is { } d) dlg.InitialDirectory = d;
        if (dlg.ShowDialog() == true)
        {
            _settings.VgmstreamPath = dlg.FileName;
            _settings.Save();
            _preview.VgmstreamPath = dlg.FileName;
        }
    }

    // ---- merging two mods ------------------------------------------------------

    /// <summary>
    /// Combine two mods. Each is diffed against the shipped bank so only what its
    /// author actually replaced is carried over, and the priority mod wins any sound
    /// both of them changed.
    /// </summary>
    private async void BtnMerge_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) { Say("load the game first."); return; }

        var filter = "Mod (.bnk or .pak)|*.bnk;*.pak;*.utoc|All files|*.*";
        var first = new OpenFileDialog
        {
            Title = "PRIORITY mod — this one wins any sound both mods changed",
            Filter = filter,
        };
        if (first.ShowDialog() != true) return;

        var second = new OpenFileDialog { Title = "SECONDARY mod", Filter = filter };
        if (second.ShowDialog() != true) return;

        if (string.Equals(first.FileName, second.FileName, StringComparison.OrdinalIgnoreCase))
        { Say("those are the same file."); return; }

        var outDlg = new OpenFolderDialog { Title = "Output folder for the merged mod" };
        if (outDlg.ShowDialog() != true) return;

        var priority = first.FileName;
        var secondary = second.FileName;
        var outRoot = outDlg.FolderName;
        var settings = _settings;
        Say("merging…");

        try
        {
            var r = await Task.Run(() => ModMerge.Build(
                _session, priority, secondary, outRoot, settings,
                Path.Combine(outRoot, "_extracted"),
                m => Dispatcher.BeginInvoke(new Action(() => Say(m)))));

            Say($"merged: {r.Banks} bank(s), {r.FromPriority} from priority, " +
                $"{r.FromSecondary} from secondary, {r.Collisions.Count} collision(s).");

            // The collisions are the part worth reading: they are the sounds where the
            // two authors disagreed, and the only place the result is a judgement call.
            var msg = r.Log + Environment.NewLine +
                      "Each mod's own changed sounds are also in _extracted, named so they " +
                      "can be dropped straight back into the staging pane.";
            MessageBox.Show(msg, "Merged", MessageBoxButton.OK,
                            r.Collisions.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Merge failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            Say("merge failed: " + ex.Message);
        }
    }

    // ---- one file, many sounds -------------------------------------------------

    /// <summary>
    /// Assign a single file to every selected sound. The file is decoded and encoded
    /// ONCE and the same bytes are staged against each id -- fifty targets would
    /// otherwise mean fifty identical encodes of the same audio.
    /// </summary>
    private void StageOneToMany(string path, List<SoundRow> rows)
    {
        var ids = rows.Select(r => r.MediaId).Distinct().ToList();
        if (ids.Count == 0) return;

        var name = Path.GetFileName(path);
        var answer = MessageBox.Show(
            $"Replace all {ids.Count} selected sound(s) with {name}?" +
            Environment.NewLine + Environment.NewLine +
            "Every one of them will play this same audio.",
            "Replace many with one", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        var bytes = PendingWem.ToWemBytes(path, out var why);
        if (bytes is null)
        {
            MessageBox.Show(why, "Could not read that file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var note = Path.GetFileNameWithoutExtension(path);
        int added = 0, replaced = 0;
        foreach (var id in ids)
        {
            var existing = _pending.FirstOrDefault(x => x.MediaId == id);
            if (existing is not null) { _pending.Remove(existing); replaced++; } else added++;
            _pending.Add(PendingWem.ForMedia(id, path, bytes, note));
        }

        Reorder();
        RefreshPendingStatus();
        MarkStagedRows();
        Grid_.Items.Refresh();
        RefreshStats();
        Say($"{name} staged against {ids.Count} sound(s) ({added} new, {replaced} updated) — " +
            $"encoded once, {bytes.Length:N0} B.");
    }

    private void MiReplaceMany_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) { Say("select the sounds to replace first."); return; }

        var dlg = new OpenFileDialog
        {
            Title = $"One file for all {rows.Count} selected sound(s)",
            Filter = AudioDecode.DialogFilter,
        };
        if (dlg.ShowDialog() == true) StageOneToMany(dlg.FileName, rows);
    }

    /// <summary>
    /// Dropping a single audio file onto a selection assigns it to all of them. A
    /// drop onto the grid with nothing selected is ambiguous, so it is sent to the
    /// staging pane, where the filename decides as it always has.
    /// </summary>
    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Grid_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = ((string[])e.Data.GetData(DataFormats.FileDrop))
                    .Where(AudioDecode.CanDecode).ToList();
        if (files.Count == 0) return;

        var rows = SelectedRows;
        if (rows.Count == 0 || files.Count > 1) { Stage(files); return; }
        StageOneToMany(files[0], rows);
    }

    // ---- extracting what a mod changed -----------------------------------------

    /// <summary>
    /// Write out only the sounds an opened bank actually replaced. Everything it left
    /// alone is the game's own audio and is not worth extracting.
    /// </summary>
    private void MiExtractModded_Click(object sender, RoutedEventArgs e)
    {
        if (_openedBank is null || _sounds is null)
        {
            MessageBox.Show(
                "Open a bank or a mod pak first — this writes out the sounds that bank " +
                "changed, which is only known once it has been compared with the game's own.",
                "Nothing opened", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var changed = _sounds.Rows
            .Where(r => r.ModTag is "MODDED" or "NEW")
            .GroupBy(r => r.MediaId)
            .Select(g => g.First())
            .ToList();
        if (changed.Count == 0)
        {
            MessageBox.Show($"{_openedBank.Name} changes nothing from the shipped bank.",
                            "Nothing changed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFolderDialog { Title = $"Where to put {changed.Count} changed sound(s)" };
        if (dlg.ShowDialog() != true) return;

        var dir = Path.Combine(dlg.FolderName,
                               Safe(Path.GetFileNameWithoutExtension(_openedBank.Name)));
        Directory.CreateDirectory(dir);

        int written = 0, failed = 0;
        foreach (var row in changed)
        {
            if (!_sounds.Raw.TryGetValue(row.MediaId, out var bytes)) { failed++; continue; }
            // Named so it drops straight back into the staging pane.
            var note = Safe(row.Display);
            var file = string.IsNullOrEmpty(note) ? $"{row.MediaId}.wem" : $"{row.MediaId}-{note}.wem";
            try { File.WriteAllBytes(Path.Combine(dir, file), bytes); written++; }
            catch { failed++; }
        }

        Say($"extracted {written} changed sound(s) to {dir}" + (failed > 0 ? $", {failed} failed" : "") + ".");
        MessageBox.Show(
            $"{written} sound(s) written to:{Environment.NewLine}{dir}" +
            Environment.NewLine + Environment.NewLine +
            "They are named {MediaID}-{event}, so they can be dropped straight back in.",
            "Extracted", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- tags ------------------------------------------------------------------

    /// <summary>
    /// Build the Tag submenu from the vocabulary each time it opens, with a tick beside
    /// every tag the whole selection already carries. Built in code rather than written
    /// out in XAML so the menu cannot drift away from the codes that get stored.
    /// </summary>
    private void Ctx_Opened(object sender, RoutedEventArgs e) => BuildTagMenu();
    private void MiTags_SubmenuOpened(object sender, RoutedEventArgs e) => BuildTagMenu();

    private void BuildTagMenu()
    {
        MiTags.Items.Clear();
        var rows = SelectedRows;

        // The vocabulary is listed even with nothing selected. Someone opening this
        // menu for the first time is usually asking WHAT the tags are, and an empty
        // submenu answers nothing.
        if (rows.Count == 0)
            MiTags.Items.Add(new MenuItem { Header = "select one or more sounds first", IsEnabled = false });

        string group = null;
        foreach (var tag in SoundTags.All)
        {
            if (group is not null && tag.Group != group) MiTags.Items.Add(new Separator());
            group = tag.Group;

            var all = rows.All(r => r.HasTag(tag.Code));
            var some = !all && rows.Any(r => r.HasTag(tag.Code));
            var item = new MenuItem
            {
                // The meaning rides along in the header: this vocabulary is only useful
                // if the person picking a tag knows what it claims.
                Header = $"[{tag.Code}]   {tag.Meaning}" + (some ? "   (some)" : ""),
                IsCheckable = true,
                IsChecked = all,
                StaysOpenOnClick = true,
                IsEnabled = rows.Count > 0,
            };
            var code = tag.Code;
            item.Click += (_, _) => ToggleTag(code, rows, item.IsChecked);
            MiTags.Items.Add(item);
        }
    }

    /// <summary>
    /// Apply one tag across the selection. A mixed selection becomes all-tagged rather
    /// than flipping each row, because "tag these as ULT" is what was asked for.
    /// </summary>
    private void ToggleTag(string code, List<SoundRow> rows, bool add)
    {
        foreach (var r in rows)
        {
            var tags = SoundTags.Canonical(r.Tags);
            tags.RemoveAll(t => t.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (add) tags.Add(code);
            r.Tags = SoundTags.Canonical(tags);
            _project.SetNote(r);
        }

        Grid_.Items.Refresh();
        RefreshProjectLabel();
        var total = _sounds?.Rows.Count(r => r.HasTag(code)) ?? 0;
        Say($"[{code}] {(add ? "added to" : "removed from")} {rows.Count} sound(s) — " +
            $"{total} tagged [{code}] here. Filter on \"{code}\" to see only those.");
    }

    private void MiClearTags_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) { Say("select one or more sounds first."); return; }
        var cleared = rows.Count(r => r.Tags is { Count: > 0 });
        foreach (var r in rows) { r.Tags = []; _project.SetNote(r); }
        Grid_.Items.Refresh();
        RefreshProjectLabel();
        Say($"tags cleared on {cleared} of {rows.Count} selected sound(s).");
    }

    // ---- columns ---------------------------------------------------------------

    /// <summary>
    /// Off on a first run. Bank repeats what the tree already shows and highlights,
    /// and a length as a bare number is not how anyone compares two clips -- that is
    /// a job for a waveform. Both stay one click away.
    /// </summary>
    internal static readonly string[] HiddenColumnsByDefault = ["Bank", "Len"];

    private void ApplyColumnVisibility()
    {
        _settings.HiddenColumns ??= [.. HiddenColumnsByDefault];
        foreach (var c in Grid_.Columns)
        {
            var header = c.Header as string;
            if (string.IsNullOrEmpty(header)) continue;
            c.Visibility = _settings.HiddenColumns.Contains(header, StringComparer.OrdinalIgnoreCase)
                ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void BtnColumns_Click(object sender, RoutedEventArgs e)
    {
        _settings.HiddenColumns ??= [.. HiddenColumnsByDefault];

        // Rebuilt each time so it always reflects the grid, not a stale copy.
        var menu = new ContextMenu { PlacementTarget = BtnColumns, Placement = PlacementMode.Bottom };
        foreach (var c in Grid_.Columns)
        {
            if (c.Header is not string header || header.Length == 0) continue;
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = c.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };
            var column = c;
            item.Click += (_, _) =>
            {
                if (item.IsChecked)
                {
                    column.Visibility = Visibility.Visible;
                    _settings.HiddenColumns.RemoveAll(h => h.Equals(header, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    column.Visibility = Visibility.Collapsed;
                    if (!_settings.HiddenColumns.Contains(header, StringComparer.OrdinalIgnoreCase))
                        _settings.HiddenColumns.Add(header);
                }
                _settings.Save();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void DropHelp_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.ShowDropHelp = DropHelp.IsExpanded;
        _settings.Save();
    }

    // ---- category translation --------------------------------------------------

    /// <summary>Fill in the English form for every row, once per load.</summary>
    private void TranslateCategories()
    {
        if (_sounds is null || _session is null) return;
        foreach (var r in _sounds.Rows)
            if (r.CategoryEnglish is null && !string.IsNullOrEmpty(r.Category))
                r.CategoryEnglish = CategoryTranslator.Translate(_session, r.Category);
    }

    private void CbTranslate_Click(object sender, RoutedEventArgs e)
    {
        SoundRow.ShowTranslatedCategory = CbTranslate.IsChecked == true;
        if (SoundRow.ShowTranslatedCategory) TranslateCategories();
        Grid_.Items.Refresh();
        RefreshStats();

        if (!SoundRow.ShowTranslatedCategory) { Say("showing the original categories."); return; }

        var left = _sounds is null ? 0
            : CategoryTranslator.Untranslatable(_session, _sounds.Rows.Select(r => r.Category)).Count();
        Say(left == 0
            ? "categories translated."
            : $"categories translated — {left} phrase(s) have no translation. " +
              "Right-click a row to fetch them online.");
    }

    /// <summary>
    /// Send the leftovers to an online translator. Deliberate and confirmed: this is
    /// the only thing in the tool that talks to the internet, and results are cached
    /// so a phrase goes out once.
    /// </summary>
    private async void MiFetchTranslations_Click(object sender, RoutedEventArgs e)
    {
        if (_sounds is null || _session is null) { Say("load a skin first."); return; }

        var missing = CategoryTranslator
            .Untranslatable(_session, _sounds.Rows.Select(r => r.Category)).ToList();
        if (missing.Count == 0) { Say("nothing left to translate."); return; }

        var why = LiveTranslate.WhyNot(_settings);
        if (why is not null)
        {
            MessageBox.Show(
                $"Cannot translate: {why}." + Environment.NewLine + Environment.NewLine +
                "Pick one in Setup:" + Environment.NewLine +
                string.Join(Environment.NewLine,
                    LiveTranslate.Providers.Where(p => p != LiveTranslate.ProviderNone)
                                 .Select(p => "  " + LiveTranslate.Describe(p))),
                "No translator configured", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var local = LiveTranslate.IsLocal(_settings.TranslateProvider);
        var ask = MessageBox.Show(
            $"{missing.Count} phrase(s) have no translation in the game or the built-in glossary." +
            Environment.NewLine + Environment.NewLine +
            $"Send them to {LiveTranslate.Destination(_settings)}?" + Environment.NewLine +
            Environment.NewLine +
            (local
                ? "This is your own machine, so nothing leaves it."
                : "Only these Chinese label texts are sent — nothing else about you or your files.") +
            " Results are saved locally, so each phrase is sent once." + Environment.NewLine +
            Environment.NewLine +
            string.Join(Environment.NewLine, missing.Take(8)) +
            (missing.Count > 8 ? Environment.NewLine + $"… and {missing.Count - 8} more" : ""),
            "Translate", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        try
        {
            var added = await LiveTranslate.FetchAsync(_settings, missing,
                m => Dispatcher.BeginInvoke(new Action(() => Say(m))));
            foreach (var r in _sounds.Rows) r.CategoryEnglish = null;
            TranslateCategories();
            Grid_.Items.Refresh();
            Say($"fetched {added} of {missing.Count} phrase(s); {LiveTranslate.Count} cached in total.");
        }
        catch (Exception ex) { Say("translation failed: " + ex.Message); }
    }

    // ---- volume ---------------------------------------------------------------

    /// <summary>
    /// Ask for a multiplier. WPF has no input box, and a whole dialog file for one
    /// number is not worth it.
    /// </summary>
    private double? AskGain(string title, string message, double start)
    {
        var box = new TextBox
        {
            Text = start.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 8, 0, 12),
        };
        var ok = new Button { Content = "Apply", Width = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(14) };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(box);
        body.Children.Add(new TextBlock
        {
            Text = $"1.0 leaves it alone. Range {VolumeTool.MinGain:0.0##}–{VolumeTool.MaxGain:0.0#}. " +
                   "Above 1.0 can clip; the build log says how much.",
            Foreground = (System.Windows.Media.Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        body.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Content = body,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("Bg"),
        };
        ok.Click += (_, _) => { win.DialogResult = true; };
        box.Focus();
        box.SelectAll();

        if (win.ShowDialog() != true) return null;
        if (!double.TryParse(box.Text.Trim().Replace(',', '.'),
                             System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var g))
        {
            Say($"\"{box.Text}\" is not a number.");
            return null;
        }
        return Math.Clamp(g, VolumeTool.MinGain, VolumeTool.MaxGain);
    }

    /// <summary>Set the level on the selected sounds, marking them as hand-set.</summary>
    private void MiSetVolume_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) { Say("select one or more sounds first."); return; }

        var start = rows[0].Volume ?? _project.BulkVolume;
        var gain = AskGain("Set volume", $"Multiplier for {rows.Count} selected sound(s):", start);
        if (gain is null) return;

        foreach (var r in rows) { r.Volume = gain; r.VolumeByHand = true; _project.SetNote(r); }
        Grid_.Items.Refresh();
        RefreshStats();
        RefreshProjectLabel();
        Say($"{rows.Count} sound(s) set to {gain:0.0##}x by hand — a bulk change will now skip them.");
    }

    /// <summary>
    /// Set the level for everything that was not set by hand. This is the "make the
    /// whole mod quieter" control; sounds someone tuned individually keep their own.
    /// </summary>
    private void MiSetAllVolume_Click(object sender, RoutedEventArgs e)
    {
        if (_sounds is null) { Say("open a skin or a bank first."); return; }

        var byHand = _sounds.Rows.Count(r => r.VolumeByHand);
        var gain = AskGain("Change all volumes",
            $"Multiplier for every sound{(byHand > 0 ? $", except the {byHand} set by hand" : "")}:",
            _project.BulkVolume);
        if (gain is null) return;

        _project.BulkVolume = gain.Value;
        var moved = 0;
        foreach (var r in _sounds.Rows.Where(r => !r.VolumeByHand))
        {
            r.Volume = Math.Abs(gain.Value - 1.0) < 0.001 ? null : gain;
            _project.SetNote(r);
            moved++;
        }
        _project.Touch();
        Grid_.Items.Refresh();
        RefreshStats();
        RefreshProjectLabel();
        Say($"{moved} sound(s) now at {gain:0.0##}x" +
            (byHand > 0 ? $"; {byHand} hand-set left alone" : "") + ".");
    }

    private void MiClearVolume_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) { Say("select one or more sounds first."); return; }
        foreach (var r in rows) { r.Volume = null; r.VolumeByHand = false; _project.SetNote(r); }
        Grid_.Items.Refresh();
        RefreshStats();
        RefreshProjectLabel();
        Say($"{rows.Count} sound(s) back to following the bulk level ({_project.BulkVolume:0.0##}x).");
    }

    /// <summary>
    /// Write banks with every level applied. The output folder carries the bulk level,
    /// so exporting at 1.0, 0.8 and 0.6 gives three folders rather than one overwritten
    /// three times.
    /// </summary>
    private async void MiBuildVolume_Click(object sender, RoutedEventArgs e)
    {
        if (BlockedByOpenedBank()) return;
        if (_sounds is null || _skin is null) { Say("pick a skin first."); return; }

        var levels = _sounds.Rows.Where(r => r.EffectiveVolume(_project.BulkVolume) is var g &&
                                             Math.Abs(g - 1.0) > 0.001)
                                 .GroupBy(r => r.MediaId)
                                 .ToDictionary(g => g.Key, g => g.First().EffectiveVolume(_project.BulkVolume));
        if (levels.Count == 0)
        {
            MessageBox.Show("Every sound is at 1.0, so there is nothing to write.\n\n" +
                            "Set a level on a selection, or use \"Change all volumes\".",
                            "Nothing to scale", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFolderDialog { Title = "Output folder for the volume build" };
        if (dlg.ShowDialog() != true) return;
        var outRoot = Path.Combine(dlg.FolderName, VolumeTool.Label(_project.BulkVolume));

        var banks = BanksFor(levels.Keys.ToList());
        var vgm = _settings.VgmstreamPath;
        Say($"scaling {levels.Count} sound(s) across {banks.Count} bank(s)…");

        try
        {
            var r = await Task.Run(() => VolumeBankBuilder.Build(
                _session, banks, id => levels.GetValueOrDefault(id, 1.0), outRoot,
                _sounds, vgm, m => Dispatcher.BeginInvoke(new Action(() => Say(m)))));

            Say($"volume build: {r.Scaled} scaled, {r.Failed} failed, {r.Banks} bank(s) -> {outRoot}");
            MessageBox.Show(r.Log + Environment.NewLine +
                            "Levels written to volume-legend.csv.",
                            "Volume build", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Say("volume build failed: " + ex.Message); }
    }

    // ---- autoplay ------------------------------------------------------------

    /// <summary>Rows still to play, and where we are in them.</summary>
    private List<SoundRow> _queue;
    private int _queueAt = -1;

    private bool AutoplayRunning => _queue is not null;

    private void BtnAuto_Click(object sender, RoutedEventArgs e)
    {
        if (AutoplayRunning) { StopAutoplay("autoplay stopped."); return; }

        // A multi-row selection is the queue. Otherwise play everything listed, from
        // wherever the cursor is -- scanning a character's lines is the common case,
        // and starting over from the top every time would be useless.
        var listed = (Grid_.ItemsSource as List<SoundRow>) ?? [];
        var picked = SelectedRows;
        List<SoundRow> queue;
        if (picked.Count > 1) queue = picked;
        else
        {
            var from = picked.Count == 1 ? listed.IndexOf(picked[0]) : 0;
            queue = listed.Skip(Math.Max(0, from)).ToList();
        }

        if (queue.Count == 0) { Say("nothing to play."); return; }
        StartAutoplay(queue);
    }

    private void MiPlayFrom_Click(object sender, RoutedEventArgs e)
    {
        var listed = (Grid_.ItemsSource as List<SoundRow>) ?? [];
        var picked = SelectedRows;
        if (picked.Count == 0) { Say("select a sound first."); return; }

        var queue = picked.Count > 1
            ? picked
            : listed.Skip(Math.Max(0, listed.IndexOf(picked[0]))).ToList();
        if (queue.Count == 0) { Say("nothing to play."); return; }
        StartAutoplay(queue);
    }

    private void StartAutoplay(List<SoundRow> queue)
    {
        if (!_autoplayHooked)
        {
            _preview.Finished += () => Dispatcher.BeginInvoke(new Action(PlayNext));
            _autoplayHooked = true;
        }
        _queue = queue;
        _queueAt = -1;
        BtnAuto.Content = "■  Stop autoplay";
        Say($"autoplay: {queue.Count} sound(s).");
        PlayNext();
    }

    private void StopAutoplay(string message)
    {
        _queue = null;
        _queueAt = -1;
        BtnAuto.Content = "▶▶  Autoplay";
        _preview.Stop();
        if (message is not null) Say(message);
    }

    /// <summary>
    /// Advance to the next playable row. Rows whose bytes cannot be read are skipped
    /// rather than ending the run -- one bad entry in four hundred should not stop a
    /// listening pass.
    /// </summary>
    private void PlayNext()
    {
        if (_queue is null) return;

        while (++_queueAt < _queue.Count)
        {
            var row = _queue[_queueAt];
            var data = _preview.Bytes(_session, _sounds, row);
            var wav = data is null ? null : _preview.Decode(data, row.MediaId, out _);
            if (wav is null) continue;

            // Follow along in the grid, so what you hear is what is highlighted.
            Grid_.SelectedItem = row;
            Grid_.ScrollIntoView(row);
            _preview.Play(wav);
            Say($"autoplay {_queueAt + 1}/{_queue.Count}  —  {row.TestLabel} {row.Display}".Replace("  ", " ").Trim() +
                (string.IsNullOrEmpty(row.Subtitle) ? "" : $"   \"{row.Subtitle}\""));
            return;
        }

        StopAutoplay($"autoplay finished — {_queue?.Count ?? 0} sound(s).");
    }

    /// <summary>
    /// Number the selection and silence everything else in the same banks, so only
    /// the sounds you picked can be heard. With several sounds firing at once this is
    /// the fastest way to find out which of them is which.
    /// </summary>
    private void MiTestOnlySelection_Click(object sender, RoutedEventArgs e)
    {
        if (BlockedByOpenedBank()) return;
        var rows = SelectedRows;
        if (_sounds is null || rows.Count == 0) { Say("select one or more sounds first."); return; }

        var ids = rows.Select(r => r.MediaId).Distinct().ToHashSet();
        var banks = BanksFor(ids);

        // Everything embedded in those banks that is NOT selected gets silence.
        var others = new HashSet<uint>();
        foreach (var bp in banks)
        {
            if (!_session.Provider.Files.TryGetValue(bp, out var f)) continue;
            try
            {
                var didx = BnkBuilder.ReadSections(f.Read()).FirstOrDefault(s => s.Tag == "DIDX");
                if (didx is null) continue;
                foreach (var entry in BnkBuilder.ReadDidx(didx.Body))
                    if (!ids.Contains(entry.Id)) others.Add(entry.Id);
            }
            catch { }
        }

        if (MessageBox.Show(
                $"Number {ids.Count} selected sound(s) and SILENCE the other {others.Count} " +
                $"in {banks.Count} bank(s)." + Environment.NewLine + Environment.NewLine +
                "Only what you picked will be audible." + Environment.NewLine + Environment.NewLine +
                "Continue?",
                "Test only the selection", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK) return;

        var wems = EnsureNumberedClips(ids.Count);
        if (wems is null) return;

        var outDlg = new OpenFolderDialog { Title = "Output folder for the test build" };
        if (outDlg.ShowDialog() != true) return;

        try
        {
            var keep = _sounds.Rows.Where(r => r.TestNumber is not null && ids.Contains(r.MediaId))
                                   .GroupBy(r => r.MediaId)
                                   .ToDictionary(g => g.Key, g => g.First().TestNumber!.Value);
            // Muted rows inside the selection stay muted; the rest of the bank joins them.
            foreach (var id in _sounds.Rows.Where(r => r.Muted).Select(r => r.MediaId)) others.Add(id);

            var r = TestBankBuilder.Build(_session, banks, wems, outDlg.FolderName,
                                          wems.ByNumber.Keys.Min(), false, _sounds,
                                          false, 0, null, others, keep);
            var tagged = _project.RecordTestNumbers(
                r.Legend.Select(x => (x.Number, x.MediaId)), _sounds.Rows);
            Grid_.Items.Refresh();
            RefreshStats();
            RefreshProjectLabel();

            Say($"test bank: {r.Numbered} numbered, {r.Silenced} silenced, {r.Banks} bank(s); " +
                $"{tagged} tagged in the project.");
            MessageBox.Show(r.Log + Environment.NewLine +
                            "Legend written to test-bank-legend.csv." + Environment.NewLine +
                            "Everything you did not select is silent in this build.",
                            "Test bank built", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Say("test bank failed: " + ex.Message); }
    }

    // ---- actions on the selected sounds -------------------------------------

    private List<SoundRow> SelectedRows => Grid_.SelectedItems.OfType<SoundRow>().ToList();

    /// <summary>Banks embedding any of these ids — the only ones worth rewriting.</summary>
    private List<string> BanksFor(IReadOnlyCollection<uint> ids)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
            foreach (var b in _session.BanksForMedia(id)) paths.Add(b);
        // Without the global index, fall back to the open skin's own banks.
        if (paths.Count == 0 && _skin is not null)
            foreach (var b in _skin.Banks) paths.Add(b.Path);
        return paths.ToList();
    }

    /// <summary>
    /// Make sure the clip folder holds at least <paramref name="required"/> numbered
    /// clips, offering to speak them if not. Returns null when the user backs out.
    /// </summary>
    private TestBankBuilder.NumberedWems EnsureNumberedClips(int required)
    {
        if (string.IsNullOrWhiteSpace(_settings.TestWemDir) || !Directory.Exists(_settings.TestWemDir))
        {
            var pick = new OpenFolderDialog { Title = "Folder for the numbered clips  ({number}.wem)" };
            if (pick.ShowDialog() != true) return null;
            _settings.TestWemDir = pick.FolderName;
            _settings.Save();
        }

        var wems = TestBankBuilder.ScanFolder(_settings.TestWemDir);
        if (wems.ByNumber.Count >= required) return wems;

        if (!Tts.IsAvailable)
        {
            MessageBox.Show($"This needs {required} numbered clip(s) and the folder has " +
                            $"{wems.ByNumber.Count}. Windows speech is unavailable, so add " +
                            "clips named 0.wem, 1.wem … to that folder.",
                            "Not enough clips", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var ask = MessageBox.Show(
            $"This needs {required} numbered clip(s); the folder has {wems.ByNumber.Count}." +
            Environment.NewLine + Environment.NewLine +
            $"Generate 0-{required - 1} now with the Windows speech voice?" +
            Environment.NewLine + Environment.NewLine +
            "Output: VORBIS — the same format the game ships. No Wwise install needed.",
            "Generate spoken numbers", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return null;

        try
        {
            Tts.GenerateNumbers(_settings.TestWemDir, 0, required - 1, Say);
            return TestBankBuilder.ScanFolder(_settings.TestWemDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Speech generation failed",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    /// <summary>
    /// Numbers only the selected sounds. Everything else keeps its shipped audio, so
    /// the build stays usable in a real match instead of turning the whole character
    /// into a counting exercise.
    /// </summary>
    private void MiTestSelection_Click(object sender, RoutedEventArgs e)
    {
        if (BlockedByOpenedBank()) return;
        var rows = SelectedRows;
        if (_sounds is null || rows.Count == 0) { Say("select one or more sounds first."); return; }

        var ids = rows.Select(r => r.MediaId).Distinct().ToHashSet();

        // Numbers already handed out are kept, so notes written after the first run
        // still point at the same sounds. Muted ones are silenced but keep their number.
        var keep = _sounds.Rows.Where(r => r.TestNumber is not null && ids.Contains(r.MediaId))
                               .GroupBy(r => r.MediaId)
                               .ToDictionary(g => g.Key, g => g.First().TestNumber!.Value);
        var muted = _sounds.Rows.Where(r => r.Muted && ids.Contains(r.MediaId))
                                .Select(r => r.MediaId).ToHashSet();

        var wems = EnsureNumberedClips(ids.Count);
        if (wems is null) return;

        var outDlg = new OpenFolderDialog { Title = "Output folder for the test build" };
        if (outDlg.ShowDialog() != true) return;

        try
        {
            var lowest = wems.ByNumber.Keys.Min();
            var r = TestBankBuilder.Build(_session, BanksFor(ids), wems, outDlg.FolderName,
                                          lowest, false, _sounds, false, 0, ids, muted, keep);
            var tagged = _project.RecordTestNumbers(
                r.Legend.Select(x => (x.Number, x.MediaId)), _sounds.Rows);
            Grid_.Items.Refresh();
            RefreshStats();
            RefreshProjectLabel();

            Say($"test bank: {r.Numbered} numbered, {r.Silenced} muted, across {r.Banks} bank(s); " +
                $"{tagged} tagged in the project.");
            MessageBox.Show(r.Log + Environment.NewLine +
                            "Legend written to test-bank-legend.csv." + Environment.NewLine +
                            (keep.Count > 0
                                ? $"{keep.Count} sound(s) kept the number they had, so earlier notes still match."
                                    + Environment.NewLine
                                : "") +
                            (muted.Count > 0
                                ? $"{muted.Count} muted sound(s) are silent this run — their numbers are still in the legend."
                                    + Environment.NewLine
                                : "") +
                            "Every sound you did not select keeps its normal audio.",
                            "Test bank built", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Say("test bank failed: " + ex.Message); }
    }

    /// <summary>Replaces the selected sounds with silence of the same length and format.</summary>
    private void MiSilentSelection_Click(object sender, RoutedEventArgs e)
    {
        if (BlockedByOpenedBank()) return;
        var rows = SelectedRows;
        if (_sounds is null || rows.Count == 0) { Say("select one or more sounds first."); return; }

        var ids = rows.Select(r => r.MediaId).Distinct().ToHashSet();
        var tested = rows.Where(r => r.TestNumber is not null).Select(r => r.MediaId).Distinct().Count();
        if (tested > 0 && MessageBox.Show(
                $"{tested} of the selected sound(s) carry a test number. Silencing them " +
                "means you will not hear their number in game any more." +
                Environment.NewLine + Environment.NewLine + "Continue?",
                "Already tested", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK) return;

        var outDlg = new OpenFolderDialog { Title = "Output folder for the silent build" };
        if (outDlg.ShowDialog() != true) return;

        try
        {
            var r = SilentBankBuilder.Build(_session, BanksFor(ids), ids, outDlg.FolderName, _sounds);

            // Marked the way soundKit marks them, so the note says what happened to it.
            var marked = 0;
            foreach (var row in _sounds.Rows.Where(x => ids.Contains(x.MediaId)))
            {
                if ((row.Notes ?? "").Contains("(Mute)", StringComparison.OrdinalIgnoreCase)) continue;
                row.Notes = string.IsNullOrWhiteSpace(row.Notes) ? "(Mute)" : row.Notes + " (Mute)";
                _project.SetNote(row);
                marked++;
            }
            Grid_.Items.Refresh();
            RefreshStats();
            RefreshProjectLabel();

            Say($"silent bank: {r.Silenced} sound(s) silenced across {r.Banks} bank(s); " +
                $"{marked} marked (Mute) in the project.");
            MessageBox.Show(r.Log + Environment.NewLine +
                            "Legend written to silent-bank-legend.csv.",
                            "Silent bank built", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Say("silent bank failed: " + ex.Message); }
    }

    /// <summary>
    /// Mute or unmute the selection for test builds. When several sounds fire at once
    /// only the loudest is intelligible; silencing the one you already identified is
    /// how the ones underneath it become audible on the next run.
    /// </summary>
    private void MiToggleMute_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) { Say("select one or more sounds first."); return; }

        // A mixed selection becomes all-muted rather than flipping each one, which is
        // what "mute these" means when some already are.
        var turnOn = rows.Any(r => !r.Muted);
        foreach (var r in rows) { r.Muted = turnOn; _project.SetNote(r); }

        Grid_.Items.Refresh();
        RefreshStats();
        RefreshProjectLabel();
        var muted = _sounds?.Rows.Count(r => r.Muted) ?? 0;
        Say($"{rows.Count} sound(s) {(turnOn ? "muted" : "unmuted")} — {muted} muted in total. " +
            "Rebuild the test bank to hear the difference.");
    }

    private void MiReplaceSelection_Click(object sender, RoutedEventArgs e) => BtnReplace_Click(sender, e);

    private void MiCopyIds_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectedRows.Select(r => r.MediaId).Distinct().ToList();
        if (ids.Count == 0) { Say("select one or more sounds first."); return; }
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, ids));
            Say($"copied {ids.Count} media id(s).");
        }
        catch (Exception ex) { Say("could not copy: " + ex.Message); }
    }

    // ---- build -------------------------------------------------------------


    /// <summary>
    /// Rebuild every bank of this skin that embeds a staged media, and drop streamed
    /// replacements as loose wems. Output mirrors the pak layout so the folder can go
    /// straight to repak, rather than a flat bucket the author has to re-sort.
    /// </summary>
    private void BtnBuild_Click(object sender, RoutedEventArgs e)
    {
        if (BlockedByOpenedBank()) return;
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

    // ---- projects ----------------------------------------------------------

    private void RefreshProjectLabel()
    {
        ProjectLabel.Text = _project.Title;
        ProjectPathLabel.Text = _project.IsSaved ? _project.Path : "(not saved yet)";
        Title = $"XzoundWave — {_project.Title}";
    }

    /// <summary>Pull the current UI state into the project before saving.</summary>
    private void CaptureIntoProject()
    {
        _project.Character = _charId ?? "";
        _project.Skin = _skin?.SkinId ?? "";
        _project.Bank = _bankFilter ?? "";
        _project.Language = _settings.Language;
        _project.Replacements = _pending
            .Select(p => new ProjectReplacement
            {
                MediaId = p.MediaId,
                File = _project.ToStoredPath(p.FullPath),
                Note = p.Note,
            }).ToList();
    }

    /// <summary>Re-stage a project's replacements, reporting any files that moved.</summary>
    private void RestoreFromProject()
    {
        _pending.Clear();
        var missing = new List<string>();
        foreach (var r in _project.Replacements)
        {
            var full = _project.ToFullPath(r.File);
            var item = File.Exists(full) ? PendingWem.FromFile(full, out _) : null;
            if (item is null) { missing.Add(r.File); continue; }
            _pending.Add(item);
        }
        Reorder();
        RefreshPendingStatus();
        MarkStagedRows();
        Grid_.Items.Refresh();
        RefreshStats();
        RefreshProjectLabel();

        if (missing.Count > 0)
            MessageBox.Show(
                "These staged files could not be found and were dropped:" +
                Environment.NewLine + string.Join(Environment.NewLine, missing.Take(15)),
                "Missing files", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Returns false if the user cancels out of an unsaved-changes prompt.</summary>
    private bool ConfirmDiscard()
    {
        if (!_project.IsDirty) return true;
        var answer = MessageBox.Show($"\"{_project.Name}\" has unsaved changes. Save it first?",
                                     "Unsaved changes", MessageBoxButton.YesNoCancel,
                                     MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return SaveProject(false);
        return true;
    }

    private bool SaveProject(bool forcePrompt)
    {
        CaptureIntoProject();
        if (forcePrompt || !_project.IsSaved)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save project",
                Filter = $"XzoundWave project (*{Project.Extension})|*{Project.Extension}",
                FileName = (string.IsNullOrWhiteSpace(_project.Name) ? "project" : _project.Name)
                           + Project.Extension,
            };
            if (dlg.ShowDialog() != true) return false;
            _project.Name = Path.GetFileNameWithoutExtension(dlg.FileName);
            _project.Save(dlg.FileName);
            // Re-store paths now that there is a project folder to be relative to.
            CaptureIntoProject();
            _project.Save();
        }
        else _project.Save();

        _settings.NoteRecentProject(_project.Path);
        _settings.Save();
        RefreshProjectLabel();
        Say($"saved {_project.Path}");
        return true;
    }

    private void BtnProjectNew_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        _project = Project.New();
        _pending.Clear();
        if (_sounds is not null)
        {
            foreach (var r in _sounds.Rows) { r.Notes = ""; r.ExtraNotes = ""; r.ReplacementPath = null; }
            Grid_.Items.Refresh();
            RefreshStats();
        }
        RefreshPendingStatus();
        RefreshProjectLabel();
        Say("new project.");
    }

    private void BtnProjectOpen_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog
        {
            Title = "Open project",
            Filter = Project.OpenFilter,
        };
        if (dlg.ShowDialog() != true) return;
        OpenProject(dlg.FileName, announce: true);
    }

    private void OpenProject(string path, bool announce)
    {
        try { _project = Project.Load(path); }
        catch (Exception ex)
        {
            Say("could not open project: " + ex.Message);
            if (announce)
                MessageBox.Show(ex.Message, "Could not open project",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.NoteRecentProject(path);
        _settings.Save();
        RestoreFromProject();
        if (_sounds is not null) { _project.ApplyNotes(_sounds.Rows); Grid_.Items.Refresh();
 RefreshStats(); }
        SelectFromProject();
        if (announce)
            Say($"opened \"{_project.Name}\" — {_project.Replacements.Count} staged file(s), " +
                $"{_project.Notes.Count} note(s).");
    }

    /// <summary>Reselect the character/skin/bank the project was left on.</summary>
    private void SelectFromProject()
    {
        if (string.IsNullOrEmpty(_project.Skin) || Tree.Items.Count == 0) return;
        foreach (TreeViewItem charNode in Tree.Items)
        foreach (TreeViewItem skinNode in charNode.Items)
        {
            if (skinNode.Tag is not Pick p || p.Skin.SkinId != _project.Skin) continue;
            charNode.IsExpanded = true;
            if (!string.IsNullOrEmpty(_project.Bank))
            {
                skinNode.IsExpanded = true;
                foreach (TreeViewItem bankNode in skinNode.Items)
                    if (bankNode.Tag is Pick bp && bp.Bank == _project.Bank)
                    { bankNode.IsSelected = true; return; }
            }
            skinNode.IsSelected = true;
            return;
        }
    }

    private void BtnProjectSave_Click(object sender, RoutedEventArgs e) => SaveProject(false);
    private void BtnProjectSaveAs_Click(object sender, RoutedEventArgs e) => SaveProject(true);
}
