using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MRAudioKit.UI;

/// <summary>
/// Setup: the four things the tool cannot work without -- where the paks are, the key
/// that decrypts them, the mappings that turn hashes into names, and whose language's
/// lines to read -- plus the button that mounts the game.
///
/// The old window put all of that on the tab itself as a grid of labelled text boxes,
/// which is why the tab strip stood twice as tall as every other tab's toolbar. Here
/// each setting is a button like everywhere else: it opens a picker or a small prompt,
/// the status line says what is now set, and the hint carries the current value so
/// hovering answers "what is this pointing at?" without a box taking up the strip.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// A second mount over the top of a running one leaves a half-built tree and two
    /// threads writing the same session, so the buttons that mount refuse to re-enter.
    /// </summary>
    private bool _setupBusy;

    private IEnumerable<ToolButton> SetupTools()
    {
        yield return new ToolButton("Load game", () => _ = SetupLoadAsync(), Key: true,
            Hint: "Mount the paks, read the banks, fill the tree.\n" + SetupPaksNote());

        yield return new ToolButton("Paks folder…", () => _ = SetupPaksAsync(),
            Hint: SetupPaksNote());

        yield return new ToolButton("AES key…", () => _ = SetupAesAsync(),
            Hint: "The key that decrypts this patch's paks. Yours to supply.\n" +
                  (string.IsNullOrWhiteSpace(Config.AesKey) ? "Not set." : "Set."));

        yield return new ToolButton("usmap…", () => _ = SetupUsmapAsync(),
            Hint: "Mappings file.\n" + SetupUsmapNote());

        yield return new ToolButton("Fetch latest mappings", () => _ = SetupFetchAsync(),
            Hint: "Download the newest mappings from the community depot.\n" +
                  "Your own file, if you picked one, is kept until you confirm.");

        yield return new ToolButton("Language…", () => _ = SetupLanguageAsync(),
            Hint: $"Which language's voice lines and subtitles to read.\nNow: {Config.Language}.");

        yield return new ToolButton("Import SoundKit lists…", () => _ = SetupImportSubsAsync(),
            Hint: "SoundKit's subtitle lists from earlier seasons (its SUBS-OLD folder): what lets Update mod " +
                  "move a loose voice line made before XzoundWave recorded that season onto today's id.");

        yield return new ToolButton("Record a game folder…", () => _ = SetupSnapshotFolderAsync(),
            Hint: "An older copy of the game's Paks folder, recorded so Update mod knows what its media ids were. " +
                  "The game you load is recorded on its own, once per version.");
    }

    /// <summary>Said after whatever is on the status line, rather than over it.</summary>
    private void SetupNote(string note) => Say(string.IsNullOrEmpty(Status.Text) ? note : Status.Text.TrimEnd() + "  " + note);

    private async Task SetupImportSubsAsync()
    {
        var folder = await PickFolder("SoundKit's folder, its SUBS-OLD folder, or one season's lists");
        if (folder is null) return;
        var found = await Working("importing SoundKit's lists", () => GameHistory.ImportSubs(folder));
        if (found is null) return;
        Say(found.Count == 0
            ? $"no SoundKit subtitle lists under {folder} — pick SoundKit's folder or its 0_DATA/0-TXTs/SUBS-OLD."
            : $"imported {found.Count} SoundKit list(s): {string.Join(", ", found)}. Update mod reads loose voice lines against them.");
    }

    private async Task SetupSnapshotFolderAsync()
    {
        var folder = await PickFolder("An older copy of the game's Paks folder");
        if (folder is null) return;
        var settings = Settings.Load();
        settings.PaksDir = folder;
        var taken = await Working($"reading every bank in {folder}", () =>
        {
            var session = new GameSession();
            session.Mount(settings, _ => { });
            return (Snapshot: GameHistory.EnsureSnapshot(session, folder), Label: GameSnapshot.VersionOf(folder).Version ?? session.BankFingerprint()[..8]);
        });
        if (taken.Label is null) return;   // Working has already said why
        Say(taken.Snapshot is null
            ? $"that version ({taken.Label}) is already recorded."
            : $"recorded {taken.Snapshot.Label}: {taken.Snapshot.Media.Count:N0} media. Update mod can read mods made for it now.");
    }

    // ---- loading ---------------------------------------------------------------

    private async Task SetupLoadAsync()
    {
        if (_setupBusy) { Say("still loading — give it a moment."); return; }

        // First run finds Steam's copy of the game and unpacks vgmstream, so it has to
        // happen before the folder is judged missing or a new user is told off for
        // something the tool could have worked out itself.
        //
        // It is a convenience and not a step. LoadGame calls it again inside its own
        // try and reports what it throws; a throw HERE lands in a task nobody awaits,
        // and the button does nothing at all -- no tree, no status line, no error.
        try { Config.AutoDetect(); } catch { }

        if (string.IsNullOrWhiteSpace(Config.PaksDir))
        {
            Say("no Paks folder set — pick one with \"Paks folder…\" first.");
            return;
        }
        if (!Directory.Exists(Config.PaksDir))
        {
            Say($"that Paks folder is not there any more: {Config.PaksDir}");
            return;
        }

        // A language the tool does not offer reaches Mount as a filter that matches no
        // folder: every localized bank is dropped and the tree comes back nearly empty
        // with nothing said about why. The old window could not produce one -- the
        // combo held the three and fell back to the first -- and it wrote that value
        // into the settings on every load. A settings file carried over from an older
        // build, or hand-edited, can.
        if (!GameSession.Languages.Contains(Config.Language))
            Config.Language = GameSession.Languages[0];

        // Whatever was chosen on this tab is what the mount should use, and it should
        // still be set tomorrow -- the old window saved on the way into the load for
        // the same reason.
        Config.Save();

        // The import path reads the settings without being handed them, so this has to
        // be pointed at them or the converted-audio cache is off whatever the setting
        // says and "replace source with .wem" never happens. The WPF window set it in
        // its constructor and nothing in this one does; the constructor is still where
        // it belongs, and this is the earliest place in a file that is mine.
        ConvertedCache.Options = Config;

        // Everything derived from the session goes when the session does -- counted
        // here because the reload silently drops loaded mods, which is worth a word.
        var hadMods = Mods.Count + Mods.LooseFiles.Count;

        _setupBusy = true;
        try
        {
            // A remount is a different game and everything read out of the old one has
            // to go with it. Mount replaces the provider on its first line, before it
            // can fail, so the previous session is gone whatever happens next --
            // keeping what was built from it only means serving it afterwards.
            // GameSession.Generation says the same thing in the logic layer: the skin
            // cache is keyed on the skin id alone, so without this a reload after
            // changing the LANGUAGE -- which is what the language button tells you to
            // do -- hands back the previous language's rows and looks like it did
            // nothing. The WPF window cleared all of this at the top of PopulateTree;
            // this port's PopulateTree only rebuilds the nodes.
            Tree.SelectedItems.Clear();
            _picked.Clear();
            _skinCache.Clear();
            _mods.Clear();
            _sounds = null;
            Grid_.ItemsSource = null;
            // Clearing the tree is also how a failure is told apart from a success
            // below: PopulateTree runs only when the mount completed, while
            // Session.Characters is left over from the previous mount and so answers
            // for the wrong one.
            _roots.Clear();

            // LoadGame belongs to the window, not to this tab: the render harness runs
            // the same work through LoadGameSync, and two copies of a mount would drift.
            // It reports its own failure, so there is nothing to catch here.
            await LoadGame();

            if (_roots.Count == 0)
            {
                // Either the mount failed and LoadGame has said why, or it mounted
                // files and found nothing readable in them -- which its own count line
                // states but does not explain. Appended rather than written over, so
                // an error already on the line survives.
                if (Session.Provider is { Files.Count: > 0 } && Session.Characters.Count == 0)
                    Say(Status.Text + "  Nothing readable in there — is that the game's own Paks folder?");
                return;
            }

            Tabs.SelectedIndex = 0;   // setup is done; the work happens on Browse

            // From now on the window loads the game by itself when it opens.
            if (!Config.LoadedOnce) { Config.LoadedOnce = true; Config.Save(); }

            if (hadMods > 0)
                Say(Status.Text + $"  {hadMods} loaded mod file{(hadMods == 1 ? "" : "s")} " +
                    "closed by the reload.");

            // The media-to-bank index is what the build path uses to know where a sound
            // lands. Roughly five seconds over 1,437 banks, so it runs behind the tree
            // rather than in front of it: everything on screen is usable without it,
            // and a build that arrives first pays for the index itself.
            var trouble = await Task.Run(() =>
            {
                try { Session.IndexBanks(_ => { }); return null; }
                catch (Exception ex) { return ex.Message; }
            });
            if (trouble is not null)
                Say("the game is loaded, but the bank index failed: " + trouble);
            else
            {
                Refresh();   // the counts can change once every bank is known
                // A record of this version, once per version, for updating mods after the next
                // patch. Reads every bank, so it runs behind everything and says nothing unless
                // it took one.
                var session = Session;
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (GameHistory.EnsureSnapshot(session) is { } taken)
                            Dispatcher.UIThread.Post(() => SetupNote($"recorded {taken.Label} for updating mods after the next patch."));
                    }
                    catch { /* a version not recorded only means an update after the patch knows less */ }
                });
            }
        }
        finally
        {
            _setupBusy = false;
            SetupRetools();
        }
    }

    // ---- the four settings ------------------------------------------------------

    private async Task SetupPaksAsync()
    {
        // The shell's picker takes no starting folder, so this opens wherever the
        // platform last left off rather than where the setting currently points.
        var dir = await PickFolder("The game's Paks folder");
        if (dir is null) return;   // cancelled means leave the setting alone

        Config.PaksDir = dir;
        Config.Save();

        // Say what is in there, because "wrong folder" is the commonest way a load
        // fails and the count answers it before the mount does.
        var containers = SetupCountContainers(dir);
        Say(containers == 0
            ? $"Paks folder set, but there are no .pak or .utoc files in it: {dir}"
            : $"Paks folder set — {containers:N0} container{(containers == 1 ? "" : "s")}. Load game to mount them.");
        SetupRetools();
    }

    private async Task SetupAesAsync()
    {
        var key = await SetupPrompt("AES key",
            "The key that decrypts this patch's paks. It is yours to supply: it is kept "
            + "in your settings, and it is not sent anywhere or written to any log.",
            Config.AesKey);
        if (key is null) return;

        key = key.Trim();
        Config.AesKey = key;
        Config.Save();

        // Never echoed. The key goes to Settings and from there to the provider; the
        // status line and the hint only ever say whether there is one.
        if (key.Length == 0) { Say("AES key cleared."); SetupRetools(); return; }

        Say(SetupLooksLikeKey(key)
            ? "AES key set. Load game to use it."
            : "AES key set, but it does not look like 64 hex characters — the mount will "
              + "reject it if it is wrong.");
        SetupRetools();
    }

    private async Task SetupUsmapAsync()
    {
        var path = await PickFile("Mappings file", "Mappings", "*.usmap");
        if (path is null) return;

        Config.UsmapPath = path;
        Config.Save();
        Say("usmap: " + SetupUsmapNote() + " — load the game again to read names with it.");
        SetupRetools();
    }

    private async Task SetupFetchAsync()
    {
        if (_setupBusy) { Say("still working — give it a moment."); return; }

        // A file the author picked themselves is theirs. UsmapFetch will not adopt one
        // on a routine check, and a button press must not either without being told to.
        // The file is not deleted, which is what makes the question a fair one to ask.
        var mine = !string.IsNullOrWhiteSpace(Config.UsmapPath)
                   && !UsmapFetch.IsManaged(Config.UsmapPath);
        if (mine)
        {
            var go = await Ask("Replace your usmap?",
                "You have picked your own usmap:\n"
                + Path.GetFileName(Config.UsmapPath) + "\n\n"
                + "Fetch the newest from the community depot and use that instead?\n\n"
                + "Your file is not deleted — \"usmap…\" can select it again.",
                "Fetch", "Keep mine");
            if (!go) { Say("kept your own usmap."); return; }
        }

        _setupBusy = true;
        try
        {
            // force: a button press, so the cached ETag is ignored and the download is
            // adopted even over a file of the author's own -- which is exactly what was
            // just agreed to above. Progress arrives off the pool thread the request
            // runs on, so it has to be posted rather than written straight to the label.
            var r = await UsmapFetch.UpdateAsync(Config, force: true,
                progress: m => Dispatcher.UIThread.Post(() => Say(m)));

            if (r.Path is not null)
            {
                Config.UsmapPath = r.Path;
                Config.Save();
            }

            // One line, not a line and a dialog. The part worth saying beyond the
            // result is that names already read are read with the OLD mappings.
            Say("usmap: " + r.Message
                + (r.Changed ? " Load the game again to read names with the new mappings." : ""));
        }
        catch (Exception ex)
        {
            // UpdateAsync answers with a message rather than throwing for anything it
            // expects, so anything landing here is unexpected and must still not throw.
            Say("could not fetch the usmap: " + ex.Message);
        }
        finally
        {
            _setupBusy = false;
            SetupRetools();
        }
    }

    private async Task SetupLanguageAsync()
    {
        var picked = await SetupChoose("Language",
            "Which language's voice lines and subtitles to read.",
            GameSession.Languages, Config.Language);
        if (picked is null || picked == Config.Language) return;

        Config.Language = picked;
        Config.Save();

        // The language is read at mount time, so nothing already on screen changes.
        // Saying so is the difference between "it did nothing" and "it will next time".
        Say(Session.Characters.Count > 0
            ? $"language: {picked} — load the game again to re-read the lines."
            : $"language: {picked}.");
        SetupRetools();
    }

    // ---- what the hints say -----------------------------------------------------

    private string SetupPaksNote()
    {
        if (string.IsNullOrWhiteSpace(Config.PaksDir)) return "No Paks folder set.";
        return Directory.Exists(Config.PaksDir)
            ? Config.PaksDir
            : Config.PaksDir + "   — FOLDER MISSING";
    }

    /// <summary>
    /// What the loaded mappings are. The build number is the useful part: a usmap is
    /// per game build, and a stale one is the difference between readable events and a
    /// wall of hashes.
    /// </summary>
    private string SetupUsmapNote()
    {
        var path = Config.UsmapPath?.Trim();
        if (string.IsNullOrEmpty(path)) return "none set — event names will show as hashes";

        var name = Path.GetFileName(path);
        var build = UsmapFetch.BuildOf(name);
        var release = UsmapFetch.ReleaseOf(name);
        return (build is null ? name : $"build {build}" + (release is null ? "" : $" · {release}"))
             + (File.Exists(path) ? "" : "  — FILE MISSING")
             + (UsmapFetch.IsManaged(path) ? "" : "  · your own file");
    }

    /// <summary>
    /// Redraw this tab's buttons so their hints show what was just set. Posted rather
    /// than called: the click that got here is still being raised, and rebuilding the
    /// toolbar pulls the button it came from out of the tree.
    /// </summary>
    private void SetupRetools() => Dispatcher.UIThread.Post(() =>
    {
        if (Tabs.SelectedIndex == 4) ShowToolbar();
    });

    /// <summary>Optional 0x, then 32 bytes of hex, which is the shape a UE AES key has.</summary>
    private static bool SetupLooksLikeKey(string key)
    {
        var hex = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key;
        return hex.Length == 64 && hex.All(Uri.IsHexDigit);
    }

    /// <summary>Containers the provider would mount, which is what "is this the right folder" means.</summary>
    private static int SetupCountContainers(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Count(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
        }
        catch { return 0; }   // unreadable is indistinguishable from empty, and both mean "no"
    }

    // ---- two small dialogs ------------------------------------------------------

    /// <summary>
    /// A line of text, or null if they cancelled -- which is not the same as an empty
    /// answer, because an empty answer clears the setting on purpose.
    /// </summary>
    private async Task<string> SetupPrompt(string title, string body, string current)
    {
        string answer = null;

        var box = new TextBox { Text = current ?? "", Width = 460, AcceptsReturn = false };
        var ok = new Button { Content = "OK", Width = 100, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 100, IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 460,
        });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => { answer = box.Text ?? ""; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        // Enter finishes: pasting a 64-character key and reaching for the mouse is a
        // step this dialog does not need. Handled, because the OK button is the
        // default one and would otherwise take the same key press afterwards and close
        // a window that has already closed.
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            answer = box.Text ?? "";
            dialog.Close();
        };
        dialog.Opened += (_, _) => box.Focus();

        await dialog.ShowDialog(this);
        return answer;
    }

    /// <summary>One of a short list, or null if they cancelled.</summary>
    private async Task<string> SetupChoose(string title, string body,
                                           IReadOnlyList<string> options, string current)
    {
        string answer = null;

        var list = new ListBox
        {
            ItemsSource = options,
            SelectedItem = options.Contains(current) ? current : options.FirstOrDefault(),
            Width = 300,
            MaxHeight = 260,
        };
        var ok = new Button { Content = "OK", Width = 100, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 100, IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 300,
        });
        panel.Children.Add(list);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => { answer = list.SelectedItem as string; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        list.DoubleTapped += (_, _) => { answer = list.SelectedItem as string; dialog.Close(); };

        await dialog.ShowDialog(this);
        return answer;
    }
}
