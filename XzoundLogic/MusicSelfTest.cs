using System.IO;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

/// <summary>
/// The game's music has to be as reachable as its voice lines.
///
/// It was not. The row walk kept HierarchySoundSfxVoice objects and nothing else, and a
/// music bank builds its audio out of MusicTrack objects instead, so the one bank holding
/// the game's music showed a single unnamed row for 36.9 MB of it and there was no row to
/// stage a replacement onto. The names were missing for a second, unrelated reason: the
/// name table read one folder of Wwise assets and the music events are in a sibling of it.
///
/// Every number below is read out of the bank rather than written down here, because the
/// point is that the walk agrees with the bank and not that it agrees with a number
/// somebody typed while the paks were a patch older.
/// </summary>
public static class MusicSelfTest
{
    /// <summary>XzoundWave.exe --music</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });
        session.IndexBanks(_ => { });

        // ---- which banks hold music, asked of the objects ----------------------
        W("the banks that hold music");
        var started = DateTime.UtcNow;
        var musical = new List<(SkinEntry Skin, WwiseReader Reader)>();
        var scanned = 0;
        foreach (var group in session.OtherBanks)
            foreach (var skin in group.Skins)
            {
                var reader = Parse(skin.Banks[0]);
                if (reader?.Hierarchies is null) continue;
                scanned++;
                if (reader.Hierarchies.Any(h => h.Data is HierarchyMusicTrack))
                    musical.Add((skin, reader));
            }
        W($"    {scanned} bank(s) outside the characters read in " +
          $"{(DateTime.UtcNow - started).TotalSeconds:0.0}s");
        Check("the game ships music as MusicTrack objects, in at least one bank",
              musical.Count > 0,
              musical.Count == 0 ? "none found" : string.Join(", ", musical.Select(m => m.Skin.Label)));
        if (musical.Count == 0)
        {
            W("");
            W("1 CHECK(S) FAILED");
            return 1;
        }

        var (bankSkin, w) = musical[0];
        var bankFile = bankSkin.Banks[0];
        var nodes = new Dictionary<uint, AbstractHierarchy>();
        foreach (var h in w.Hierarchies) if (h.Data is not null) nodes[h.Data.Id] = h.Data;

        // What the bank itself says it contains, by two independent routes: the media
        // directory it ships bytes in, and the hierarchy that plays them.
        var didx = BnkBuilder.ReadDidx(
            BnkBuilder.ReadSections(bankFile.Read()).First(s => s.Tag == "DIDX").Body)
            .Select(e => e.Id).ToList();
        var fromTracks = w.Hierarchies.Select(h => h.Data).OfType<HierarchyMusicTrack>()
            .SelectMany(t => t.Sources ?? [])
            .Select(s => s.SourceId != 0 ? s.SourceId : s.FileId)
            .Where(id => id != 0).Distinct().ToList();
        var fromSounds = w.Hierarchies.Select(h => h.Data).OfType<HierarchySoundSfxVoice>()
            .Select(s => s.Source.SourceId != 0 ? s.Source.SourceId : s.Source.FileId)
            .Where(id => id != 0).Distinct().ToList();
        var expected = fromTracks.Concat(fromSounds).Distinct().ToList();

        W("");
        W($"{bankSkin.Label}: {didx.Count} embedded media, {fromTracks.Count} carried by " +
          $"{w.Hierarchies.Count(h => h.Data is HierarchyMusicTrack)} music track(s), " +
          $"{fromSounds.Count} by a Sound");

        // ---- every medium becomes a row ---------------------------------------
        W("");
        W("rows");
        var sounds = SoundIndex.Build(session, bankSkin, "", _ => { });
        var byId = sounds.Rows.GroupBy(r => r.MediaId).ToDictionary(g => g.Key, g => g.ToList());

        Check("every medium the bank embeds has a row",
              didx.All(byId.ContainsKey),
              $"{didx.Count(byId.ContainsKey)} of {didx.Count}");
        Check("every medium the hierarchy plays has a row",
              expected.All(byId.ContainsKey),
              $"{expected.Count(byId.ContainsKey)} of {expected.Count}");
        Check("and the rows are exactly those media", sounds.Rows.Count == expected.Count,
              $"{sounds.Rows.Count} row(s) for {expected.Count} medium/media");
        // One row per medium is the unit a swap lands on. This bank carries 327 of its ids
        // in more than one track and one of them in seventeen, so a row per occurrence
        // would claim a swap changes seventeen things.
        Check("one row per medium, not one per source occurrence",
              byId.Values.All(v => v.Count == 1),
              $"{byId.Values.Count(v => v.Count > 1)} medium/media with more than one row");
        Check("the walk finds more than the one Sound object it used to",
              sounds.Rows.Count > fromSounds.Count,
              $"{sounds.Rows.Count} now, {fromSounds.Count} before");

        // ---- the names are the game's own -------------------------------------
        W("");
        W("names");
        var named = sounds.Rows.Where(r => r.EventName is not null).ToList();
        Check("every row is named", named.Count == sounds.Rows.Count,
              $"{named.Count} of {sounds.Rows.Count}");
        // A name is only worth showing if it is the plaintext of the id the bank stores.
        // Hashing it back is the whole proof that nothing here was invented.
        var wrong = named.Where(r => WwiseFnv.GetHash(r.EventName) != r.EventHash).ToList();
        Check("each name hashes to the event id the bank stores", wrong.Count == 0,
              wrong.Count == 0 ? $"{named.Count} checked"
                               : $"{wrong[0].EventName} is not {wrong[0].EventHash}");

        // The event has to be one that really reaches this medium, not merely a name that
        // hashes. Walked again here, independently of the walk that built the rows.
        var mismatched = named.Count(r => !Reaches(r.EventHash, r.MediaId, nodes));
        Check("the event on a row really reaches that medium", mismatched == 0,
              $"{named.Count - mismatched} of {named.Count}");

        // Where those names come from. The point of the name table's scope: every one of
        // these is a shipped asset that the old, narrower scope could not see.
        var wanted = named.Select(r => r.EventName).Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assetOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in session.Provider.Files.Keys)
        {
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = Path.GetFileNameWithoutExtension(path);
            if (wanted.Contains(stem)) assetOf[stem] = path;
        }
        Check("each name is the stem of a shipped asset",
              wanted.All(assetOf.ContainsKey),
              $"{wanted.Count(assetOf.ContainsKey)} of {wanted.Count} distinct name(s)");
        Check("which the old WwiseEvents-only scope could not reach",
              assetOf.Values.All(p => p.IndexOf("/WwiseEvents/", StringComparison.OrdinalIgnoreCase) < 0),
              string.Join(", ", assetOf.Values.Take(1).Select(p => Path.GetDirectoryName(p))));
        W($"    {wanted.Count} distinct name(s) over {named.Count} row(s); " +
          $"e.g. {string.Join(", ", named.Take(3).Select(r => $"{r.Display} {r.Length}"))}");

        // ---- the lengths the bank states --------------------------------------
        W("");
        W("lengths");
        // Only what a playlist times. A music bank can also hold a plain Sound, and
        // nothing in the bank states that one's length, so it keeps the old behaviour of
        // being measured on demand rather than being given a figure from somewhere else.
        var timed = fromTracks.ToHashSet();
        var trackRows = sounds.Rows.Where(r => timed.Contains(r.MediaId)).ToList();
        Check("every clip a music track carries states how long it is",
              trackRows.Count > 0 && trackRows.All(r => r.Seconds is > 0),
              $"{trackRows.Count(r => r.Seconds is > 0)} of {trackRows.Count}");
        Check("and a row the bank does not time is left to be measured",
              sounds.Rows.Where(r => !timed.Contains(r.MediaId)).All(r => r.Seconds is null),
              $"{sounds.Rows.Count - trackRows.Count} row(s) the bank states nothing about");

        // The bank states each clip's length twice over: once in the playlist that places
        // it and once in the wem's own header. They agree, which is what makes the stated
        // figure usable as the row's length and as the thing a replacement is checked
        // against.
        var compared = 0; var disagreed = 0; var worst = 0.0;
        foreach (var id in didx)
        {
            if (!sounds.Embedded.TryGetValue(id, out var bytes)) continue;
            var decoded = BnkBuilder.Seconds(bytes.GetData());
            var stated = byId[id][0].Seconds;
            if (decoded is null || stated is null) continue;
            compared++;
            worst = Math.Max(worst, Math.Abs(decoded.Value - stated.Value));
            if (Math.Abs(decoded.Value - stated.Value) > MusicIndex.Tolerance) disagreed++;
        }
        Check("the stated length is the audio's own length", compared > 0 && disagreed == 0,
              $"{compared} clip(s) compared, {disagreed} disagreed, worst {worst * 1000:0.##} ms");
        Check("and the row says so where a swap would be decided",
              trackRows.All(r => r.AppliedLabel.Contains("timed")),
              trackRows.FirstOrDefault(r => !r.AppliedLabel.Contains("timed"))?.AppliedLabel
              ?? trackRows[0].AppliedLabel);

        // ---- where else the bank places the same medium ------------------------
        W("");
        W("the placements a row does not show");
        // A row is one row per medium and shows one track's numbers, so anything the other
        // tracks state differently is a figure the row is quietly overwriting. Counted here
        // rather than described, because the honest sentence the pane prints depends on
        // which of the two cases a row is in.
        var occurrences = w.Hierarchies.Select(h => h.Data).OfType<HierarchyMusicTrack>()
            .SelectMany(t => t.Sources ?? [])
            .Count(s => (s.SourceId != 0 ? s.SourceId : s.FileId) != 0);
        var multi = sounds.Rows.Where(r => r.Placements.Count > 1).ToList();
        var differ = multi.Where(r => r.PlacementsDiffer).ToList();
        // Only the rows a TRACK carries. This bank also holds one plain Sound, which sits
        // in no track and is placed nowhere, so counting it among the media placed once
        // would put the sum one over the occurrences and read as a lost placement.
        var once = sounds.Rows.Count(r => r.Placements.Count == 0 && timed.Contains(r.MediaId));
        W($"    {occurrences} source occurrence(s) for {sounds.Rows.Count} row(s); " +
          $"{multi.Count} row(s) are placed in more than one track, up to " +
          $"{(multi.Count == 0 ? 0 : multi.Max(r => r.Placements.Count))}");
        Check("a medium in several tracks keeps every one of them",
              multi.Count > 0 && multi.Sum(r => r.Placements.Count) + once == occurrences,
              $"{multi.Sum(r => r.Placements.Count)} kept plus {once} placed once, " +
              $"against {occurrences} occurrence(s)");
        Check("exactly one of them is the one the row's own columns describe",
              sounds.Rows.All(r => r.Placements.Count == 0 || r.Placements.Count(p => p.Shown) == 1),
              $"{sounds.Rows.Count(r => r.Placements.Count > 0 && r.Placements.Count(p => p.Shown) != 1)} row(s) wrong");
        // The figure that decides whether the pane may state a level as fact. On this bank
        // it is 140 of 655, so a pane that printed the row's level with nothing else to say
        // would be over-confident about better than a fifth of it.
        var pairs = differ.Select(r => r.Placements
            .Select(p => (p.Db, p.Driven, p.SegmentSeconds, p.Trimmed)).Distinct().Count()).ToList();
        W($"    {differ.Count} row(s) have a placement stating something else, " +
          $"in {(pairs.Count == 0 ? 0 : pairs.Max())} distinct combination(s) at worst");
        Check("and the disagreement is small enough to print in full",
              pairs.Count == 0 || pairs.Max() <= 16,
              $"worst {(pairs.Count == 0 ? 0 : pairs.Max())} combination(s)");
        // Measured, and it is what lets the clip's own duration stay on the main section
        // instead of being repeated once per placement.
        Check("no medium's tracks disagree about the clip's own length",
              multi.All(r => r.Placements.Select(p => p.Seconds).Distinct().Count() <= 1),
              $"{multi.Count(r => r.Placements.Select(p => p.Seconds).Distinct().Count() > 1)} row(s) disagree");

        // ---- a swap that changes the length is said out loud -------------------
        W("");
        W("a replacement the bank's timing no longer describes");
        var embedded = didx.Where(id => byId.ContainsKey(id) && sounds.Embedded.ContainsKey(id))
                           .Select(id => byId[id][0]).ToList();
        var donor = embedded.FirstOrDefault();
        var victim = embedded.FirstOrDefault(r =>
            donor is not null && MusicIndex.Disagrees(r.Seconds, donor.Seconds));
        if (donor is null || victim is null)
        {
            Check("two embedded clips of different stated lengths to swap between",
                  false, "none found");
        }
        else
        {
            // One clip's audio onto two slots: the slot that states that length, where
            // nothing is wrong, and a slot that states another, where the bank's timing is
            // now describing audio that is gone. Both in one build, because a warning that
            // fires on everything is as useless as one that never fires.
            var audio = sounds.Embedded[donor.MediaId].GetData();
            var pending = new List<PendingWem>
            {
                PendingWem.ForMedia(donor.MediaId, "same-length.wem", audio, donor.Display),
                PendingWem.ForMedia(victim.MediaId, "different-length.wem", audio, victim.Display),
            };
            W($"    {donor.MediaId} states {donor.Seconds:0.###} s and is given audio of " +
              $"{pending[0].Seconds:0.###} s; {victim.MediaId} states {victim.Seconds:0.###} s " +
              "and is given the same audio");

            var outDir = Path.Combine(Path.GetTempPath(), "xzw-musictest-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var built = ModBuilder.Build(session, bankSkin, pending, outDir,
                                             new HashSet<string> { bankFile.Path });
                Check("the build rebuilt the bank", built.Banks == 1, $"{built.Banks} bank(s)");
                Check("it counts the one clip whose length the bank contradicts",
                      built.Retimed == 1, $"{built.Retimed} reported");
                Check("and names it, with both lengths",
                      built.Log.Contains(victim.MediaId.ToString()) &&
                      built.Log.Contains($"{victim.Seconds:0.###}"),
                      "");
                Check("while saying nothing about the clip that kept its length",
                      !built.Log.Split('\n').Any(line =>
                          line.Contains("LENGTH") && line.Contains(donor.MediaId.ToString())));
                Check("and it is at the top of the log, not buried in it",
                      built.Log.IndexOf("LENGTH", StringComparison.Ordinal) <
                      built.Log.IndexOf("media " + victim.MediaId, StringComparison.Ordinal));
            }
            finally
            {
                try { Directory.Delete(outDir, true); } catch { }
            }
        }

        // ---- and nothing changed for a bank with no music in it ---------------
        W("");
        W("a bank that is not music");
        var plain = session.Characters.FirstOrDefault(c => c.CharId == "1030")?.Skins
                           .FirstOrDefault(s => s.SkinId == "1030001");
        if (plain is null) Check("a character skin to compare against", false, "1030001 not mounted");
        else
        {
            var rows = SoundIndex.Build(session, plain, "1030", _ => { });
            var stated = rows.Rows.Count(r => r.Applied?.ClipSeconds is not null);
            Check("no row in it is timed by its bank", stated == 0, $"{stated} of {rows.Rows.Count}");

            // The real statement of "unchanged": every row still comes from a Sound, so the
            // music walk contributed nothing here. It cannot: these banks hold no MusicTrack.
            var soundIds = new HashSet<uint>();
            var tracks = 0;
            foreach (var file in plain.Banks)
            {
                var reader = Parse(file);
                foreach (var h in reader?.Hierarchies ?? [])
                {
                    if (h.Data is HierarchyMusicTrack) tracks++;
                    if (h.Data is not HierarchySoundSfxVoice s) continue;
                    soundIds.Add(s.Source.SourceId != 0 ? s.Source.SourceId : s.Source.FileId);
                }
            }
            Check("its banks hold no music tracks at all", tracks == 0, $"{tracks} found");
            var stray = rows.Rows.Count(r => !soundIds.Contains(r.MediaId));
            Check("every one of its rows still comes from a Sound object", stray == 0,
                  $"{rows.Rows.Count - stray} of {rows.Rows.Count}");
        }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    private static WwiseReader Parse(CUE4Parse.FileProvider.Objects.GameFile file)
    {
        try
        {
            using var ar = file.CreateReader();
            return new WwiseReader(new FWwiseArchive(ar), new WwiseGameFileSource(file));
        }
        catch { return null; }   // one unreadable bank must not lose the rest
    }

    /// <summary>
    /// Does this event reach that medium? Walked here from the event down, deliberately
    /// duplicating what the index did, so a row's name is checked against the bank rather
    /// than against the code that wrote the row.
    /// </summary>
    private static bool Reaches(uint eventId, uint mediaId,
                               IReadOnlyDictionary<uint, AbstractHierarchy> nodes)
    {
        if (eventId == 0) return false;
        if (nodes.GetValueOrDefault(eventId) is not HierarchyEvent ev) return false;
        foreach (var actionId in ev.EventActionIds ?? [])
        {
            if (nodes.GetValueOrDefault(actionId) is not HierarchyEventAction act) continue;
            if (Below(act.ReferencedId, nodes, []).Contains(mediaId)) return true;
        }
        return false;
    }

    private static IEnumerable<uint> Below(uint id, IReadOnlyDictionary<uint, AbstractHierarchy> nodes,
                                          HashSet<uint> seen)
    {
        if (!seen.Add(id) || !nodes.TryGetValue(id, out var node)) yield break;
        switch (node)
        {
            case HierarchySoundSfxVoice s:
                yield return s.Source.SourceId != 0 ? s.Source.SourceId : s.Source.FileId;
                break;
            case HierarchyMusicTrack t:
                foreach (var s in t.Sources ?? [])
                    yield return s.SourceId != 0 ? s.SourceId : s.FileId;
                break;
            case HierarchyMusicSegment seg:
                foreach (var c in seg.ChildIds ?? [])
                    foreach (var x in Below(c, nodes, seen)) yield return x;
                break;
            case HierarchyMusicRandomSequenceContainer ranseq:
                foreach (var c in ranseq.ChildIds ?? [])
                    foreach (var x in Below(c, nodes, seen)) yield return x;
                break;
            case HierarchyMusicSwitchContainer sw:
                foreach (var c in sw.ChildIds ?? [])
                    foreach (var x in Below(c, nodes, seen)) yield return x;
                break;
            case HierarchyRandomSequenceContainer r:
                foreach (var c in r.ChildIds ?? [])
                    foreach (var x in Below(c, nodes, seen)) yield return x;
                break;
            case HierarchySwitchContainer sw2:
                foreach (var c in sw2.ChildIds ?? [])
                    foreach (var x in Below(c, nodes, seen)) yield return x;
                break;
            case HierarchyLayerContainer l:
                foreach (var c in l.ChildIds ?? [])
                    foreach (var x in Below(c, nodes, seen)) yield return x;
                break;
            case HierarchyActorMixer m:
                foreach (var c in m.ChildIds ?? [])
                    foreach (var x in Below(c, nodes, seen)) yield return x;
                break;
        }
    }
}
