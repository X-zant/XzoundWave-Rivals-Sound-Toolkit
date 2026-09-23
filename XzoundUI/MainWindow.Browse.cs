using System.IO;
using Avalonia.Threading;

namespace MRAudioKit.UI;

/// <summary>
/// Browse: hearing what a row actually is.
///
/// Everything here is one question asked in different ways -- what does this sound
/// like? The answer depends on two switches that are easy to miss, so both are made
/// visible rather than assumed: a file staged over a row is what plays, because that
/// is what will ship; and "Ingame" decides whether you are hearing the FILE or the
/// SOUND, which are different whenever the .bnk trims, pitches, delays or loops it.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// wem -> wav -> speakers. One per window: the decode writes into a temp folder
    /// keyed by media id, so a second instance would race the first over the same
    /// files.
    /// </summary>
    private readonly AudioPreview _preview = new();

    private bool _previewHooked;
    private bool _outputChecked;
    private string _outputProblem;
    private bool _measuring;

    /// <summary>
    /// The preview, with the things that can only be settled after the game is loaded.
    ///
    /// VgmstreamPath is one of them: Settings.AutoDetect unpacks the bundled decoder
    /// and writes the path, and that runs in LoadGame, long after this field is built.
    /// Setting it in a field initialiser would leave a fresh install silent with
    /// nothing to point at, so it is read here, on every use.
    /// </summary>
    private AudioPreview Preview()
    {
        _preview.VgmstreamPath = Config.VgmstreamPath;
        if (_previewHooked) return _preview;
        _previewHooked = true;

        // A failure to play used to be swallowed entirely, so "no sound" looked the
        // same as a broken decoder. Say it once, where someone is already looking.
        _preview.PlaybackFailed += msg =>
            Dispatcher.UIThread.Post(() => Say("playback failed: " + msg));

        // Autoplay advances on this and nothing else. Durations are unknown until a
        // clip has been measured, and polling for "is it done yet" gets the gaps
        // wrong in both directions. The output raises it from its own thread, so it
        // is posted rather than handled where it lands.
        _preview.Finished += () => Dispatcher.UIThread.Post(PlayNext);

        Closed += (_, _) => _preview.Dispose();
        return _preview;
    }

    // ---- the buttons ---------------------------------------------------------

    private IEnumerable<ToolButton> BrowseTools()
    {
        yield return new ToolButton(
            "▶  Play",
            () => PlayRow(CurrentRow, original: false),
            Key: true,
            Hint: "Play the selected sound. A file staged over it plays instead of the game's, because that is what will ship.");

        yield return new ToolButton(
            AutoplayRunning ? "■  Stop autoplay" : "▶▶  Autoplay",
            ToggleAutoplay,
            Hint: "Play the selection in turn, or everything listed from the cursor down.");

        yield return new ToolButton(
            "■  Stop",
            StopPlayback,
            Hint: "Stop the sound and end any autoplay run.");

        yield return new ToolButton(
            "Measure durations",
            () => _ = MeasureDurationsAsync(),
            Hint: "Read the length of every listed sound from the game's own file.");

        yield return new ToolButton(
            Config.PreviewAsInGame ? "Ingame  ✓" : "Ingame",
            ToggleInGame,
            Hint: "Apply what the .bnk does to the sound -- volume, pitch, delay and loop -- instead of playing the file as it is.");
    }

    /// <summary>
    /// Redraw the toolbar, for a button whose label carries state.
    ///
    /// The buttons are built fresh every time the toolbar is shown and nothing keeps a
    /// reference to one, so this is how "Autoplay" becomes "Stop autoplay". Only the
    /// Browse tab's labels change, so only that tab is worth rebuilding.
    /// </summary>
    private void BrowseRedraw()
    {
        // The same range ShowToolbar's default branch covers. A ListBox tab strip can
        // be ctrl+clicked back to nothing selected, which still shows Browse's buttons
        // -- and would have frozen "Autoplay" on a label that no longer matched.
        if (Tabs.SelectedIndex <= 0) ShowToolbar();
    }

    // ---- playing one row -----------------------------------------------------

    /// <param name="original">
    /// Hear what the game shipped rather than what is staged over it. The old window
    /// reached this from a right-click item; this grid has no context menu yet, so the
    /// route is kept here waiting for one rather than the behaviour being dropped.
    /// </param>
    private void PlayRow(SoundRow row, bool original)
    {
        if (Sounds is null) { Say("pick a .bnk in the tree first."); return; }
        if (row is null) { Say("select a sound first."); return; }

        var preview = Preview();
        var data = preview.Bytes(Session, Sounds, row, ignoreStaged: original);
        if (data is null) { Say($"media {row.MediaId}: no bytes found."); return; }

        var wav = preview.Decode(data, row.MediaId, out var err);
        if (wav is null) { Say(err); return; }

        // The switch decides whether this is the file or the sound. Both are worth
        // hearing: one is what you are shipping, the other is what will be heard.
        var applied = Config.PreviewAsInGame ? row.Applied : null;
        preview.Play(wav, applied, out var did);

        var note = Config.PreviewAsInGame ? $"  —  ingame: {did}" + NotReproduced(row) : "";

        // data.Length rather than row.Bytes: Bytes() sets row.Bytes on every path that
        // reads the game, but NOT on the staged one -- so a staged row was announced
        // with the staged path and the GAME file's size, two halves of two different
        // sounds in one sentence. What played is what is counted.
        Say($"{row.Display}  —  {data.Length:N0} B from {row.Origin}{note}{SilenceNote()}");
    }

    /// <summary>
    /// Name what the preview could not reproduce. Without it, a sound that is quiet in
    /// game and loud here reads as a bug in the tool rather than as a filter it never
    /// claimed to apply.
    /// </summary>
    private static string NotReproduced(SoundRow row)
    {
        var missing = InGameChain.Unapplied(row.Applied);
        return missing.Length == 0 ? "" : $"   (not reproduced: {missing})";
    }

    private void ToggleInGame()
    {
        Config.PreviewAsInGame = !Config.PreviewAsInGame;
        Config.Save();
        Say(Config.PreviewAsInGame
            ? "previews now include what the .bnk applies — volume, pitch, delay and loop."
            : "previews now play the file as it is.");
        BrowseRedraw();
    }

    private void StopPlayback()
    {
        StopAutoplay(null);
        _preview.Stop();
        Say("stopped.");
    }

    /// <summary>
    /// Why nothing can be heard, or null when it can.
    ///
    /// Worked out once and kept: asking constructs an output device, which is not
    /// something to do per row. This build resolves to NoAudio on net10.0, so the
    /// honest answer today is that decoding works and playback does not.
    /// </summary>
    private string NoSoundReason()
    {
        if (_outputChecked) return _outputProblem;
        _outputChecked = true;
        _outputProblem = AudioPreview.OutputProblem();
        return _outputProblem;
    }

    private string SilenceNote() =>
        NoSoundReason() is { } why ? $"   (heard nothing: {why})" : "";

    // ---- autoplay ------------------------------------------------------------

    /// <summary>Rows still to play, and where we are in them.</summary>
    private List<SoundRow> _queue;
    private int _queueAt = -1;

    /// <summary>
    /// What the run actually managed, so the end of it can be honest.
    ///
    /// Skipping a row that will not decode is right, but a run where EVERY row was
    /// skipped -- no decoder unpacked, a bank whose media is missing -- finished in a
    /// blink and still said "autoplay finished — 412 sound(s)", which reads as 412
    /// sounds played. The count and the first reason are kept so it can say what
    /// happened instead.
    /// </summary>
    private int _queuePlayed;
    private string _queueTrouble;

    private bool AutoplayRunning => _queue is not null;

    private void ToggleAutoplay()
    {
        if (AutoplayRunning) { StopAutoplay("autoplay stopped."); return; }
        if (Sounds is null) { Say("pick a .bnk in the tree first."); return; }

        // A multi-row selection is the queue. Otherwise play everything listed, from
        // wherever the cursor is -- scanning a character's lines is the common case,
        // and starting over from the top every time would be useless.
        var listed = BrowseListed();
        var picked = SelectedRows;
        List<SoundRow> queue;
        if (picked.Count > 1) queue = picked;
        else
        {
            var from = picked.Count == 1 ? listed.IndexOf(picked[0]) : 0;
            queue = listed.Skip(Math.Max(0, from)).ToList();
        }

        if (queue.Count == 0) { Say("nothing to play."); return; }

        // With nowhere for the sound to go, the output reports every clip finished the
        // instant it starts -- so a run would walk four hundred rows in a blink and
        // read as a crash. Refusing says the real reason instead.
        if (NoSoundReason() is { } problem) { Say("autoplay needs sound: " + problem); return; }

        StartAutoplay(queue);
    }

    private void StartAutoplay(List<SoundRow> queue)
    {
        Preview();          // hooks Finished, which is what advances the run
        _queue = queue;
        _queueAt = -1;
        _queuePlayed = 0;
        _queueTrouble = null;
        BrowseRedraw();
        Say($"autoplay: {queue.Count} sound(s).");
        PlayNext();
    }

    private void StopAutoplay(string message)
    {
        _queue = null;
        _queueAt = -1;
        BrowseRedraw();
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

        var preview = Preview();
        while (++_queueAt < _queue.Count)
        {
            var row = _queue[_queueAt];
            var data = preview.Bytes(Session, Sounds, row);

            // The reason the first skip happened is kept rather than discarded. It is
            // the only thing that distinguishes "one bad file in four hundred" from
            // "the decoder never unpacked", and both look identical while the run is
            // going past.
            string wav = null;
            if (data is null) _queueTrouble ??= $"media {row.MediaId}: no bytes found.";
            else
            {
                wav = preview.Decode(data, row.MediaId, out var why);
                if (wav is null) _queueTrouble ??= why;
            }
            if (wav is null) continue;

            // Follow along in the grid, so what you hear is what is highlighted.
            Grid_.SelectedItem = row;
            Grid_.ScrollIntoView(row, null);
            preview.Play(wav, Config.PreviewAsInGame ? row.Applied : null, out _);
            _queuePlayed++;
            Say($"autoplay {_queueAt + 1}/{_queue.Count}  —  {row.TestLabel} {row.Display}"
                    .Replace("  ", " ").Trim() +
                (string.IsNullOrEmpty(row.Subtitle) ? "" : $"   \"{row.Subtitle}\""));
            return;
        }

        // Built before StopAutoplay, which clears the queue it counts.
        var total = _queue.Count;
        var played = _queuePlayed;
        StopAutoplay(played == total
            ? $"autoplay finished — {total} sound(s)."
            : $"autoplay finished — played {played} of {total}; {total - played} could not be " +
              "decoded" + (_queueTrouble is null ? "." : $" — {_queueTrouble}"));
    }

    // ---- durations -----------------------------------------------------------

    /// <summary>
    /// Fill in the length of every listed sound that has none.
    ///
    /// Measured from the game's own file, not from anything staged over it: this is
    /// the timing the .bnk was authored against, and a swap being longer or shorter
    /// than what it replaces is exactly the thing worth seeing. Rows that already have
    /// a number are left alone, so a second pass over a big bank costs nothing.
    /// </summary>
    private async Task MeasureDurationsAsync()
    {
        if (Sounds is null) { Say("pick a .bnk in the tree first."); return; }
        if (_measuring) { Say("already measuring."); return; }

        var rows = BrowseListed();
        if (rows.Count == 0) { Say("nothing listed to measure."); return; }

        var preview = Preview();
        _measuring = true;
        try
        {
            // Nullable, so a failure Working has already reported is not overwritten
            // by a summary claiming zero clips were measured.
            var measured = await Working<int?>("Measuring durations", () =>
            {
                var n = 0;
                var got = 0;
                foreach (var r in rows)
                {
                    if (r.Seconds is null)
                    {
                        var d = preview.Bytes(Session, Sounds, r, ignoreStaged: true);
                        if (d is not null) r.Seconds = preview.Probe(d, r.MediaId);
                        if (r.Seconds is not null) got++;
                    }
                    if (++n % 25 == 0) BrowseProgress($"measuring… {n}/{rows.Count}");
                }
                return got;
            });

            if (measured is null) return;

            var known = rows.Count(r => r.Seconds is not null);
            var total = rows.Sum(r => r.Seconds ?? 0);
            Refresh();
            Say($"measured {measured} clip(s) — {known}/{rows.Count} listed sounds now have a " +
                $"length, {TimeSpan.FromSeconds(total):h\\:mm\\:ss} in total." +
                (known < rows.Count ? "  The rest could not be decoded." : ""));
        }
        finally { _measuring = false; }
    }

    /// <summary>Status from work that is not on the drawing thread.</summary>
    private void BrowseProgress(string message) => Dispatcher.UIThread.Post(() => Say(message));

    /// <summary>
    /// The rows on screen, in the order they are drawn.
    ///
    /// CollectionView, not ItemsSource: the grid sorts by wrapping what it was handed,
    /// and ItemsSource still reads back in the order it was set. Autoplay's rule is
    /// "everything listed from the cursor down", so reading the unsorted list means
    /// that after a click on a column header the run starts in the wrong place and
    /// plays in an order nobody can see. ItemsSource remains the fallback for the
    /// moment between it being set and the view existing.
    /// </summary>
    private List<SoundRow> BrowseListed() =>
        (Grid_.CollectionView ?? Grid_.ItemsSource)?.OfType<SoundRow>().ToList() ?? [];
}
