using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.UE4.Wwise.Enums;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;
using Newtonsoft.Json;

namespace MRAudioKit;

/// <summary>
/// A built voice-line mod, checked against the project it was built from: the game is
/// mounted with the mod on top -- through CUE4Parse, not the UAssetAPI that wrote it -- and
/// every line of the project is followed from its VoiceItem entry to its decoded audio.
/// Nothing else may have changed, and nothing may be there twice.
///
/// xzoundwave --voicelinecheck &lt;project.xzw&gt; &lt;modDir&gt; [swappedMediaId]
/// </summary>
public static class VoiceLineCheck
{
    public static int Run(string projectPath, string modDir, uint swapped = 0)
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}"); if (!ok) fails++; }
        string J(object o) => JsonConvert.SerializeObject(o);

        var project = Project.Load(projectPath);
        var settings = Settings.Load();
        var session = new GameSession();
        session.Mount(settings, _ => { });
        var P = session.Provider;
        var sets = VoiceSets.Discover(session);
        Console.WriteLine($"{project.VoiceLines.Count} line(s) in {Path.GetFileName(projectPath)}, mod in {modDir}");

        // ---- the game as it ships, before the mod is mounted ---------------------------
        var groups = project.VoiceLines.GroupBy(l => l.Set).Select(g =>
        {
            var set = sets.FirstOrDefault(s => s.Key == g.Key) ?? throw new InvalidDataException($"voice set not in this game: {g.Key}");
            return (Set: set, Lines: g.ToList());
        }).ToList();
        string Pkg(string package) => VoiceSet.VfsPath(package)[..^".uasset".Length];
        string Obj(string package) => Pkg(package) + "." + Path.GetFileName(Pkg(package));
        // Tables and VoiceItems by package, not by set: two sets can share one (a skin with its
        // own table and the hero's VoiceItem), and then both sets' lines are in it.
        var lineSet = groups.SelectMany(g => g.Lines.Select(l => (l, g.Set))).ToDictionary(x => x.l, x => x.Set);
        var tables = groups.Select(g => g.Set.TablePackage).Distinct().ToList();
        var items = groups.SelectMany(g => g.Set.ItemPackages).Distinct().ToList();
        var stockRows = tables.ToDictionary(t => t, t =>
            P.LoadPackageObject<UDataTable>(Obj(t)).RowMap.ToDictionary(r => r.Key.Text, r => J(r.Value.Properties.Select(p => new { n = p.Name.Text, v = p.Tag?.GenericValue }))));
        var stockItems = items.ToDictionary(i => i, i =>
            P.LoadPackageObject(Obj(i)).Properties.ToDictionary(p => p.Name.Text, p => J(p.Tag?.GenericValue)));
        // How many entries the game itself keys like each line's target: a line must not add one
        // where one existed, and a new entry must be the only one.
        int Same(List<VoiceTriggers.Entry> entries, Dictionary<string, string> keys) =>
            keys.Count > 0 ? entries.Count(e => keys.All(kv => VoiceItemPath.SameKey(e.Keys.GetValueOrDefault(kv.Key), kv.Value))) : 1;
        var stockSame = lineSet.ToDictionary(x => x.Key, x =>
            Same(VoiceTriggers.Entries(session, x.Value, VoiceTriggers.Of(x.Key), x.Key.ItemIn(x.Value)), VoiceTriggers.Condition(x.Key).Keys));
        var heroes = groups.Select(g => g.Set.HeroId).Distinct().ToList();
        var gameBanks = heroes.SelectMany(h => GameSession.Languages.Select(lang => $"Marvel/Content/WwiseAudio/{lang}/bnk_vo_{h}001.bnk"))
                              .Where(bp => session.Bank(bp) is not null)
                              .ToDictionary(bp => bp, bp => session.Bank(bp).Read());
        byte[] swappedStock = null;
        string swappedBank = null;
        if (swapped != 0)
        {
            swappedBank = session.BanksForMedia(swapped).FirstOrDefault()
                          ?? gameBanks.Keys.FirstOrDefault(bp => BnkBuilder.ReadDidx(BnkBuilder.ReadSections(gameBanks[bp]).Single(s => s.Tag == "DIDX").Body).Any(m => m.Id == swapped));
            if (swappedBank is not null) swappedStock = Media(session.Bank(swappedBank).Read(), swapped);
        }

        // ---- the mod on top ---------------------------------------------------------------
        var mine = Directory.GetFiles(modDir).Where(f => f.EndsWith(".pak") || f.EndsWith(".utoc")).ToList();
        Check(mine.Count > 0, $"mod containers: {string.Join(", ", mine.Select(Path.GetFileName))}");
        foreach (var f in mine) P.RegisterVfs(f);
        P.Mount();
        foreach (var (g, k) in P.Keys.ToList()) P.SubmitKey(g, k);
        var modNames = mine.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The VoiceItems: one with lines changed only in the fields those lines' triggers live in;
        // every other VoiceItem of the sets (another form's) is untouched; every field still reads.
        foreach (var itemPkg in items)
        {
            var inIt = lineSet.Where(x => x.Key.ItemIn(x.Value) == itemPkg).Select(x => x.Key).ToList();
            var touched = inIt.Select(l => VoiceTriggers.Of(l)?.Field).ToHashSet();
            var stock = stockItems[itemPkg];
            var mod = P.LoadPackageObject(Obj(itemPkg)).Properties.ToDictionary(p => p.Name.Text, p => J(p.Tag?.GenericValue));
            var changed = mod.Keys.Union(stock.Keys).Where(k => mod.GetValueOrDefault(k) != stock.GetValueOrDefault(k)).ToList();
            Check(mod.Count == stock.Count && changed.All(touched.Contains) && (changed.Count > 0) == (inIt.Count > 0),
                  $"{Path.GetFileName(itemPkg)}: all {mod.Count} fields read; {inIt.Count} line(s) in it; changed {(changed.Count == 0 ? "nothing" : string.Join(", ", changed))}");
        }

        // The tables: exactly the new rows of every set that writes into one differ.
        foreach (var table in tables)
        {
            var dt = P.LoadPackageObject<UDataTable>(Obj(table));
            var modRows = dt.RowMap.ToDictionary(r => r.Key.Text, r => J(r.Value.Properties.Select(p => new { n = p.Name.Text, v = p.Tag?.GenericValue })));
            var diff = modRows.Keys.Union(stockRows[table].Keys).Where(k => modRows.GetValueOrDefault(k) != stockRows[table].GetValueOrDefault(k)).OrderBy(x => x).ToList();
            var ids = lineSet.Where(x => x.Value.TablePackage == table).Select(x => x.Key.VoiceId.ToString()).OrderBy(x => x).ToList();
            Check(diff.SequenceEqual(ids), $"{Path.GetFileName(table)}: {modRows.Count} rows; only the {ids.Count} new ones differ");
        }

        foreach (var (set, lines) in groups)
        {
            Console.WriteLine($"{set.Label}: {lines.Count} line(s)");
            // Each line sits in the entry its target names, in its own form's VoiceItem -- an
            // entry that exists once.
            foreach (var l in lines)
            {
                var t = VoiceTriggers.Of(l);
                var entries = VoiceTriggers.Entries(session, set, t, l.ItemIn(set));
                var (keys, index) = VoiceTriggers.Condition(l);
                var entry = t.Shape == VoiceTriggers.Shape.Single ? entries.FirstOrDefault()
                          : keys.Count > 0 ? VoiceTriggers.Find(entries, keys) : entries.ElementAtOrDefault(index);
                var same = Same(entries, keys);
                // Entries the game itself keys alike (it plays the first) are allowed; a duplicate
                // made by the build is not -- the stock count says which.
                Check(entry is not null && entry.Voices.Contains(l.VoiceId) && same == Math.Max(1, stockSame[l]),
                      $"{l.VoiceId} is in {l.Target} of {Path.GetFileName(l.ItemIn(set))} ({entry?.Voices.Count ?? 0} line(s) there; {same} entr{(same == 1 ? "y" : "ies")} with its keys)");
            }

            // Each row says what the project says.
            var dt = P.LoadPackageObject<UDataTable>(Obj(set.TablePackage));
            foreach (var l in lines)
            {
                var row = dt.RowMap.FirstOrDefault(r => r.Key.Text == l.VoiceId.ToString()).Value;
                if (row is null) { Check(false, $"row {l.VoiceId} missing"); continue; }
                var text = session.Localize(row.GetOrDefault<FText>("Lines"));
                var ev = row.GetOrDefault<FPackageIndex>("Event")?.Load<UAkAudioEvent>();
                Check(row.GetOrDefault<int>("VoiceID") == l.VoiceId && text == l.Subtitle
                      && row.GetOrDefault<bool>("bLocallyHear") == l.HearSelf
                      && row.GetOrDefault<bool>("bFriendlyHear") == l.HearAllies
                      && row.GetOrDefault<bool>("bEnemyHear") == l.HearEnemies
                      && (l.HearRelevant is not { } rel || row.GetOrDefault<bool>("bRelevantHear") == rel),
                      $"row {l.VoiceId}: \"{text}\", heard by self={l.HearSelf} allies={l.HearAllies} enemies={l.HearEnemies}" +
                      $" relevant={row.GetOrDefault<bool>("bRelevantHear")}{(l.HearRelevant is null ? " (as its template)" : "")}");
                Check(ev is not null && J(ev.EventCookedData).Contains($"\"EventId\":{l.EventId}") && !J(ev.EventCookedData).Contains("\"MediaId\"")
                      && ev.GetPathName().EndsWith(l.EventName + "." + l.EventName),
                      $"row {l.VoiceId}: event {l.EventName} loads, id {l.EventId}, no streamed media, {ev?.MaximumDuration:0.000} s");
            }
        }

        // ---- the banks: every line of the hero, once, with its audio; the manifest names them all
        foreach (var hero in heroes)
        {
            var heroLines = groups.Where(g => g.Set.HeroId == hero).SelectMany(g => g.Lines).ToList();
            var durations = heroLines.ToDictionary(l => l.VoiceId, l =>
            {
                var set = groups.First(g => g.Lines.Contains(l)).Set;
                var row = P.LoadPackageObject<UDataTable>(Obj(set.TablePackage)).RowMap.First(r => r.Key.Text == l.VoiceId.ToString()).Value;
                return (double)(row.GetOrDefault<FPackageIndex>("Event")?.Load<UAkAudioEvent>()?.MaximumDuration ?? 0);
            });
            foreach (var (bp, game) in gameBanks.Where(b => b.Key.Contains($"bnk_vo_{hero}001")))
            {
                var winner = P.MountedVfs.Where(v => v.Files.ContainsKey(bp)).OrderByDescending(v => v.ReadOrder).First();
                var bytes = winner.Files[bp].Read();
                var secs = BnkBuilder.ReadSections(bytes);
                var reader = new WwiseReader(new FWwiseArchive(Path.GetFileName(bp), bytes), new WwiseArchiveSource());
                var all = reader.Hierarchies.Where(h => h.Data is not null).Select(h => h.Data).ToList();
                var nodes = all.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
                var didx = BnkBuilder.ReadDidx(secs.Single(s => s.Tag == "DIDX").Body);
                var lang = bp.Split('/')[3];
                Check(modNames.Contains(winner.Name) || modNames.Any(n => winner.Name.StartsWith(Path.GetFileNameWithoutExtension(n))),
                      $"{lang} bnk_vo_{hero}001: loads from {winner.Name}");
                Check(all.Count == nodes.Count && didx.Select(m => m.Id).Distinct().Count() == didx.Count,
                      $"{lang}: {all.Count} objects and {didx.Count} media, no id twice");
                foreach (var l in heroLines)
                {
                    var e = nodes.GetValueOrDefault(l.EventId) as HierarchyEvent;
                    var a = e is null ? null : nodes.GetValueOrDefault(e.EventActionIds.Single()) as HierarchyEventAction;
                    var s = a is null ? null : nodes.GetValueOrDefault(a.ReferencedId) as HierarchySoundSfxVoice;
                    var parent = s is null ? null : nodes.GetValueOrDefault(s.BaseParams.DirectParentId) as HierarchyActorMixer;
                    var decoded = Seconds(Media(bytes, l.MediaId));
                    Check(s is { Source.SourceType: EAKBKSourceType.Data } && s.Source.SourceId == l.MediaId && parent?.ChildIds.Contains(s.Id) == true
                          && Math.Abs(decoded - durations[l.VoiceId]) < 0.05,
                          $"{lang}: {l.EventName} -> action -> in-memory Sound in mixer {parent?.Id}; audio {decoded:0.000} s = event {durations[l.VoiceId]:0.000} s");
                }
                var man = secs.FirstOrDefault(s => s.Tag == VoiceLineBuilder.ManifestTag);
                if (man is null) { Check(false, $"{lang}: no manifest"); continue; }
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(man.Body).TrimEnd(' ', '\0'));
                var named = doc.RootElement.GetProperty("lines").EnumerateArray().Select(x => x.GetProperty("voiceId").GetInt32()).OrderBy(x => x).ToList();
                var builtOn = doc.RootElement.GetProperty("builtOn").GetProperty("sha256").GetString();
                Check(named.SequenceEqual(heroLines.Select(l => l.VoiceId).OrderBy(x => x)),
                      $"{lang}: manifest names exactly the project's {heroLines.Count} line(s) for {hero}: {string.Join(", ", named)}");
                Check(builtOn == Convert.ToHexString(SHA256.HashData(game)).ToLowerInvariant(), $"{lang}: manifest built-on hash is the game's own bank");
            }
        }

        if (swapped != 0)
        {
            var winner = swappedBank is null ? null : P.MountedVfs.Where(v => v.Files.ContainsKey(swappedBank)).OrderByDescending(v => v.ReadOrder).First();
            var now = winner is null ? null : Media(winner.Files[swappedBank].Read(), swapped);
            Check(now is not null && swappedStock is not null && !now.AsSpan().SequenceEqual(swappedStock),
                  $"the swapped sound {swapped} in {swappedBank?.Split('/').Last()} is the staged audio, not the game's ({swappedStock?.Length} -> {now?.Length} B)");
        }

        Console.WriteLine(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    private static byte[] Media(byte[] bank, uint id)
    {
        var secs = BnkBuilder.ReadSections(bank);
        var e = BnkBuilder.ReadDidx(secs.Single(s => s.Tag == "DIDX").Body).FirstOrDefault(m => m.Id == id);
        return e is null ? null : secs.Single(s => s.Tag == "DATA").Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();
    }

    private static double Seconds(byte[] wem)
    {
        if (wem is null) return -1;
        var tmp = Path.Combine(Path.GetTempPath(), $"xzvc_{Guid.NewGuid():N}.wem");
        File.WriteAllBytes(tmp, wem);
        try
        {
            var pcm = AudioDecode.ToPcm(tmp);
            return pcm.Pcm.Length / (double)(pcm.SampleRate * pcm.Channels * (pcm.Bits / 8));
        }
        finally { File.Delete(tmp); }
    }
}
