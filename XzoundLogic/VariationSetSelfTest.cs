using System.IO;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.UE4.Wwise.Objects;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

/// <summary>
/// A variation set is what its CONTAINER holds, checked against the .bnk and never
/// against the index's own account of itself.
///
/// The bug this exists for could not be seen from the rows. The index keeps one row per
/// bank, event and medium, and this game's banks build one set of media into container
/// after container through Sound objects of their own each time, so the rows scatter
/// across whichever container each medium was met in first. Container 183834482 of
/// bnk_sfx_1022001 holds media 673786398, 53978063 and 634398524; only the first comes
/// back with a row naming it, the other two naming container 479715779. Anything that
/// rebuilt the set by gathering rows on the group id therefore found one member of three
/// and had no way to notice, because the rows it gathered agreed with each other
/// perfectly. 47 sets across five skins were short that way and every one of them was
/// self-consistent.
///
/// So the hierarchy is walked again here, by this file's own code rather than by the
/// walk under test, and the answer the index gives is held against it.
/// </summary>
public static class VariationSetSelfTest
{
    /// <summary>XzoundWave.exe --variations</summary>
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

        // Three skins rather than one, because the shapes a slot can take are spread
        // across them: 1022001 is where one set of media is built into eighteen
        // containers, 1034001 is where a slot's child is a layer container of two sounds
        // instead of a sound, and 1030001 is the plain case the others are read against.
        string[] skins = ["1022001", "1034001", "1030001"];

        var shortfalls = 0;
        var containersChecked = 0;
        var slotsChecked = 0;

        foreach (var skinId in skins)
        {
            var ch = session.Characters.FirstOrDefault(c => c.CharId == skinId[..4]);
            var skin = ch?.Skins.FirstOrDefault(s => s.SkinId == skinId);
            if (skin is null) { W($"no skin {skinId}"); return 1; }

            W("");
            W($"skin {skinId}");
            var sounds = SoundIndex.Build(session, skin, skinId[..4], _ => { });

            // The bank's own answer, read here. Deliberately not WwiseProps.SoundsUnder:
            // a check that calls the walk it is checking can only prove the walk agrees
            // with itself, which is the failure this whole file exists for.
            var stated = new Dictionary<(string Bank, uint Container), List<uint[]>>();
            foreach (var bankFile in skin.Banks)
            {
                var bankName = Path.GetFileNameWithoutExtension(bankFile.Path);
                WwiseReader w;
                try
                {
                    using var ar = bankFile.CreateReader();
                    w = new WwiseReader(new FWwiseArchive(ar), new WwiseGameFileSource(bankFile));
                }
                catch { continue; }
                if (w.Hierarchies is null) continue;

                var nodes = new Dictionary<uint, AbstractHierarchy>();
                foreach (var h in w.Hierarchies) if (h.Data is not null) nodes[h.Data.Id] = h.Data;

                // Media at or below one object. The four kinds below are the ones that
                // carry children; anything else ends the descent.
                List<uint> Under(uint id, HashSet<uint> seen)
                {
                    var found = new List<uint>();
                    if (!seen.Add(id) || !nodes.TryGetValue(id, out var n)) return found;
                    if (n is HierarchySoundSfxVoice s)
                    {
                        var m = s.Source.SourceId != 0 ? s.Source.SourceId : s.Source.FileId;
                        if (m != 0) found.Add(m);
                        return found;
                    }
                    var kids = n switch
                    {
                        HierarchyRandomSequenceContainer r => r.ChildIds,
                        HierarchySwitchContainer sw => sw.ChildIds,
                        HierarchyLayerContainer l => l.ChildIds,
                        HierarchyActorMixer a => a.ChildIds,
                        _ => null,
                    };
                    foreach (var k in kids ?? []) found.AddRange(Under(k, seen));
                    return found;
                }

                foreach (var c in nodes.Values.OfType<HierarchyRandomSequenceContainer>())
                {
                    if (c.ChildIds is null || c.ChildIds.Length <= 1) continue;
                    stated[(bankName, c.Id)] =
                        [.. c.ChildIds.Select(k => Under(k, []).Distinct().ToArray())];
                }
            }
            W($"    {stated.Count} containers of more than one child, read from the .bnk");

            var grouped = sounds.Rows.Where(r => r.Applied?.Alternative is { Count: > 1 }).ToList();
            var sets = grouped.Select(r => (r.Bank, r.Applied.Alternative.GroupId)).Distinct().ToList();

            // ---- what the index says a set holds, against what the container holds ----
            var wrongCount = 0; var wrongMedia = 0; var missingContainer = 0; var notInOwnSlot = 0;
            var readHere = 0; var slotsHere = 0;
            foreach (var row in grouped)
            {
                var v = row.Applied.Alternative;
                if (!stated.TryGetValue((row.Bank, v.GroupId), out var slots))
                { missingContainer++; continue; }

                readHere++;
                if (v.Members.Count != slots.Count) { wrongCount++; continue; }
                for (var at = 0; at < slots.Count; at++)
                {
                    slotsHere++;
                    if (v.Members[at].Index != at ||
                        !v.Members[at].Media.SequenceEqual(slots[at])) wrongMedia++;
                }
                // The slot the row says it sits in has to be a slot holding the row's own
                // medium, or the number beside "this one" names somebody else's sound.
                if (v.Index >= slots.Count || !slots[v.Index].Contains(row.MediaId)) notInOwnSlot++;
            }
            containersChecked += readHere;
            slotsChecked += slotsHere;
            Check("every set names a container that is in the bank", missingContainer == 0,
                  $"{missingContainer} of {grouped.Count} rows");
            Check("a set holds as many slots as the container has children", wrongCount == 0,
                  $"{wrongCount} of {readHere} read");
            Check("and each slot holds the media the bank puts under that child",
                  wrongMedia == 0, $"{wrongMedia} of {slotsHere} slots wrong");
            Check("a row sits in a slot that holds its own medium", notInOwnSlot == 0,
                  $"{notInOwnSlot} of {grouped.Count} rows");

            // ---- the defect itself, stated as the gap between the two ways of asking ----
            //
            // Gathering rows on the group id, which is what the window used to do. Where
            // this is short of the container the old list drew fewer boxes than the set
            // has members and then said so in a sentence that was wrong about which ones.
            var worstShort = 0u; var worstBank = ""; var worstBy = 0;
            var shortHere = 0;
            foreach (var (bank, id) in sets)
            {
                if (!stated.TryGetValue((bank, id), out var slots)) continue;
                var byRows = grouped.Where(r => r.Bank == bank && r.Applied.Alternative.GroupId == id)
                                    .Select(r => r.Applied.Alternative.Index).Distinct().Count();
                if (byRows >= slots.Count) continue;
                shortHere++;
                if (slots.Count - byRows <= worstBy) continue;
                worstBy = slots.Count - byRows; worstShort = id; worstBank = bank;
            }
            shortfalls += shortHere;
            W($"    {shortHere} of {sets.Count} sets are short when gathered from rows" +
              (worstShort == 0 ? "" : $", worst {worstBank} container {worstShort}: " +
                                      $"{stated[(worstBank, worstShort)].Count - worstBy} row " +
                                      $"slot(s) of {stated[(worstBank, worstShort)].Count}"));

            // Every one of those has to be whole when read off the container instead,
            // which is the repair. Checked against the .bnk figure and not against the
            // rows, so this cannot pass by both sides making the same mistake.
            var stillShort = sets.Count(s => stated.TryGetValue(s, out var slots) &&
                grouped.First(r => r.Bank == s.Bank && r.Applied.Alternative.GroupId == s.GroupId)
                       .Applied.Alternative.Members.Count != slots.Count);
            Check("reading the container instead leaves none of them short", stillShort == 0,
                  $"{stillShort} of {sets.Count}");

            // ---- no member is dropped, whichever container a row happens to name ----
            //
            // A medium's row carries the id of the container it was met in first, so the
            // OTHER containers holding that medium must still state it. This is the part
            // that fails if a set is ever rebuilt from rows again.
            var dropped = 0;
            foreach (var ((bank, id), slots) in stated)
            {
                var row = grouped.FirstOrDefault(r => r.Bank == bank &&
                                                      r.Applied.Alternative.GroupId == id);
                if (row is null) continue;
                var says = row.Applied.Alternative.Members.SelectMany(m => m.Media).ToHashSet();
                if (slots.SelectMany(s => s).Any(m => !says.Contains(m))) dropped++;
            }
            Check("a set states every medium its container reaches", dropped == 0,
                  $"{dropped} of {sets.Count} sets drop one");

            // ---- the shares are the slots', and they add up ----
            var wrongShare = grouped.Count(r =>
                r.Applied.Alternative.Members[r.Applied.Alternative.Index].Share
                != r.Applied.Alternative.Share);
            Check("a slot's share is worded as the row in it words its own",
                  wrongShare == 0, $"{wrongShare} of {grouped.Count} rows");

            // ---- the list the tooltip prints is the set, and marks one line ----
            var wrongLength = grouped.Count(r => r.Siblings.Count != r.Applied.Alternative.Count);
            var wrongMark = grouped.Count(r => r.Siblings.Count(l => l.StartsWith("> ")) != 1);
            Check("the siblings list is one line per slot the container has",
                  wrongLength == 0, $"{wrongLength} of {grouped.Count} rows");
            Check("and marks the reader's own line exactly once", wrongMark == 0,
                  $"{wrongMark} of {grouped.Count} rows");
        }

        // The checks above pass trivially on a game whose banks never reuse a set of media,
        // so this says out loud that the shape the repair is for was actually met. Without
        // it a mount that read no bank at all would print ALL CHECKS PASSED.
        W("");
        Check("the shape this checks for is present in the banks read", shortfalls > 0,
              $"{shortfalls} set(s) would be short if gathered from rows");
        Check("something was checked against the .bnk at all", slotsChecked > 0,
              $"{slotsChecked} slots over {containersChecked} rows' containers");

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
