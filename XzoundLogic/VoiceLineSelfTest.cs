using System.IO;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.UE4.Wwise.Enums;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;
using Newtonsoft.Json;
using Xzound;

namespace MRAudioKit;

/// <summary>
/// The voice-line engine, end to end, against the one line already proven in a match:
/// Hela, a kill line for Iron Man (1034), with audio of the caller's own. The engine builds
/// and packages it, and then CUE4Parse -- not UAssetAPI, which wrote it -- reads the game
/// with the mod mounted on top, the way the game's own read order resolves it, and checks
/// every link from the VoiceItem entry down to the decoded audio.
/// </summary>
public static class VoiceLineSelfTest
{
    public static int Run(string audioPath, string outDir)
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}"); if (!ok) fails++; }

        var settings = Settings.Load();
        var session = new GameSession();
        session.Mount(settings, _ => { });
        var set = VoiceSets.Discover(session).Single(s => s.HeroId == "1024" && s.IsDefault);
        Console.WriteLine($"voice set: {set.Label}  table {set.TablePackage}  items {string.Join(", ", set.ItemPackages)}  skins {set.SkinIds.Count}");

        var pcm = AudioDecode.ToPcm(audioPath);
        var audio = new VoiceLineBuilder.Audio(XzoundCore.Encode(pcm), pcm.Pcm.Length / (double)(pcm.SampleRate * pcm.Channels * (pcm.Bits / 8)));
        var gameIds = VoiceLineBuilder.AllVoiceIds(session);
        // A new keyed entry copied from a list whose first entry has zero keys (the case that
        // once wrote a broken asset), and one more line in a single list, heard by enemies too
        // (a flag that is zero in the row it is copied from).
        var line = new VoiceLine
        {
            Set = set.Key,
            Target = "KillVoices.KillVoices[+HeroID=1034,SculptID=0].Voices",
            Subtitle = "[XzoundWave engine test] Iron Man is down",
            VoiceId = VoiceLineBuilder.AllocateVoiceId(session, set, [], gameIds),
        };
        var extra = new VoiceLine
        {
            Set = set.Key,
            Target = "HealthPackVoices.Voices",
            Subtitle = "[XzoundWave engine test] health pack",
            HearEnemies = true,
            VoiceId = VoiceLineBuilder.AllocateVoiceId(session, set, [line.VoiceId], gameIds),
        };
        // A second line for the same new victim entry: it must join the entry the first one
        // made, not make another (two entries with one key: the game keeps only the later).
        var second = new VoiceLine
        {
            Set = set.Key,
            Target = line.Target,
            Subtitle = "[XzoundWave engine test] Iron Man is down, again",
            VoiceId = VoiceLineBuilder.AllocateVoiceId(session, set, [line.VoiceId, extra.VoiceId], gameIds),
        };
        // A keyed target naming an entry the game already has: the line joins it.
        var building = new VoiceLine
        {
            Set = set.Key,
            Target = "BuildingDestroyedVoices[+BuildingTag.TagName=Voice.Gameplay.Building.MoonKnightAnka].Voices",
            Subtitle = "[XzoundWave engine test] ankh destroyed",
            VoiceId = VoiceLineBuilder.AllocateVoiceId(session, set, [line.VoiceId, extra.VoiceId, second.VoiceId], gameIds),
        };
        VoiceLine[] lines = [line, extra, second, building];
        foreach (var l in lines)
        {
            Console.WriteLine($"line: VoiceID {l.VoiceId}, event {l.EventName} ({l.EventId}), media {l.MediaId}, at {l.Target}");
            Check(l.VoiceId / 10000 == 1024 && !gameIds.Contains(l.VoiceId), $"allocated VoiceID {l.VoiceId} is Hela's and free in all {gameIds.Count:N0} game ids");
        }

        var stage = Path.Combine(outDir, "stage");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        var built = VoiceLineBuilder.Build(session, settings, new GameAssetExtractor(settings, session), set, lines,
                                           id => lines.Any(l => l.MediaId == id) ? audio : null, stage, Console.WriteLine);
        var outputBase = Path.Combine(outDir, "mod", "XzoundVoiceLineTest_9999999_P");
        foreach (var f in Directory.Exists(Path.GetDirectoryName(outputBase)) ? Directory.GetFiles(Path.GetDirectoryName(outputBase)!) : []) File.Delete(f);
        var (ok, message) = ModPackager.Package(stage, outputBase);
        Check(ok, $"packaged: {message}");

        // ---- a rebuild over this build (a loaded mod as the base) ------------------------
        // The earlier build's lines come out of the banks and the new set goes in; the result
        // must be the very bytes a build of the new set straight from the game gives. Done
        // before the mod is mounted below: once it is, the game's own copies are not what the
        // session resolves.
        VoiceLine[] fewer = [line, extra];
        var overEarlier = Path.Combine(outDir, "stage-over-earlier");
        var fresh = Path.Combine(outDir, "stage-fresh");
        foreach (var d in new[] { overEarlier, fresh }) if (Directory.Exists(d)) Directory.Delete(d, true);
        var extractor = new GameAssetExtractor(settings, session);
        var rebuilt = VoiceLineBuilder.Build(session, settings, extractor, set, fewer, id => audio, overEarlier, null,
                                             bankSource: bp => File.Exists(Path.Combine(stage, bp)) ? File.ReadAllBytes(Path.Combine(stage, bp)) : null);
        VoiceLineBuilder.Build(session, settings, extractor, set, fewer, id => audio, fresh, null);
        var banks = Directory.GetFiles(fresh, "*.bnk", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(fresh, f)).ToList();
        var took = rebuilt.Log.Count(s => s.Contains("took out line"));
        Check(banks.Count == 3 && banks.All(b => File.ReadAllBytes(Path.Combine(fresh, b)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(overEarlier, b))))
              && took == 3 * lines.Length,
              $"rebuilt over the earlier build: took out its {lines.Length} lines from each of {banks.Count} banks ({took} removals), " +
              "and every bank is byte-identical to a fresh build of the 2 lines kept");

        // ---- read it back the way the game would see it -------------------------------
        var P = session.Provider;
        var tbl = VoiceSet.VfsPath(set.TablePackage)[..^".uasset".Length];
        var itm = VoiceSet.VfsPath(set.ItemPackages[0])[..^".uasset".Length];
        string J(object o) => JsonConvert.SerializeObject(o);
        var stockRows = P.LoadPackageObject<UDataTable>(tbl + "." + Path.GetFileName(tbl)).RowMap.ToDictionary(k => k.Key.Text, v => J(v.Value.Properties.Select(p => new { n = p.Name.Text, v = p.Tag?.GenericValue })));
        var stockItem = P.LoadPackageObject(itm + "." + Path.GetFileName(itm)).Properties.ToDictionary(p => p.Name.Text, p => J(p.Tag?.GenericValue));
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(outputBase)!).Where(f => f.EndsWith(".pak") || f.EndsWith(".utoc"))) P.RegisterVfs(f);
        P.Mount();
        foreach (var (g, k) in P.Keys.ToList()) P.SubmitKey(g, k);

        var item = P.LoadPackageObject(itm + "." + Path.GetFileName(itm));
        var modItem = item.Properties.ToDictionary(p => p.Name.Text, p => J(p.Tag?.GenericValue));
        var changed = modItem.Keys.Union(stockItem.Keys).Where(k => modItem.GetValueOrDefault(k) != stockItem.GetValueOrDefault(k)).ToList();
        Check(changed.OrderBy(x => x).SequenceEqual(["BuildingDestroyedVoices", "HealthPackVoices", "KillVoices"]) && stockItem.Count == modItem.Count,
              $"VoiceItem: all {modItem.Count} fields read; only KillVoices, HealthPackVoices and BuildingDestroyedVoices changed");
        Check(modItem["KillVoices"].Contains($"{{\"HeroID\":1034,\"SculptID\":0,\"Voices\":[{line.VoiceId},{second.VoiceId}]}}")
              && modItem["KillVoices"].Split("\"HeroID\":1034,").Length == 2,
              $"KillVoices: ONE new 1034 entry holding both lines ({line.VoiceId}, {second.VoiceId})");
        Check(modItem["HealthPackVoices"].Contains($",{extra.VoiceId}]"), $"HealthPackVoices: +{extra.VoiceId}");
        int Entries(string json) => Newtonsoft.Json.Linq.JArray.Parse(json).Count;
        var anka = Newtonsoft.Json.Linq.JArray.Parse(modItem["BuildingDestroyedVoices"])
                       .FirstOrDefault(e => (string)e.SelectToken("BuildingTag.TagName") == "Voice.Gameplay.Building.MoonKnightAnka");
        Check(Entries(modItem["BuildingDestroyedVoices"]) == Entries(stockItem["BuildingDestroyedVoices"])
              && anka?["Voices"] is Newtonsoft.Json.Linq.JArray av && av.Count > 1 && (int)av.Last() == building.VoiceId,
              $"BuildingDestroyedVoices: still {Entries(modItem["BuildingDestroyedVoices"])} entries; the existing MoonKnightAnka one gained {building.VoiceId}");

        var dt = P.LoadPackageObject<UDataTable>(tbl + "." + Path.GetFileName(tbl));
        var modRows = dt.RowMap.ToDictionary(k => k.Key.Text, v => J(v.Value.Properties.Select(p => new { n = p.Name.Text, v = p.Tag?.GenericValue })));
        var diff = modRows.Keys.Union(stockRows.Keys).Where(k => modRows.GetValueOrDefault(k) != stockRows.GetValueOrDefault(k)).ToList();
        Check(diff.OrderBy(x => x).SequenceEqual(lines.Select(x => x.VoiceId.ToString()).OrderBy(x => x)),
              $"voice table: {modRows.Count} rows, only the {lines.Length} new ones differ from the game's");
        var extraRow = dt.RowMap.Single(r => r.Key.Text == extra.VoiceId.ToString()).Value;
        Check(extraRow.GetOrDefault<bool>("bEnemyHear") && extraRow.GetOrDefault<bool>("bLocallyHear") && extraRow.GetOrDefault<bool>("bFriendlyHear"),
              $"row {extra.VoiceId}: heard by self, allies AND enemies, as set (bEnemyHear is false in the row it was copied from)");
        var row = dt.RowMap.Single(r => r.Key.Text == line.VoiceId.ToString()).Value;
        var evObj = row.GetOrDefault<FPackageIndex>("Event")?.Load<UAkAudioEvent>();
        Check(evObj is not null && J(evObj.EventCookedData).Contains($"\"EventId\":{line.EventId}") && !J(evObj.EventCookedData).Contains("\"MediaId\""),
              $"the row's event {evObj?.GetPathName()} loads, id {line.EventId}, no streamed media");
        Check(Math.Abs((evObj?.MaximumDuration ?? 0) - audio.Seconds) < 0.01, $"event duration {evObj?.MaximumDuration:0.000} s = the clip's {audio.Seconds:0.000} s");

        foreach (var bp in new[] { "English(US)", "Chinese(CN)", "Japanese(JP)" }.Select(l => $"Marvel/Content/WwiseAudio/{l}/bnk_vo_1024001.bnk"))
        {
            var winner = P.MountedVfs.Where(v => v.Files.ContainsKey(bp)).OrderByDescending(v => v.ReadOrder).First();
            var bytes = winner.Files[bp].Read();
            var r = new WwiseReader(new FWwiseArchive(Path.GetFileName(bp), bytes), new WwiseArchiveSource());
            var nodes = r.Hierarchies.Where(h => h.Data is not null).ToDictionary(h => h.Data.Id, h => h.Data);
            HierarchySoundSfxVoice SoundOf(VoiceLine l)
            {
                var ev = nodes.GetValueOrDefault(l.EventId) as HierarchyEvent;
                var act = ev is null ? null : nodes.GetValueOrDefault(ev.EventActionIds.Single()) as HierarchyEventAction;
                return act is null ? null : nodes.GetValueOrDefault(act.ReferencedId) as HierarchySoundSfxVoice;
            }
            var s = SoundOf(line);
            var allLinked = lines.All(l => SoundOf(l) is { Source.SourceType: EAKBKSourceType.Data } snd && snd.Source.SourceId == l.MediaId);
            var secs = BnkBuilder.ReadSections(bytes);
            var entry = BnkBuilder.ReadDidx(secs.Single(x => x.Tag == "DIDX").Body).SingleOrDefault(m => m.Id == line.MediaId);
            double decoded = 0;
            if (entry is not null)
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"xzvl_{line.MediaId}.wem");
                File.WriteAllBytes(tmp, secs.Single(x => x.Tag == "DATA").Body.AsSpan((int)entry.Offset, (int)entry.Size).ToArray());
                var back = AudioDecode.ToPcm(tmp);
                decoded = back.Pcm.Length / (double)(back.SampleRate * back.Channels * (back.Bits / 8));
                File.Delete(tmp);
            }
            Check(winner.Name.StartsWith("XzoundVoiceLineTest") && s is { Source.SourceType: EAKBKSourceType.Data } && s.Source.SourceId == line.MediaId
                  && Math.Abs(decoded - audio.Seconds) < 0.1 && secs.Any(x => x.Tag == VoiceLineBuilder.ManifestTag) && allLinked,
                  $"{bp.Split('/')[3]}: from {winner.Name}; all {lines.Length} events -> action -> in-memory Sound (parent {s?.BaseParams.DirectParentId}) = {allLinked}; audio decodes to {decoded:0.000} s; manifest present");
        }
        Console.WriteLine($"\nmod: {outputBase}.utoc/.ucas/.pak");
        Console.WriteLine(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
