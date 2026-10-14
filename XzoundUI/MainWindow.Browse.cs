using System.IO;
using Avalonia.Media;
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
            Hint: "Apply what the .bnk does to the sound, which is volume, pitch, delay and loop, instead of playing the file as it is.");

        yield return new ToolButton(
            SoundRow.ShowTranslatedCategory ? "English categories  ✓" : "English categories",
            ToggleEnglishCategories,
            Hint: "Read the Category column in English. The game authored that column in Chinese and ships no translation for it: " +
                  "hero and ability names come from the game's own English text and the rest from a built-in glossary, so this " +
                  "costs nothing and reaches nothing.\n\nA label neither source covers stays in Chinese rather than being guessed at.");
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
    /// <summary>
    /// What Play plays for a row. The original is the GAME's: with a mod opened, the rows' own
    /// bytes are the mod's, and "the original" played the mod's audio back.
    /// </summary>
    private byte[] PlayBytes(AudioPreview preview, SoundRow row, bool original) =>
        original && Mods.Any
            ? AudioPreview.GameBytes(Session, row, GameBankPathFor(row.Bank))
            : preview.Bytes(Session, Sounds, row, ignoreStaged: original);

    /// <summary>The render harness: the bytes Play would play for a medium, without playing them.</summary>
    internal byte[] PlayBytesForShot(uint media, bool original) =>
        Sounds?.Rows.FirstOrDefault(r => r.MediaId == media) is { } row ? PlayBytes(Preview(), row, original) : null;

    private void PlayRow(SoundRow row, bool original)
    {
        if (Sounds is null) { Say("pick a .bnk in the tree first."); return; }
        if (row is null) { Say("select a sound first."); return; }

        var preview = Preview();
        var data = PlayBytes(preview, row, original);
        if (data is null)
        {
            var shipped = Sounds.BankOfMedia.ContainsKey(row.MediaId) || Session?.LooseMedia(row.MediaId) is not null;
            Say(original && row.ModTag == "NEW" ? $"media {row.MediaId}: the mod added this sound; the game has no original."
                : SoundSources.NoAudioReason(row, shipped) is { } why ? $"{row.Display}: {why}"
                : $"media {row.MediaId}: no bytes found.");
            return;
        }
        if (original && Mods.Any) row.Origin = "the game's own copy";

        var wav = preview.Decode(data, row.MediaId, out var err);
        if (wav is null) { Say(err); return; }

        // The switch decides whether this is the file or the sound. Both are worth
        // hearing: one is what you are shipping, the other is what will be heard.
        var applied = Config.PreviewAsInGame ? row.Applied : null;
        preview.Play(wav, applied, out var did);

        var note = Config.PreviewAsInGame ? $"; ingame: {did}" + NotReproduced(row) : "";

        // data.Length rather than row.Bytes: Bytes() sets row.Bytes on every path that
        // reads the game, but NOT on the staged one -- so a staged row was announced
        // with the staged path and the GAME file's size, two halves of two different
        // sounds in one sentence. What played is what is counted.
        Say($"{row.Display}: {data.Length:N0} B from {row.Origin}{note}{SilenceNote()}");
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
            ? "previews now include what the .bnk applies: volume, pitch, delay and loop."
            : "previews now play the file as it is.");
        BrowseRedraw();
        // The details panel says in words which of those two things Play is about to do, and
        // nothing about the selection moved, so it has to be told. Without this the panel went
        // on reading "Play plays the file as it is" with Ingame switched on beside it.
        ShowDetails();
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
    /// something to do per row. Which device that is depends on the moniker this was
    /// built for: WaveOut on net10.0-windows, PortAudio or a system player on net10.0,
    /// and NoAudio when the machine has neither, which is the only case with a reason
    /// to report.
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
    /// blink and still said "autoplay finished, 412 sound(s)", which reads as 412
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
            Say($"autoplay {_queueAt + 1}/{_queue.Count}: {row.TestLabel} {row.Display}"
                    .Replace("  ", " ").Trim() +
                (string.IsNullOrEmpty(row.Subtitle) ? "" : $"   \"{row.Subtitle}\""));
            return;
        }

        // Built before StopAutoplay, which clears the queue it counts.
        var total = _queue.Count;
        var played = _queuePlayed;
        StopAutoplay(played == total
            ? $"autoplay finished, {total} sound(s)."
            : $"autoplay finished, played {played} of {total}; {total - played} could not be " +
              "decoded" + (_queueTrouble is null ? "." : $": {_queueTrouble}"));
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
            Say($"measured {measured} clip(s), {known}/{rows.Count} listed sounds now have a " +
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

    // ---- what language the Category column is in -----------------------------

    /// <summary>
    /// Whether one pass is already running, so a filter keystroke that re-lists the
    /// rows does not start a second one over the same rows. The pass mutates the rows
    /// it is given, and two of them racing would translate everything twice and hand
    /// CategoryTranslator's unsynchronised name cache to two threads at once.
    /// </summary>
    private bool _translating;

    /// <summary>Told once, when there are rows on screen for it to be about.</summary>
    private bool _noChineseFontNotice;

    /// <summary>
    /// Whether the offer to spend the network on the leftovers has been made. Once a
    /// window, because the answer does not change between two clicks of the same
    /// button and a question nobody asked for is worse than the gap it is about.
    /// </summary>
    private bool _askedToFetch;

    /// <summary>
    /// Set by LoadGameSync: the render harness owns the one thread and then blocks on
    /// it, so a continuation posted back to it never runs. Work that would normally go
    /// through Working has to happen in place there, or the frame is captured showing
    /// the state before it.
    /// </summary>
    private bool _nothingAwaited;

    /// <summary>
    /// What the Category column starts in.
    ///
    /// A stored choice is obeyed as given. Null means nobody has ever chosen, and that
    /// is the only case the font check is allowed to decide.
    /// </summary>
    private void StartCategoryLanguage()
    {
        if (Config.EnglishCategories is { } chosen) { SoundRow.ShowTranslatedCategory = chosen; return; }
        if (CanDrawChinese()) return;

        // Nothing installed here can draw the script, so the choice is not between two
        // readings of the label: it is between English and a column of empty boxes. A
        // stock WSL Ubuntu is exactly this, with no font on it claiming any Chinese
        // coverage at all.
        //
        // Deliberately NOT written back to settings. The reason is a property of this
        // host, not a decision about the tool, and the same settings file opened on a
        // machine that has the fonts should go back to showing what the game itself
        // says. It is announced rather than done quietly, because a column saying
        // something other than what the game says is worth knowing about even when it
        // is the only readable option.
        SoundRow.ShowTranslatedCategory = true;
        _noChineseFontNotice = true;
    }

    private void ToggleEnglishCategories()
    {
        SoundRow.ShowTranslatedCategory = !SoundRow.ShowTranslatedCategory;
        Config.EnglishCategories = SoundRow.ShowTranslatedCategory;
        Config.Save();
        BrowseRedraw();

        if (!SoundRow.ShowTranslatedCategory)
        {
            // Nothing to undo in the rows: both forms are kept on each row and the
            // switch picks which one the column reads, so turning it off is a redraw.
            Refresh();
            Say("Category is showing the game's own Chinese labels again.");
            return;
        }
        _ = EnglishCategoriesAsync(announce: true);
    }

    /// <summary>
    /// Give any row that has no English form one, for rows that arrived after the
    /// switch was flipped.
    ///
    /// Called from ApplyFilter, which is the one place every route to the list passes
    /// through: picking a second skin, opening a mod, closing one and reloading the
    /// game all put rows on screen that were never handed to the switch, and in the
    /// WPF window each of those was a separate bug until the call was put where every
    /// re-list reaches it.
    /// </summary>
    private void CategoryLanguageForNewRows()
    {
        if (!SoundRow.ShowTranslatedCategory || _translating || !AnyUntranslated()) return;
        if (_nothingAwaited) { TranslateRows(); return; }
        _ = EnglishCategoriesAsync(announce: false);
    }

    /// <summary>
    /// The pass, off the drawing thread. The first one on a cold session is seconds of
    /// work: the proper nouns come from reading the game's Chinese and English locres
    /// and pairing them by key, which is a pak read before it is a lookup.
    /// </summary>
    private async Task EnglishCategoriesAsync(bool announce)
    {
        _translating = true;
        try
        {
            var done = await Working("Translating categories", TranslateRows);
            Refresh();

            if (_noChineseFontNotice)
            {
                _noChineseFontNotice = false;
                Say("no font on this host can draw Chinese, so Category is showing English. " +
                    "The English categories button puts it back.");
                return;
            }
            if (announce) await ReportCategories();
            else if (done > 0 && Status.Text.StartsWith("Translating categories"))
                Say($"{done:N0} categor{(done == 1 ? "y" : "ies")} translated.");
        }
        finally { _translating = false; }
    }

    /// <summary>
    /// Fill in the English for every row that has none, and say how many that was.
    /// Rows that already have one are left alone, so this is cheap on every re-list
    /// and only a newly read skin costs anything.
    /// </summary>
    private int TranslateRows()
    {
        var todo = Sounds is null ? [] : Sounds.Rows.Where(NeedsEnglish).ToList();
        foreach (var row in todo)
            row.CategoryEnglish = CategoryTranslator.Translate(Session, row.Category);
        return todo.Count;
    }

    /// <summary>
    /// The same question without building the list, because it is asked on every
    /// keystroke in the filter box and the answer is almost always no.
    /// </summary>
    private bool AnyUntranslated() => Sounds is not null && Sounds.Rows.Any(NeedsEnglish);

    private static bool NeedsEnglish(SoundRow row) =>
        row.CategoryEnglish is null && !string.IsNullOrEmpty(row.Category);

    /// <summary>
    /// Say what the pass managed, and only then, only once, and only if a provider has
    /// actually been chosen, offer to spend the network on what is left over.
    ///
    /// LiveTranslate.WhyNot is null only when someone went into settings, picked a
    /// service and gave it what it needs. That act is the consent and the only thing
    /// that unlocks this: with no provider nothing is sent, nothing is offered, and
    /// the leftovers simply stay in Chinese. The rule exists because this tool reads
    /// files off someone's own disk for people who have no reason to expect it to talk
    /// to anything, so a phrase leaving the machine has to be something they asked
    /// for twice rather than a side effect of reading a column.
    /// </summary>
    private async Task ReportCategories()
    {
        var left = Sounds is null
            ? []
            : CategoryTranslator.Untranslatable(Session, Sounds.Rows.Select(r => r.Category)).ToList();
        if (left.Count == 0) { Say("Category is in English."); return; }

        var stays = $"Category is in English. {left.Count} phrase(s) have no translation " +
                    "and stay in Chinese.";
        if (LiveTranslate.WhyNot(Config) is not null || _askedToFetch || _nothingAwaited)
        {
            Say(stays);
            return;
        }

        _askedToFetch = true;
        var local = LiveTranslate.IsLocal(Config.TranslateProvider);
        var send = await Ask("Translate the rest",
            $"{left.Count} phrase(s) have no translation in the game or in the built-in glossary." +
            Environment.NewLine + Environment.NewLine +
            $"Send them to {LiveTranslate.Destination(Config)}?" + Environment.NewLine +
            (local
                ? "That is your own machine, so nothing leaves it."
                : "Only these Chinese label texts go. Nothing else about you or your files.") +
            " Each result is kept here, so a phrase is sent once." + Environment.NewLine +
            Environment.NewLine + string.Join(Environment.NewLine, left.Take(8)) +
            (left.Count > 8 ? Environment.NewLine + $"and {left.Count - 8} more" : ""),
            yes: "Send");
        if (!send) { Say(stays); return; }

        // Blocking on the fetch inside Working rather than awaiting it here: awaiting
        // on the drawing thread while the fetch posts progress back to it is the
        // deadlock the command-line version already hit.
        var added = await Working("Fetching the rest", () =>
            LiveTranslate.FetchAsync(Config, left, _ => { }).GetAwaiter().GetResult());

        // The cache has grown, so every row is asked again rather than only the ones
        // that had nothing: a label is several segments and one of them getting an
        // answer improves a row that already had a partial reading.
        foreach (var row in Sounds.Rows) row.CategoryEnglish = null;
        TranslateRows();
        Refresh();
        Say($"Category is in English, having fetched {added} of {left.Count}.");
    }

    /// <summary>
    /// Whether this host can draw the script the Category column is authored in.
    ///
    /// Asked of the font stack and not of the operating system, because the operating
    /// system is the wrong question: a Linux box with the CJK fonts installed draws
    /// these labels and a Windows install with them stripped does not. A stock WSL
    /// Ubuntu has no font claiming any Chinese coverage, and the column there is a row
    /// of empty boxes rather than anything a reader can act on.
    ///
    /// Two codepoints, both from labels that are everywhere in this data: 语 out of
    /// 语音 and 击 out of 击败. Worked out once and kept, because it walks the
    /// installed families and that is not a per-row question.
    /// </summary>
    public static bool CanDrawChinese()
    {
        if (_chineseDrawable is { } known) return known;
        _chineseDrawable = HasGlyph(0x8BED) && HasGlyph(0x51FB);
        return _chineseDrawable.Value;
    }

    private static bool? _chineseDrawable;

    private static bool HasGlyph(int codepoint)
    {
        try
        {
            var fonts = FontManager.Current;
            if (!fonts.TryMatchCharacter(codepoint, FontStyle.Normal, FontWeight.Normal,
                                         FontStretch.Normal, null, null, out var typeface))
                return false;

            // A match is not a glyph. The fallback will answer with SOME typeface, and
            // a typeface that maps the codepoint to .notdef is precisely the empty box
            // this is here to detect, so the font's own character map is asked.
            return fonts.TryGetGlyphTypeface(typeface, out var glyphs) &&
                   glyphs.CharacterToGlyphMap.TryGetGlyph(codepoint, out var glyph) && glyph != 0;
        }
        catch
        {
            // No font stack at all, which is a headless host with nothing configured.
            // Unknowable is treated as "cannot", which lands on the readable option.
            return false;
        }
    }
}
