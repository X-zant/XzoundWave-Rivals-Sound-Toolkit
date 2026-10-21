using System.IO;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

/// <summary>
/// XzoundWave --layertest: added sounds (EventLayer) against the game's own banks, no window.
///
/// 1023's banks are the case that taught the rules: its ult start plays one Sound directly, and
/// the game has muted it (output bus at -96 dB on the Sound and its mixer), while what you hear
/// is a random container. A copy of the muted Sound shipped silent. So: a muted Sound is never
/// copied, the container is (as a one-child copy carrying its level, aux sends and routing), a
/// row named after an event that only stops its sound leads to the event that plays it, the app's
/// SILENT label counts the output-bus volume, and every layer comes back out of its bank exactly.
/// Emma Frost's ult start, which worked in game, stays a plain Sound copy.
/// </summary>
public static class LayerSelfTest
{
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}{(detail is null ? "" : "   " + detail)}");
            if (!ok) fails++;
        }

        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });
        if (session.Provider is null) { W("the game did not mount; set it up first"); return 2; }
        string Path(string stem) => session.BankPaths.First(p => System.IO.Path.GetFileName(p).Equals(stem + ".bnk", StringComparison.OrdinalIgnoreCase) &&
                                                                 GameSession.LanguageOf(p) is null);
        var rocketPath = Path("bnk_sfx_1023001");
        var emmaPath = Path("bnk_sfx_1053001");
        var rocketBytes = session.Bank(rocketPath).Read();
        var emmaBytes = session.Bank(emmaPath).Read();
        var rocket = HircBank.Parse("rocket", rocketBytes);
        var emma = HircBank.Parse("emma", emmaBytes);
        uint E(string name) => VoiceLine.Fnv(name);
        var wem = new byte[4096];
        new Random(1).NextBytes(wem);

        W("what is heard");
        Check("1023's ult start Sound 1670445 is muted (output bus -96 dB on it and on its mixer)", EventLayer.IsMuted(rocket, 1670445));
        Check("the ult start's container sounds are heard", ((HierarchyRandomSequenceContainer)rocket.Nodes[937071405]).ChildIds
              .All(k => EventLayer.Heard(rocket, (HierarchySoundSfxVoice)rocket.Nodes[k])));
        Check("Emma's ult start Sound 149773038 is heard", EventLayer.Heard(emma, (HierarchySoundSfxVoice)emma.Nodes[149773038]));

        W("");
        W("what a layer copies");
        var star = EventLayer.TemplateFor(rocket, E("sfx_1023001_q_star_play"), 256755055, out _);
        Check("1023's ult start: the random container, not the muted Sound, even from the muted Sound's row", star == 937071405, $"{star}");
        Check("1023's fire loop: its Sound", EventLayer.TemplateFor(rocket, E("sfx_1023001_left_lp_play"), null, out _) == 698353017);
        var release = EventLayer.TemplateFor(rocket, E("sfx_1023001_q_release_play"), null, out var releaseWhy);
        Check("1023's ult end: refused, saying why", release is null && releaseWhy.Contains("muted") && releaseWhy.Contains("layer container"),
              releaseWhy);
        Check("Emma's ult start: her Sound, as before", EventLayer.TemplateFor(emma, E("sfx_1053001_q_start_play"), null, out _) == 149773038);
        var players = EventLayer.PlayersOf(rocket, 395501421);
        // Emma's ult row names q_start_play, which only resets the playlist its sound is in; the
        // event on the row still takes the layer, as it did in the in-game test.
        Check("Emma's ult row: its own event takes the layer, whoever else plays the row's sound",
              EventLayer.TemplateFor(emma, E("sfx_1053001_q_start_play"), 216027850, out _) == 149773038 &&
              !EventLayer.PlayersOf(emma, 216027850).Contains(E("sfx_1053001_q_start_play")));
        Check("the fire loop's row event, left_end_play, takes none; left_lp_play plays the row's sound",
              EventLayer.TemplateFor(rocket, E("sfx_1023001_left_end_play"), 395501421, out _) is null &&
              players.Contains(E("sfx_1023001_left_lp_play")) && !players.Contains(E("sfx_1023001_left_end_play")),
              string.Join(", ", players.Select(p => session.EventName(p) ?? p.ToString())));

        W("");
        W("a layer inside a random container (1023's ult start)");
        {
            var bank = HircBank.Parse("rocket", rocketBytes);
            var ids = EventLayer.Ids.For("layertest/rocket");
            var start = E("sfx_1023001_q_star_play");
            var added = EventLayer.Add(bank, start, wem, ids, star, EventLayer.StopsOf(bank, star.Value));
            var original = (HierarchyRandomSequenceContainer)bank.Nodes[937071405];
            var copy = bank.Nodes.GetValueOrDefault(ids.ContainerId) as HierarchyRandomSequenceContainer;
            var sound = bank.Nodes.GetValueOrDefault(ids.SoundId) as HierarchySoundSfxVoice;
            var ev = (HierarchyEvent)bank.Nodes[start];
            var mixer = (HierarchyActorMixer)bank.Nodes[original.BaseParams.DirectParentId];
            Check("the event's new Play is the container copy's", bank.Nodes[ev.EventActionIds[^1]] is HierarchyEventAction a && a.ReferencedId == ids.ContainerId);
            Check("the copy holds only the new Sound, which holds the new medium",
                  copy?.ChildIds.SequenceEqual([ids.SoundId]) == true && sound?.Source.SourceId == ids.MediaId &&
                  sound.BaseParams.DirectParentId == ids.ContainerId && bank.Media.Any(m => m.Id == ids.MediaId));
            Check("the copy sits beside the original in its mixer", copy?.BaseParams.DirectParentId == mixer.Id && mixer.ChildIds.Contains(ids.ContainerId));
            Check("with the original's level and aux send",
                  copy is not null && (copy.BaseParams.PropBundle.Props ?? []).Select(p => (p.Id, p.Value.u32))
                      .SequenceEqual((original.BaseParams.PropBundle.Props ?? []).Select(p => (p.Id, p.Value.u32))) &&
                  copy.BaseParams.AuxParams?.AuxIds?.SequenceEqual(original.BaseParams.AuxParams?.AuxIds ?? []) == true);
            Check("and the new Sound is heard", sound is not null && EventLayer.Heard(bank, sound));
            Check("the manifest entry names the container", LayerManifest.Of("layertest/rocket", "sfx_1023001_q_star_play", start, added, false).Container == ids.ContainerId);

            // Written, read back, and taken out again: the game's bank, byte for byte.
            var manifest = LayerManifest.Read(bank);
            LayerManifest.Write(bank, manifest, [LayerManifest.Of("layertest/rocket", "sfx_1023001_q_star_play", start, added, false)], rocketPath, rocketBytes);
            var written = bank.Write();
            var back = HircBank.Parse("back", written);
            Check("it reads back", back.Nodes.ContainsKey(ids.ContainerId) && back.Nodes.ContainsKey(ids.SoundId));
            var taken = LayerManifest.TakeOut(back, LayerManifest.Read(back));
            LayerManifest.Write(back, LayerManifest.Read(back), [], null, null);
            Check("taking it out gives the game's bank back, byte for byte", taken.Count == 1 && back.Write().AsSpan().SequenceEqual(rocketBytes),
                  $"{taken.Count} taken, {back.Write().Length:N0} B vs {rocketBytes.Length:N0} B");

            // An update puts it back onto a bank, anchored on the container.
            var re = LayerBuild.Readd(rocketBytes, rocketBytes, rocketPath, taken, _ => false, session.EventName);
            var reBank = re.Bytes is null ? null : HircBank.Parse("readd", re.Bytes);
            Check("Update mod puts it back inside a container copy", re.Added == 1 && reBank is not null &&
                  LayerManifest.Entries(LayerManifest.Read(reBank)).SingleOrDefault()?.Container is { } c && c != 0 &&
                  reBank.Nodes.ContainsKey(c), string.Join(" | ", re.Log));
        }

        W("");
        W("a plain Sound layer (Emma Frost's ult start), and out again");
        {
            var bank = HircBank.Parse("emma", emmaBytes);
            var ids = EventLayer.Ids.For("layertest/emma");
            var start = E("sfx_1053001_q_start_play");
            var stops = EventLayer.StopsOf(bank, 149773038);
            var added = EventLayer.Add(bank, start, wem, ids, 149773038, stops);
            Check("a Sound beside hers, no container", added.Container == 0 && bank.Nodes.ContainsKey(ids.SoundId) && !bank.Nodes.ContainsKey(ids.ContainerId));
            LayerManifest.Write(bank, LayerManifest.Read(bank), [LayerManifest.Of("layertest/emma", "sfx_1053001_q_start_play", start, added, true)], emmaPath, emmaBytes);
            var back = HircBank.Parse("back", bank.Write());
            var taken = LayerManifest.TakeOut(back, LayerManifest.Read(back));
            LayerManifest.Write(back, LayerManifest.Read(back), [], null, null);
            Check("taking it out gives the game's bank back, byte for byte", taken.Count == 1 && back.Write().AsSpan().SequenceEqual(emmaBytes));
        }

        W("");
        W("a layer v1.1.0 made from the muted Sound (1023's ult start, as the in-game failure was built)");
        {
            var name = "layertest/v110";
            var ids = EventLayer.Ids.For(name);
            var old = new ProjectLayer
            {
                Bank = rocketPath, Event = E("sfx_1023001_q_star_play"), EventName = "sfx_1023001_q_star_play",
                Template = 1670445, Stops = false, Name = name, MediaId = ids.MediaId,
            };
            var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XzoundWave", "layertest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var built = LayerBuild.Apply(session, [old], _ => wem, work, BankBase.None);
                var bytes = File.ReadAllBytes(System.IO.Path.Combine(work, built.Outputs.Single()));
                var entry = LayerManifest.Entries(LayerManifest.Read(HircBank.Parse("v110", bytes))).SingleOrDefault();
                Check("Build mod copies the ult start's container instead, and says why",
                      entry is { Template: 937071405, Container: not 0 } && built.Log.Any(l => l.Contains("is muted in the game")),
                      string.Join(" | ", built.Log));
            }
            finally { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

            var oldEntry = new LayerManifest.Entry(name, "sfx_1023001_q_star_play", E("sfx_1023001_q_star_play"), 1670445, 420753244, false,
                                                   ids.SoundId, ids.PlayId, ids.MediaId, []);
            var re = LayerBuild.Readd(rocketBytes, rocketBytes, rocketPath, [new LayerManifest.Taken(oldEntry, wem)], _ => false, session.EventName);
            var reEntry = re.Bytes is null ? null : LayerManifest.Entries(LayerManifest.Read(HircBank.Parse("readd", re.Bytes))).SingleOrDefault();
            Check("Update mod puts it back on the container, and says why",
                  re.Added == 1 && reEntry is { Template: 937071405, Container: not 0 } && re.Log.Any(l => l.Contains("is muted in the game")),
                  string.Join(" | ", re.Log));
        }

        W("");
        W("aux sends");
        Check("the ult start's container sends to an aux bus (the bit CUE4Parse names OverridePriority)",
              ((HierarchyRandomSequenceContainer)rocket.Nodes[937071405]).BaseParams.AuxParams is { } aux && EventLayer.SendsToAux(aux));
        Check("the muted Sound sends to none",
              ((HierarchySoundSfxVoice)rocket.Nodes[1670445]).BaseParams.AuxParams is not { } mutedAux || !EventLayer.SendsToAux(mutedAux));

        W("");
        W("the app's SILENT label");
        {
            var rows = ModBank.ToSounds(session, new ModBank.Bank("bnk_sfx_1023001.bnk", rocketPath, rocketBytes)).Rows;
            var muted = rows.FirstOrDefault(r => r.MediaId == 256755055);
            var heard = rows.FirstOrDefault(r => r.MediaId == 1059905049);
            Check("the muted ult-start Sound reads SILENT", muted?.Applied?.Silenced == true, muted?.AppliedLabel);
            Check("the container's sound does not", heard?.Applied?.Silenced == false, heard?.AppliedLabel);
        }

        W("");
        W("a playback limit above the layer (1023's ult voice line: one sound per character)");
        {
            // In game the music played and the line did not: the voice mixer drops the older sound.
            var voPath = session.BankPaths.First(p => System.IO.Path.GetFileName(p).Equals("bnk_vo_1023001.bnk", StringComparison.OrdinalIgnoreCase) &&
                                                      string.Equals(GameSession.LanguageOf(p), session.Language, StringComparison.OrdinalIgnoreCase));
            var voBytes = session.Bank(voPath).Read();
            var vo = HircBank.Parse("vo", voBytes);
            var line = E("vo_10230045_q_p13_play");
            var tmpl = EventLayer.TemplateFor(vo, line, null, out var why);
            Check("the ult line's own Sound is what is copied", tmpl == 72452803, why ?? $"{tmpl}");
            var ids = EventLayer.Ids.For("layertest/vo");
            var added = EventLayer.Add(vo, line, wem, ids, tmpl);
            var mine = ((HierarchySoundSfxVoice)vo.Nodes[ids.SoundId]).BaseParams.AdvSettingsParams;
            var theirs = ((HierarchySoundSfxVoice)vo.Nodes[72452803]).BaseParams.AdvSettingsParams;
            Check("the copy is left out of the mixer's limit, every other playback setting the line's own",
                  (Convert.ToByte(mine.AdvSettingsFlags) & HircWriter.IgnoreParentMaxNumInst) != 0 &&
                  (Convert.ToByte(mine.AdvSettingsFlags) & ~HircWriter.IgnoreParentMaxNumInst) == Convert.ToByte(theirs.AdvSettingsFlags) &&
                  mine.MaxNumInstance == theirs.MaxNumInstance && mine.VirtualQueueBehavior == theirs.VirtualQueueBehavior &&
                  mine.BelowThresholdBehavior == theirs.BelowThresholdBehavior && mine.HdrEnvelopeFlags == theirs.HdrEnvelopeFlags,
                  $"flags {mine.AdvSettingsFlags} vs {theirs.AdvSettingsFlags}, max {mine.MaxNumInstance}");
            Check("and the build log says why", added.Log.Any(l => l.Contains("left out of that count")), string.Join(" | ", added.Log));
            LayerManifest.Write(vo, LayerManifest.Read(vo), [LayerManifest.Of("layertest/vo", "vo_10230045_q_p13_play", line, added, false)], voPath, voBytes);
            var back = HircBank.Parse("back", vo.Write());
            LayerManifest.TakeOut(back, LayerManifest.Read(back));
            LayerManifest.Write(back, LayerManifest.Read(back), [], null, null);
            Check("taking it out gives the voice bank back, byte for byte", back.Write().AsSpan().SequenceEqual(voBytes));

            var em = HircBank.Parse("emma", emmaBytes);
            var emIds = EventLayer.Ids.For("layertest/emma-limit");
            EventLayer.Add(em, E("sfx_1053001_q_start_play"), wem, emIds, 149773038);
            Check("with no limit above it (Emma's ult), the copy's playback settings are her Sound's, untouched",
                  ((HierarchySoundSfxVoice)em.Nodes[emIds.SoundId]).BaseParams.AdvSettingsParams.AdvSettingsFlags ==
                  ((HierarchySoundSfxVoice)em.Nodes[149773038]).BaseParams.AdvSettingsParams.AdvSettingsFlags);
        }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
