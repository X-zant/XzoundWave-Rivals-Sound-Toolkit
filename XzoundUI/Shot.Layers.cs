using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit.UI;

public static partial class Shot
{
    /// <summary>
    /// XzoundWave --shot-layers &lt;dir&gt; &lt;audio&gt;: right-click › Add a sound to this event… on Emma
    /// Frost's ult start, end to end. The added sound is in the project and the tree, its audio is
    /// staged, a second one gets ids of its own, Remove takes one out, a mute is refused; it survives
    /// Save, New and Open; Build mod writes a bank whose ult plays it, stops it where the ult's own
    /// sound stops, and names it in the bank's XZWV manifest. Then the built mod is opened and built
    /// on again (the earlier copy taken out, not added twice), and updated with "Update mod…" (taken
    /// out before the rebase and put back after it).
    ///
    /// Saves a project and builds into &lt;dir&gt;, and the window writes its settings as a save does, so
    /// run it under a settings folder of its own (on Linux, HOME pointed at a scratch folder).
    /// </summary>
    public static int TakeLayers(string dir, string audio)
    {
        try
        {
            AppBuilder.Configure<App>().UseBundledFonts().UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            Directory.CreateDirectory(dir);
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            void Pump(Task t = null)
            {
                while (t is { IsCompleted: false }) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
                for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            }
            Pump();
            main.ToldForShot = [];

            var failures = 0;
            void Check(string what, bool ok, string detail = "")
            {
                Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}{(detail.Length > 0 ? "   " + detail : "")}");
                if (!ok) failures++;
            }

            const string start = "sfx_1053001_q_start_play";
            var startId = VoiceLine.Fnv(start);
            main.ShowSkinSync("1053001", "sfx");
            Pump();
            var grid = main.FindControl<DataGrid>("Grid_");
            var row = main.SoundsForShot.Rows.FirstOrDefault(r => r.EventName == start);
            Check("Emma Frost's ult start is in the list", row is not null, $"{main.SoundsForShot.Rows.Count} rows");
            if (row is null) return 1;
            Console.WriteLine($"  on {row.Bank}, media {row.MediaId}");

            grid.SelectedItem = row;
            Pump();
            Pump(main.AddLayerForShot(audio, stops: true));
            var layers = main.CurrentProject.Layers;
            Check("the project has the added sound, stopped with the ult", layers.Count == 1 && layers[0].Event == startId && layers[0].Stops,
                  main.Status.Text);
            if (layers.Count == 0) return 1;
            var layer = layers[0];
            Console.WriteLine($"  copies Sound {layer.Template}; media {layer.MediaId}; bank {layer.Bank}");
            Check("its audio is staged under its own media id", main.StagedFiles.Any(p => p.MediaId == layer.MediaId));
            Check("which no game bank has", !main.CurrentSession.MediaBank.ContainsKey(layer.MediaId));
            var node = main.RootsForShot.FirstOrDefault(n => n.What == Node.Kind.AddedSounds);
            Check("the tree lists it under Added sounds", node?.Label.Contains("(1/1 with audio)") == true, node?.Label ?? "(none)");
            Check("and the project is unsaved", main.CurrentProject.IsDirty);

            grid.SelectedItem = row;
            Pump();
            Pump(main.AddLayerForShot(audio, stops: false));
            Check("the same file again gets ids of its own", layers.Count == 2 && layers[1].MediaId != layer.MediaId && layers[1].Name != layer.Name,
                  string.Join(", ", layers.Select(l => $"{l.Name} -> {l.MediaId}")));
            var second = layers.Count > 1 ? layers[1].MediaId : 0;

            main.RevealAddedForShot();
            Pump();
            var added = main.SoundsForShot.Rows.Where(r => layers.Any(l => l.MediaId == r.MediaId)).ToList();
            Check("the node's rows are the two added sounds", added.Count == 2 && main.SoundsForShot.Rows.Count == 2,
                  $"{added.Count} of {main.SoundsForShot.Rows.Count}");
            var secondRow = added.FirstOrDefault(r => r.MediaId == second);
            grid.SelectedItem = secondRow;
            Pump();
            // Played first: playing a staged row rewrites its Origin, which must not make it a game row.
            Check("an added row plays its own file", main.PlayBytesForShot(second, original: false) is { Length: > 0 });
            main.MuteForShot();
            Check("an added sound is not muted, even after it was played", secondRow is { Muted: false }, main.Status.Text);
            if (secondRow is not null)
            {
                secondRow.Notes = "too loud";
                main.RowStateChangedForShot([secondRow]);
            }
            Check("a note on an added sound is kept in the project", main.CurrentProject.FindNote(second)?.Notes == "too loud");
            main.RemoveLayersForShot();
            Pump();
            Check("Remove takes the second one out, its audio and its note",
                  layers.Count == 1 && layers[0].MediaId == layer.MediaId && !main.StagedFiles.Any(p => p.MediaId == second) &&
                  main.CurrentProject.FindNote(second) is null,
                  main.Status.Text);

            var path = Path.Combine(dir, "layers-check" + Project.Extension);
            Pump(main.SaveProjectForShot(path));
            var saved = Project.Load(path);
            Check("the saved project keeps it, and its audio as a staged file",
                  saved.Layers.Count == 1 && saved.Layers[0].MediaId == layer.MediaId && saved.Layers[0].Template == layer.Template &&
                  saved.Replacements.Any(r => r.MediaId == layer.MediaId && r.Bank is null),
                  string.Join("; ", saved.Replacements.Select(r => $"{r.MediaId} {r.File} {r.Bank}")));
            Pump(main.NewProjectForShot());
            Check("New takes it away", main.CurrentProject.Layers.Count == 0 &&
                                       !main.RootsForShot.Any(n => n.What == Node.Kind.AddedSounds) && main.StagedFiles.Count == 0);
            Pump(main.OpenProjectForShot(path));
            layers = main.CurrentProject.Layers;
            Check("Open brings it back staged, and the project opens clean",
                  layers.Count == 1 && main.StagedFiles.Any(p => p.MediaId == layer.MediaId) && !main.CurrentProject.IsDirty &&
                  main.RootsForShot.Any(n => n.What == Node.Kind.AddedSounds),
                  main.Status.Text);

            // ---- the build: the bank's ult plays it, its end stops it, its manifest names it ----
            var settings = Settings.Load();
            byte[] BankOf(string pak) =>
                ModBank.Open(pak, settings, withMedia: true).Banks
                       .FirstOrDefault(b => Path.GetFileName(b.Name).Equals(Path.GetFileName(layer.Bank), StringComparison.OrdinalIgnoreCase))?.Bytes;
            var game = main.CurrentSession.Bank(layer.Bank).Read();
            var gameBank = HircBank.Parse("game", game);
            var gameActions = ((HierarchyEvent)gameBank.Nodes[startId]).EventActionIds.Length;
            Console.WriteLine($"  the Sound it copies {(gameBank.Nodes[layer.Template] is HierarchySoundSfxVoice t && t.BaseParams.PropBundle.Props?.Any(p => p.Id == 0x3A) == true ? "loops" : "does not loop")}");

            // A retrigger -- an event that stops a Sound and then plays it again, so a new ult cuts
            // the last one off -- keeps that order for its copy: the copy's Stop before the copy's
            // Play, not after it. None of the game's first SFX banks has one, so it is made in a copy
            // of this bank with the same typed writers: the ult start given, first, a copy of the
            // Stop the ult end makes on the Sound it plays.
            {
                var syn = HircBank.Parse("retrigger", game);
                var endId = VoiceLine.Fnv("sfx_1053001_q_end_play");
                var endStop = syn.Objects.First(o => ((HierarchyEvent)syn.Nodes[endId]).EventActionIds.Contains(o.Id) &&
                                                     o.Node is HierarchyEventAction { EventActionType: CUE4Parse.UE4.Wwise.Enums.EAkActionType.Stop } a &&
                                                     a.ReferencedId == layer.Template);
                var span = HircLayout.Action(endStop.Body, syn.Version);
                var preStop = VoiceLine.Fnv("xzoundwave/check/retrigger-stop");
                var pre = new HircObject { RawType = endStop.RawType, Type = endStop.Type,
                    Body = HircWriter.Action(endStop.Body, span, syn.Version, preStop, layer.Template) };
                pre.Node = HircBank.ReadExact(pre.Type, pre.Body, syn.Version, "retrigger stop");
                syn.Objects.Insert(syn.Objects.IndexOf(endStop) + 1, pre);
                syn.Nodes[preStop] = pre.Node;
                var startObj = syn.Objects.First(o => o.Id == startId && o.Node is HierarchyEvent);
                startObj.Body = HircWriter.Event(startId, [preStop, .. ((HierarchyEvent)startObj.Node).EventActionIds], syn.Version);
                startObj.Node = HircBank.ReadExact(startObj.Type, startObj.Body, syn.Version, "retrigger event");
                syn.Nodes[startId] = startObj.Node;

                var ids = EventLayer.Ids.For("retrigger check");
                var stops = EventLayer.StopsOf(syn, layer.Template);
                EventLayer.Add(syn, startId, new byte[64], ids, layer.Template, stops);
                var after = ((HierarchyEvent)syn.Nodes[startId]).EventActionIds.ToList();
                var mine = Enumerable.Range(0, stops.Count).Select(ids.StopAt).Where(after.Contains).ToList();
                var ends = ((HierarchyEvent)syn.Nodes[endId]).EventActionIds;
                Check("an event that stops and then plays the Sound stops the copy before playing it, not after",
                      stops.Contains(startId) && mine.Count == 1 && after.IndexOf(mine[0]) == after.IndexOf(ids.PlayId) - 1 &&
                      after[^1] == ids.PlayId,
                      $"start event: {string.Join(", ", after.Select(a => a == ids.PlayId ? "PLAY copy" : mine.Contains(a) ? "STOP copy" : a.ToString()))}");
                Check("and the ult's end still stops the copy last", ends.Length > 0 && Enumerable.Range(0, stops.Count).Select(ids.StopAt).Contains(ends[^1]));
            }
            string Verify(string label, string pak)
            {
                var bytes = pak is null ? null : BankOf(pak);
                if (bytes is null) return $"{label}: no {Path.GetFileName(layer.Bank)} in {pak ?? "(no pak)"}";
                var hb = HircBank.Parse(label, bytes);
                var ev = (HierarchyEvent)hb.Nodes[startId];
                var entries = LayerManifest.Entries(LayerManifest.Read(hb));
                var problems = new List<string>();
                if (ev.EventActionIds.Length != gameActions + 1) problems.Add($"ult has {ev.EventActionIds.Length} actions, the game's {gameActions}");
                if (entries.Count != 1) problems.Add($"manifest names {entries.Count} added sound(s)");
                foreach (var e in entries)
                {
                    if (!ev.EventActionIds.Contains(e.PlayId)) problems.Add("the ult does not play it");
                    if (hb.Nodes.GetValueOrDefault(e.SoundId) is not HierarchySoundSfxVoice s || s.Source.SourceId != e.MediaId) problems.Add("no Sound for it");
                    if (!hb.Media.Any(m => m.Id == e.MediaId)) problems.Add("its audio is not embedded");
                    if (e.Template != layer.Template) problems.Add($"copies {e.Template}, not {layer.Template}");
                    if (e.Stops.Count == 0) problems.Add("nothing stops it");
                    foreach (var st in e.Stops)
                        if (hb.Nodes.GetValueOrDefault(st.EventId) is not HierarchyEvent se || !se.EventActionIds.Contains(st.StopId))
                            problems.Add($"stop event {st.EventId} does not stop it");
                }
                foreach (var e in entries)
                    if (hb.Nodes.GetValueOrDefault(e.SoundId) is HierarchySoundSfxVoice own &&
                        own.BaseParams.PropBundle.Props?.Any(p => p.Id == 0x3A) == true)
                        problems.Add("it loops");
                var named = string.Join(", ", entries.SelectMany(e => e.Stops).Select(st => main.CurrentSession.EventName(st.EventId) ?? st.EventId.ToString()));
                return problems.Count == 0 ? $"ok; media {entries[0].MediaId}, stopped by {named}" : string.Join("; ", problems);
            }

            var built = Path.Combine(dir, "built");
            Pump(main.BuildForShot(built));
            var pak = Directory.Exists(built) ? Directory.GetFiles(built, "*.pak").FirstOrDefault() : null;
            Check("Build mod writes a .pak", pak is not null, main.Status.Text);
            var v1 = Verify("built", pak);
            Check("its bank: the ult plays the added sound, its end stops it, the XZWV manifest names it", v1.StartsWith("ok"), v1);
            Check("the build log says so", File.Exists(Path.Combine(built, "build-log.txt")) &&
                                           File.ReadAllText(Path.Combine(built, "build-log.txt")).Contains("LAYER"));

            // Built again with that mod open as the base: the earlier copy goes, this one goes in, once.
            if (pak is not null)
            {
                main.AnswerForShot = title => title == "Building on a loaded mod";
                Pump(main.OpenModForShot(pak));
                var again = Path.Combine(dir, "built-again");
                Pump(main.BuildForShot(again));
                var pak2 = Directory.Exists(again) ? Directory.GetFiles(again, "*.pak").FirstOrDefault() : null;
                var v2 = Verify("again", pak2);
                Check("built on the built mod: still one added sound, not two", v2.StartsWith("ok"), v2 + " | " + main.Status.Text);

                // Someone else's mod (here: a project without the sound) built on: its added sound
                // stays, as everything else it changed does. A mute in the same bank makes the build
                // rewrite that bank from the mod's copy.
                // Saved first: opening the mod changed the project, and New would ask about it.
                Pump(main.SaveProjectForShot(path));
                Pump(main.NewProjectForShot());
                Pump(main.OpenModForShot(pak));
                main.ShowSkinSync("1053001", "sfx");
                Pump();
                var other = main.SoundsForShot.Rows.FirstOrDefault(r => r.EventName is not null && r.EventName != start && !r.Streamed);
                if (other is not null)
                {
                    grid.SelectedItem = other;
                    Pump();
                    main.MuteForShot();
                }
                var onTop = Path.Combine(dir, "built-on-mod");
                Pump(main.BuildForShot(onTop));
                var pak4 = Directory.Exists(onTop) ? Directory.GetFiles(onTop, "*.pak").FirstOrDefault() : null;
                var v4 = Verify("on-mod", pak4);
                Check("a project without the sound, built on the mod, keeps the mod's added sound", v4.StartsWith("ok"),
                      v4 + " | " + main.Status.Text);
                if (other is not null) { other.Muted = false; main.RowStateChangedForShot([other]); }
                Pump(main.CloseModsForShot());
                main.AnswerForShot = null;

                // Update mod…: taken out before the rebase, put back on the current bank after it.
                // It needs the bank index, which a headless load does not start on its own.
                if (!main.CurrentSession.BankIndexReady) main.CurrentSession.IndexBanks(_ => { });
                var updated = Path.Combine(dir, "updated");
                Pump(main.UpdateForShot(pak, updated));
                var pak3 = Directory.Exists(updated) ? Directory.GetFiles(updated, "*.pak", SearchOption.AllDirectories).FirstOrDefault() : null;
                var v3 = Verify("updated", pak3);
                Check("Update mod puts it back", v3.StartsWith("ok"), v3 + " | " + main.Status.Text);
                var report = Directory.Exists(updated)
                    ? Directory.GetFiles(updated, "*.txt", SearchOption.AllDirectories).Select(File.ReadAllText).FirstOrDefault(t => t.Contains("ADDED SOUND"))
                    : null;
                Check("and its report says so, naming the bank it wrote", report?.Contains("put back") == true &&
                      report.Contains("wrote  " + layer.Bank),
                      report is null ? "(no report mentions it)" : "");
                Check("and the status line counts it", main.Status.Text.Contains("added sounds: 1 put back"), main.Status.Text);
            }

            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the added-sounds check: " + ex);
            return 1;
        }
    }
}
