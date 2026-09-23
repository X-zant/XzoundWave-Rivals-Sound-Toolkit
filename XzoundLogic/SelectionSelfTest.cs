using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MRAudioKit;

/// <summary>
/// Checks the two selection-scoped builds without a window: number (or silence) a
/// handful of sounds and prove that everything NOT selected comes through byte for
/// byte. That property is the whole point of working on a selection -- if untouched
/// sounds drift, the build is no longer usable in a real match.
/// </summary>
public static class SelectionSelfTest
{
    /// <summary>XzoundWave.exe --seltest &lt;skinId&gt; &lt;outFolder&gt; [count]</summary>
    public static int Run(string skinId, string outRoot, int count)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        var charId = skinId[..4];
        var skin = session.Characters.First(c => c.CharId == charId).Skins.First(s => s.SkinId == skinId);
        var sounds = SoundIndex.Build(session, skin, charId, _ => { });

        // Pick from ONE bank so the untouched check has a clear subject.
        var bankPath = skin.Banks.Select(b => b.Path)
            .FirstOrDefault(p => Path.GetFileName(p).Contains("sfx", StringComparison.OrdinalIgnoreCase))
            ?? skin.Banks[0].Path;
        var orig = session.Provider.Files[bankPath].Read();
        var entries = Entries(orig);
        W($"bank: {Path.GetFileName(bankPath)}  {entries.Count} media, {orig.Length:N0} B");

        var chosen = entries.Take(count).Select(e => e.Id).ToHashSet();
        W($"selecting {chosen.Count}: {string.Join(", ", chosen)}");

        var failures = 0;

        // ---- silent build ---------------------------------------------------
        var silentDir = Path.Combine(outRoot, "silent");
        if (Directory.Exists(silentDir)) Directory.Delete(silentDir, true);
        var sr = SilentBankBuilder.Build(session, [bankPath], chosen, silentDir, sounds);
        W("");
        W($"silent: {sr.Silenced} silenced, {sr.Banks} bank(s), {sr.NotFound} not found");
        failures += Verify(W, "silent", orig, entries, chosen,
                           Path.Combine(silentDir, bankPath.Replace('/', Path.DirectorySeparatorChar)),
                           expectSilent: true);

        // ---- numbered build on the same selection ---------------------------
        var clipDir = Path.Combine(outRoot, "clips");
        if (!Directory.Exists(clipDir) || TestBankBuilder.ScanFolder(clipDir).ByNumber.Count < chosen.Count)
        {
            Directory.CreateDirectory(clipDir);
            Tts.GenerateNumbers(clipDir, 0, chosen.Count - 1, _ => { });
        }
        var wems = TestBankBuilder.ScanFolder(clipDir);
        var testDir = Path.Combine(outRoot, "test");
        if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
        var tr = TestBankBuilder.Build(session, [bankPath], wems, testDir,
                                       wems.ByNumber.Keys.Min(), false, sounds, false, 0, chosen);
        W("");
        W($"test: {tr.Numbered} numbered, {tr.Silenced} silenced, {tr.Banks} bank(s)");
        failures += Verify(W, "test", orig, entries, chosen,
                           Path.Combine(testDir, bankPath.Replace('/', Path.DirectorySeparatorChar)),
                           expectSilent: false);

        // ---- round two: mute one, rebuild ----------------------------------
        // The workflow this exists for: several sounds fire together, you make out
        // one number, you mute it so the others can be heard. That only works if the
        // rebuild keeps every other number exactly where it was.
        var round1 = tr.Legend.ToDictionary(x => x.MediaId, x => x.Number);
        var victim = tr.Legend.OrderBy(x => x.Number).First().MediaId;
        var keep = round1;
        var muteSet = new HashSet<uint> { victim };

        var testDir2 = Path.Combine(outRoot, "test2");
        if (Directory.Exists(testDir2)) Directory.Delete(testDir2, true);
        var tr2 = TestBankBuilder.Build(session, [bankPath], wems, testDir2,
                                        wems.ByNumber.Keys.Min(), false, sounds, false, 0,
                                        chosen, muteSet, keep);
        W("");
        W($"round 2 (muting #{round1[victim]}, media {victim}): " +
          $"{tr2.Numbered} numbered, {tr2.Silenced} muted");
        failures += VerifyRound2(W, orig, entries, chosen, round1, victim,
                                 Path.Combine(testDir, bankPath.Replace('/', Path.DirectorySeparatorChar)),
                                 Path.Combine(testDir2, bankPath.Replace('/', Path.DirectorySeparatorChar)),
                                 tr2);

        // ---- round three: number the selection, silence the whole rest ------
        // "Highlight a group, test only that, mute everything else." Every entry in
        // the bank is touched here, so the check is the mirror of round one: the
        // chosen ones must speak, and nothing else may.
        var others = entries.Select(e => e.Id).Where(id => !chosen.Contains(id)).ToHashSet();
        var onlyDir = Path.Combine(outRoot, "only");
        if (Directory.Exists(onlyDir)) Directory.Delete(onlyDir, true);
        var tr3 = TestBankBuilder.Build(session, [bankPath], wems, onlyDir,
                                        wems.ByNumber.Keys.Min(), false, sounds, false, 0,
                                        null, others, round1);
        W("");
        W($"round 3 (only the selection audible): {tr3.Numbered} numbered, {tr3.Silenced} silenced");

        var builtPath = Path.Combine(onlyDir, bankPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(builtPath)) { W("  [only] no bank written -> FAIL"); failures++; }
        else
        {
            var built = File.ReadAllBytes(builtPath);
            var bData = Body(built, "DATA");
            int speak = 0, quiet = 0, wrong = 0;
            foreach (var e in Entries(built))
            {
                var bytes = bData.AsSpan((int)e.Offset, (int)e.Size).ToArray();
                var silent = IsSilentVorbis(bytes);
                if (chosen.Contains(e.Id))
                {
                    if (silent) { wrong++; } else speak++;
                }
                else
                {
                    if (silent) quiet++; else wrong++;
                }
            }
            W($"  [only] {speak} of {chosen.Count} selected audible, {quiet} of {others.Count} others silent");
            if (wrong > 0) { W($"  [only] {wrong} entr(y/ies) on the wrong side -> FAIL"); failures++; }
        }

        W("");
        W(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static int Verify(Action<string> W, string label, byte[] orig,
                              List<BnkBuilder.MediaEntry> origEntries, HashSet<uint> chosen,
                              string builtPath, bool expectSilent)
    {
        if (!File.Exists(builtPath)) { W($"  [{label}] no bank written -> FAIL"); return 1; }
        var built = File.ReadAllBytes(builtPath);
        var fails = 0;

        var origData = Body(orig, "DATA");
        var builtData = Body(built, "DATA");
        var builtEntries = Entries(built);

        if (builtEntries.Count != origEntries.Count)
        { W($"  [{label}] media count {builtEntries.Count} != {origEntries.Count} -> FAIL"); fails++; }

        // Non-media sections must be untouched: the event graph is not ours to edit.
        foreach (var tag in new[] { "BKHD", "HIRC", "STID" })
        {
            var a = Body(orig, tag); var b = Body(built, tag);
            if (a is null && b is null) continue;
            if (a is null || b is null || !a.AsSpan().SequenceEqual(b))
            { W($"  [{label}] section {tag} changed -> FAIL"); fails++; }
        }

        var byId = builtEntries.ToDictionary(e => e.Id);
        int untouched = 0, replaced = 0, silentOk = 0;
        foreach (var e in origEntries)
        {
            if (!byId.TryGetValue(e.Id, out var b))
            { W($"  [{label}] media {e.Id} missing -> FAIL"); fails++; continue; }

            var before = origData.AsSpan((int)e.Offset, (int)e.Size);
            var after = builtData.AsSpan((int)b.Offset, (int)b.Size);

            if (!chosen.Contains(e.Id))
            {
                if (!before.SequenceEqual(after))
                { W($"  [{label}] UNSELECTED media {e.Id} changed -> FAIL"); fails++; }
                else untouched++;
            }
            else
            {
                if (before.SequenceEqual(after))
                { W($"  [{label}] selected media {e.Id} was NOT replaced -> FAIL"); fails++; }
                else replaced++;
                if (expectSilent && IsSilentVorbis(after.ToArray())) silentOk++;
            }
        }

        W($"  [{label}] {untouched} unselected byte-identical, {replaced} replaced" +
          (expectSilent ? $", {silentOk} of {replaced} confirmed silent" : ""));
        if (expectSilent && silentOk != replaced)
        { W($"  [{label}] some replacements are not silence -> FAIL"); fails++; }
        return fails;
    }


    /// <summary>
    /// A rebuild with one sound muted must change exactly one thing. Everything else
    /// keeping its bytes is what keeps the author's notes from round one valid.
    /// </summary>
    private static int VerifyRound2(Action<string> W, byte[] orig,
                                    List<BnkBuilder.MediaEntry> origEntries, HashSet<uint> chosen,
                                    Dictionary<uint, int> round1, uint victim,
                                    string firstPath, string secondPath,
                                    TestBankBuilder.Result r2)
    {
        var fails = 0;
        var a = File.ReadAllBytes(firstPath);
        var b = File.ReadAllBytes(secondPath);
        var aData = Body(a, "DATA"); var bData = Body(b, "DATA");
        var aById = Entries(a).ToDictionary(e => e.Id);
        var bById = Entries(b).ToDictionary(e => e.Id);
        var origData = Body(orig, "DATA");
        var origById = origEntries.ToDictionary(e => e.Id);

        int same = 0, changed = 0;
        foreach (var id in aById.Keys)
        {
            var ea = aById[id];
            if (!bById.TryGetValue(id, out var eb))
            { W($"  [round2] media {id} vanished -> FAIL"); fails++; continue; }

            var sa = aData.AsSpan((int)ea.Offset, (int)ea.Size);
            var sb = bData.AsSpan((int)eb.Offset, (int)eb.Size);
            var identical = sa.SequenceEqual(sb);

            if (id == victim)
            {
                if (identical) { W("  [round2] the muted sound did not change -> FAIL"); fails++; }
                else if (!IsSilentVorbis(sb.ToArray()))
                { W("  [round2] the muted sound is not silence -> FAIL"); fails++; }
                changed++;
            }
            else if (!identical)
            {
                W($"  [round2] media {id} changed but was not muted -> FAIL"); fails++;
            }
            else same++;
        }

        // Unselected entries must still match the SHIPPED bytes, not just round one.
        var drifted = origEntries.Count(e => !chosen.Contains(e.Id) &&
            !origData.AsSpan((int)e.Offset, (int)e.Size)
                     .SequenceEqual(bData.AsSpan((int)bById[e.Id].Offset, (int)bById[e.Id].Size)));
        if (drifted > 0) { W($"  [round2] {drifted} unselected entries drifted -> FAIL"); fails++; }

        // Numbers must not move, and the muted one must still be findable.
        var moved = r2.Legend.Where(x => round1.TryGetValue(x.MediaId, out var was) && was != x.Number).ToList();
        if (moved.Count > 0)
        { W($"  [round2] {moved.Count} sound(s) were renumbered -> FAIL"); fails++; }

        var victimRow = r2.Legend.FirstOrDefault(x => x.MediaId == victim);
        if (victimRow is null || victimRow.Number != round1[victim])
        { W("  [round2] the muted sound lost its number in the legend -> FAIL"); fails++; }

        W($"  [round2] {same} clips byte-identical to round 1, {changed} changed, " +
          $"{drifted} unselected drifted, {moved.Count} renumbered, " +
          $"muted still listed as #{victimRow?.Number}");
        return fails;
    }

    /// <summary>
    /// A Vorbis packet holding pure digital silence is one byte, so uMaxPacketSize
    /// separates silence from audio outright: measured across these banks it is 1 for
    /// silence and 139-151 for a spoken clip. Bytes-per-second does NOT work -- on a
    /// clip of 0.05 s the header and setup packet dominate the file and a silent clip
    /// looks like a loud one.
    /// </summary>
    private static bool IsSilentVorbis(byte[] wem)
    {
        var p = 12; ushort tag = 0; byte[] ext = null; var rate = 0;
        while (p + 8 <= wem.Length)
        {
            var id = Encoding.ASCII.GetString(wem, p, 4);
            var sz = (int)BinaryPrimitives.ReadUInt32LittleEndian(wem.AsSpan(p + 4, 4));
            if (sz < 0 || p + 8 + sz > wem.Length) break;
            if (id == "fmt ")
            {
                tag = BinaryPrimitives.ReadUInt16LittleEndian(wem.AsSpan(p + 8, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wem.AsSpan(p + 12, 4));
                if (sz >= 18 + 48) ext = wem.AsSpan(p + 8 + 18, 48).ToArray();
            }
            p += 8 + sz + (sz & 1);
        }
        if (tag != 0xFFFF || ext is null || rate == 0) return false;
        return BinaryPrimitives.ReadUInt16LittleEndian(ext.AsSpan(0x1e, 2)) <= 4;
    }

    // ---- bank reading ------------------------------------------------------

    private static byte[] Body(byte[] bnk, string tag) =>
        BnkBuilder.ReadSections(bnk).FirstOrDefault(s => s.Tag == tag)?.Body;

    private static List<BnkBuilder.MediaEntry> Entries(byte[] bnk)
    {
        var didx = Body(bnk, "DIDX");
        return didx is null ? [] : BnkBuilder.ReadDidx(didx);
    }
}
