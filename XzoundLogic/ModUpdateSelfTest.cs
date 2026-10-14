using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MRAudioKit;

/// <summary>
/// An old mod's audio lands on the game's current bank, and only where it should.
///
/// The two banks below differ the way a real mod and the patch after it were measured to:
/// events renamed under the mod, audio swapped in place on ids the game kept, Sounds the
/// game moved onto new media so the mod's audio now sits on an id nothing plays, and media
/// the game shares between Sounds the mod only partly touched. The last is the one that
/// matters most and is easiest to get wrong without noticing, because a swap there changes
/// sounds the mod never touched and nothing in the output looks broken. A second pair holds
/// the cases where the game gave a line's media id to another line, or removed or reshaped
/// the Sound a line was written for, which is where a line can land on the wrong slot; and
/// the placement audit is shown to catch exactly that when it is done on purpose.
///
/// Everything here is synthetic, so it runs with no game mounted and no mod on disk. The
/// banks are written the way version 145 writes them, so the hierarchy parser reads them as
/// it reads the game's.
/// </summary>
public static class ModUpdateSelfTest
{
    private const string GamePath = "Marvel/Content/WwiseAudio/English(US)/bnk_test_update.bnk";
    private const string Media700 = "Marvel/Content/WwiseAudio/Media/English(US)/12/700.wem";
    private const string Media750 = "Marvel/Content/WwiseAudio/Media/English(US)/34/750.wem";
    private const string Media760 = "Marvel/Content/WwiseAudio/Media/English(US)/56/760.wem";

    /// <summary>XzoundWave.exe --updatetest</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        // The game streams 750 and cuts its opening into the bank. The current bank holds
        // the first 80 bytes and the mod's copy is the first 60 of the same file: different
        // bytes, and still the game's own audio.
        var file750 = Wem(0x75, 200);
        var mod = Bank(
            events: [5000, 5001, 5002],
            media:
            [
                (100, Wem(0xA1)), (200, Wem(0x20)), (400, Wem(0x40)), (800, Wem(0xA8)),
                (750, file750[..60]), (760, Wem(0xA7)),
                (900, Wem(0x90)), (901, Wem(0x91)), (902, Wem(0x92)), (903, Wem(0x93)),
                (904, Wem(0x94)), (905, Wem(0x95)), (906, Wem(0x96)), (907, Wem(0x97)),
                (908, Wem(0x98)), (909, Wem(0x99)), (910, Wem(0x63)),
            ],
            sounds:
            [
                (1000, 100), (1100, 200),
                (1001, 900), (1002, 900),     // both repointed to one mod medium: carries onto 300
                (1003, 901), (1004, 400),     // only one of 400's two Sounds repointed
                (1005, 902),                  // 410's other Sound is one the game added since, so it carries
                (1007, 903), (1008, 904),     // 420's two Sounds point at two different mod media
                (1009, 905),                  // the game now streams 700 instead of embedding it
                (1010, 906),                  // repointed onto 800, where 1020 has an in-place change of its own
                (1020, 800),
                (1011, 908),                  // a Sound the current bank no longer has
                (1013, 909),                  // 710 is neither embedded nor streamed
                (1014, 750), (1015, 760),
                (1017, 200),                  // pointed at one of the game's own media, not new audio
                (1018, 910),                  // the game renamed 910 to 630 and kept the audio
                // 907: no Sound plays it at all
            ]);
        var current = Bank(
            events: [5000, 6001, 6002, 6003],
            media:
            [
                (100, Wem(0x10)), (200, Wem(0x20)), (300, Wem(0x30)), (400, Wem(0x40)),
                (410, Wem(0x41)), (420, Wem(0x42)), (600, Wem(0x60)), (620, Wem(0x62)),
                (630, Wem(0x63)), (750, file750[..80]), (800, Wem(0x80)),
            ],
            sounds:
            [
                (1000, 100), (1100, 200), (1001, 300), (1002, 300), (1003, 400), (1004, 400),
                (1005, 410), (1006, 410), (1007, 420), (1008, 420), (1009, 700), (1010, 800),
                (1012, 600), (1013, 710), (1014, 750), (1015, 760), (1017, 620),
                // 630 is shared with a Sound the mod never had, which would take the mod's
                // audio as well if the mod's audio for 1018 were anything but the game's own.
                (1018, 630), (1019, 630),
                (1020, 800),                  // 800 is the line of a Sound both banks have
            ]);

        var loose = new Dictionary<uint, (string Path, byte[] Bytes)>
        {
            [700] = (Media700, Wem(0x70)),
            [750] = (Media750, file750),
            [760] = (Media760, Wem(0x76)),
        };
        ModUpdate.Streamed StreamedAt(uint id) =>
            loose.TryGetValue(id, out var f) ? new ModUpdate.Streamed(f.Path, () => f.Bytes) : null;

        W("reading a bank");
        var shape = ModUpdate.Read(current);
        Check("events, media and Sounds are all read", shape.Events.Count == 4 && shape.Media.Count == 11 &&
              shape.SoundSource.Count == 20, $"{shape.Events.Count} / {shape.Media.Count} / {shape.SoundSource.Count}");
        Check("a Sound's medium is read from its body", shape.SoundSource.GetValueOrDefault(1009u) == 700);
        Check("the bank version is read", shape.Version == 145, shape.Version.ToString());

        W("");
        W("nothing changed, nothing carried");
        var same = ModUpdate.Rebase(current, current, StreamedAt);
        Check("a bank rebased onto itself carries nothing", same.Carried.Count == 0, $"{same.Carried.Count} carried");
        Check("and refuses nothing", same.Refused.Count == 0, string.Join("; ", same.Refused.Select(r => r.Kind)));

        var plan = ModUpdate.Rebase(mod, current, StreamedAt);
        ModUpdate.Carried Got(uint id) => plan.Carried.FirstOrDefault(c => c.Target == id);
        ModUpdate.Refused No(uint target, uint source = 0) =>
            plan.Refused.FirstOrDefault(r => (target == 0 || r.Target == target) && (source == 0 || r.Source == source));

        W("");
        W("in place");
        Check("audio changed on an id the game kept carries onto that id",
              Got(100) is { How: "in place", LoosePath: null } c100 && c100.Bytes[8] == 0xA1);
        Check("an id with the same bytes on both sides is not carried", Got(200) is null);
        Check("a copy that is the start of the game's streamed file is not a change",
              Got(750) is null && plan.Stock.Any(s => s.Target == 750 && s.Source == 750),
              string.Join(", ", plan.Stock.Select(s => s.Target)));
        Check("a medium the game moved out of the bank follows it to its loose file",
              Got(760) is { LoosePath: Media760 } c760 && c760.Bytes[8] == 0xA7,
              Got(760)?.LoosePath ?? "(not carried)");

        W("");
        W("repointed");
        Check("a Sound moved onto a new medium takes the mod's audio there",
              Got(300) is { How: "repointed", Source: 900 } c300 && c300.Bytes[8] == 0x90,
              $"{Got(300)?.How} from {Got(300)?.Source}");
        Check("and it names both Sounds that play it", Got(300)?.Sounds.SequenceEqual([1001u, 1002u]) == true);
        Check("a medium the game now streams gets the mod's audio as a loose file at the game's path",
              Got(700) is { LoosePath: Media700, Source: 905 } c700 && c700.Bytes[8] == 0x95,
              Got(700)?.LoosePath ?? "(not carried)");
        Check("a medium the game only renamed, with the same audio, is not a change and not put to a person",
              Got(630) is null && No(630) is null && plan.Stock.Any(s => s.Target == 630 && s.Source == 910),
              No(630)?.Kind ?? "");
        // 410 is played today by 1005, which the mod repointed, and by 1006, which the mod's
        // bank does not have at all: the game added it after the mod was made. No swap can
        // change one without the other, and refusing would leave the author's line playing
        // nowhere, so it carries and 1006 is named as the sound the game added.
        Check("a medium shared with a Sound the mod's bank never had carries",
              Got(410) is { How: "repointed", Source: 902 } c410 && c410.Bytes[8] == 0x92 && No(410) is null,
              No(410)?.Why ?? $"{Got(410)?.How} from {Got(410)?.Source}");
        Check("and names that Sound as one the game added, apart from the one the mod repointed",
              Got(410) is { } a410 && a410.GameAdded.SequenceEqual([1006u]) && a410.Sounds.SequenceEqual([1005u, 1006u]),
              Got(410) is { } g410 ? string.Join(", ", g410.GameAdded) : "(not carried)");
        Check("a carry the mod's own Sounds alone play names no added Sound",
              Got(300)?.GameAdded.Count == 0 && Got(100)?.GameAdded.Count == 0);

        W("");
        W("refused, so nothing the mod did not touch changes");
        var r400 = No(400);
        Check("a medium another Sound also plays, one the mod's bank has and left on it, is NOT carried",
              Got(400) is null && r400?.Kind == "shared", r400?.Kind ?? "(no refusal)");
        Check("and the refusal names that Sound and both media",
              r400 is not null && r400.Why.Contains("1004") && r400.Source == 901, r400?.Why);
        Check("Sounds split across two mod media are NOT carried", Got(420) is null && No(420)?.Kind == "split",
              No(420)?.Kind);
        // 800 is played today by 1020, which kept it and whose line the mod changed in place,
        // and by 1010, which the mod played from its own 906. Either swap puts one of the
        // author's lines on the other's Sound, so neither is made.
        Check("two of the mod's lines landing on one medium are NOT carried, the in-place one included",
              Got(800) is null && No(800)?.Kind == "split" && No(800).Why.Contains("800, 906"), No(800)?.Why);
        Check("a medium neither embedded nor streamed is reported, not invented",
              Got(710) is null && No(710)?.Kind == "nowhere", No(710)?.Kind);
        Check("a Sound pointed at another of the game's own media is reported, not carried",
              Got(620) is null && No(620)?.Kind == "existing medium" && No(620)?.Source == 200, No(620)?.Kind);
        Check("mod media no Sound plays is reported as an orphan", No(0, 907)?.Kind == "orphan", No(0, 907)?.Kind);
        Check("mod media only a vanished Sound plays is reported as gone",
              No(0, 908) is { Kind: "gone" } g && g.Sounds.SequenceEqual([1011u]), No(0, 908)?.Kind);
        // Seven decisions, and the media a repoint already explained are not reported a
        // second time as orphans: 903 and 904 are named by one split, 800 and 906 by the other.
        Check("every refusal is made once",
              plan.Refused.Count == 7 && plan.Refused.Count(r => r.Kind is "orphan" or "gone") == 2,
              string.Join(", ", plan.Refused.Select(r => $"{r.Kind}:{r.Target}/{r.Source}")));

        W("");
        W("what the update gains");
        Check("events only in the mod are counted as renamed or removed", plan.EventsGone.SequenceEqual([5001u, 5002u]),
              string.Join(", ", plan.EventsGone));
        Check("events only in the current bank are what the old mod hid",
              plan.EventsGained.SequenceEqual([6001u, 6002u, 6003u]), string.Join(", ", plan.EventsGained));
        Check("and so are its media", plan.MediaGained.SequenceEqual([300u, 410u, 420u, 600u, 620u, 630u]),
              string.Join(", ", plan.MediaGained));

        W("");
        W("the bank that is written");
        var root = Path.Combine(Path.GetTempPath(), "XzoundWave", "updatetest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var written = ModUpdate.Write(plan, current, GamePath, root, taken);
            var bankFile = Path.Combine(root, GamePath.Replace('/', Path.DirectorySeparatorChar));
            Check("it lands at the game's path, language folder included", File.Exists(bankFile),
                  written.BankPath);
            var built = File.Exists(bankFile) ? File.ReadAllBytes(bankFile) : [];
            var after = ModUpdate.Read(built);
            Check("it embeds exactly the current bank's media, no more and no fewer",
                  after.Media.Keys.OrderBy(x => x).SequenceEqual(shape.Media.Keys.OrderBy(x => x)),
                  string.Join(", ", after.Media.Keys.OrderBy(x => x)));
            Check("the carried audio is in it", after.Media.GetValueOrDefault(100u)?[8] == 0xA1 &&
                  after.Media.GetValueOrDefault(300u)?[8] == 0x90 && after.Media.GetValueOrDefault(410u)?[8] == 0x92);
            Check("everything refused is still the game's own",
                  new uint[] { 200, 400, 420, 600, 620, 630, 750, 800 }.All(id =>
                      after.Media.GetValueOrDefault(id)?.AsSpan().SequenceEqual(shape.Media[id]) == true));
            Check("the hierarchy is the current bank's, byte for byte",
                  Section(built, "HIRC").AsSpan().SequenceEqual(Section(current, "HIRC")) &&
                  Section(built, "BKHD").AsSpan().SequenceEqual(Section(current, "BKHD")));
            Check("so it answers the current bank's events", after.Events.SetEquals(shape.Events));

            var l700 = Path.Combine(root, Media700.Replace('/', Path.DirectorySeparatorChar));
            var l760 = Path.Combine(root, Media760.Replace('/', Path.DirectorySeparatorChar));
            Check("the loose carries are written at the game's paths",
                  File.Exists(l700) && File.ReadAllBytes(l700)[8] == 0x95 &&
                  File.Exists(l760) && File.ReadAllBytes(l760)[8] == 0xA7);

            // Read back from disk, loose files included, and a Sound the game added beside a
            // carry (1006 on 410) is one the line shares its medium with, not a wrong slot.
            byte[] LooseOnDisk(string at)
            {
                var f = Path.Combine(root, at.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(f) ? File.ReadAllBytes(f) : null;
            }
            var audit = ModUpdate.AuditPlacement(plan, mod, current, built, LooseOnDisk);
            Check("the placement audit finds every carry, loose ones included, on the slot it was written for",
                  audit.Clean && audit.Lines == plan.Carried.Count && audit.Verified == audit.Lines,
                  audit.Line + " " + string.Join("; ", audit.Problems));

            // A loose file the mod ships itself is written first and wins its path.
            var again = ModUpdate.Rebase(mod, current, StreamedAt);
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Media700 };
            ModUpdate.Write(again, current, GamePath, Path.Combine(root, "second"), claimed);
            Check("a path the mod already ships a file at keeps the mod's file",
                  again.Carried.All(c => c.Target != 700) &&
                  again.Refused.Any(r => r.Target == 700 && r.Kind == "kept the mod's file"));

            var report = ModUpdate.Describe("bnk_test_update.bnk", GamePath, mod.Length, GamePath, current.Length,
                                            plan, written, null, null, id => loose.ContainsKey(id));
            Check("the report counts what was carried and what was not",
                  report.Contains($"CARRIED {plan.Carried.Count} (3 into the bank, 2 as loose files)") &&
                  report.Contains($"NOT CARRIED {plan.Refused.Count}"),
                  $"{plan.Carried.Count} carried, {plan.Refused.Count} not");
            Check("and says which carry also lands on a sound the game added, naming it",
                  report.Contains("carried onto a sound the game added since the mod was made, which plays the same line: Sound 1006") &&
                  report.Split("the game added since").Length == 2,
                  report.Split('\n').FirstOrDefault(l => l.Contains("the game added since"))?.Trim() ?? "(not said)");
            Check("and says how many of the game's events the old bank answered",
                  report.Contains("answers 1 of the 4 events"));
            Check("the read check counts embedded, loose and missing media",
                  report.Contains("20 of 20 Sounds in the current bank decode a medium") &&
                  report.Contains("17 play a medium the bank embeds (0 of them") &&
                  report.Contains("2 one the bank does not embed but the game has as a loose file, 1 neither"),
                  report.Split('\n').FirstOrDefault(l => l.Contains("of those")));
            Check("and lists what was not a change", report.Contains("NOT A CHANGE 2") &&
                  report.Contains("630 (mod media 910)"));
            Check("the report has no em-dash", !report.Contains((char)0x2014));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        W("");
        W("a bank whose every event was renamed");
        var renamedMod = Bank([1, 2, 3], [(100, Wem(0x10))], [(1000, 100)]);
        var renamedNow = Bank([11, 12, 13, 14], [(100, Wem(0x10))], [(1000, 100)]);
        var renamed = ModUpdate.Rebase(renamedMod, renamedNow, null);
        var said = ModUpdate.Describe("x.bnk", "x", renamedMod.Length, "x", renamedNow.Length, renamed,
                                      new ModUpdate.Written([], null, 0, 0), null, null, null);
        Check("is said to play nothing as shipped", said.Contains("answers 0 of the 4 events") &&
              said.Contains("plays nothing at all"));
        Check("and the update gains every event", renamed.EventsGained.Count == 4 && renamed.EventsGone.Count == 3);
        Check("with no audio to carry, no bank is written", said.Contains("wrote  no bank"));

        W("");
        W("audio the mod's bank built itself");
        // Wwise writes each Sound's declared in-memory size as exactly its medium's size, and
        // a hand swap leaves that field at the size the game's audio had. 2001 is swapped by
        // hand (declared 60, holds 120), 2002 was built by Wwise (declares its own 90), 2913
        // is the same under an id the game has since renamed to 2003, and 2004 is played by
        // two Sounds of which only one declares its size, so it is not Wwise's own doing.
        var builtMod = Bank([1],
            [(2001, Wem(0xC1, 120)), (2002, Wem(0xC2, 90)), (2913, Wem(0xC3, 80)), (2004, Wem(0xC4, 70))],
            [(3001, 2001), (3002, 2002), (3003, 2913), (3004, 2004), (3005, 2004)],
            declared: new() { [3001] = 60, [3002] = 90, [3003] = 80, [3004] = 70, [3005] = 60 });
        var builtNow = Bank([1],
            [(2001, Wem(0x51, 60)), (2002, Wem(0x52, 60)), (2003, Wem(0x53, 60)), (2004, Wem(0x54, 60))],
            [(3001, 2001), (3002, 2002), (3003, 2003), (3004, 2004), (3005, 2004)],
            declared: new() { [3001] = 60, [3002] = 60, [3003] = 60, [3004] = 60, [3005] = 60 });
        // The game install these banks stand for: an older container still ships every media id
        // above, as the base pak keeps the banks a patch replaced, but none of these very bytes.
        static ModUpdate.GameCopy Install(uint id, byte[] bytes) => new(id is 2001 or 2002 or 2003 or 2004 or 2913);
        var strict = ModUpdate.Rebase(builtMod, builtNow, null, shipped: Install);
        var kept = ModUpdate.Rebase(builtMod, builtNow, null, keepWwiseBuilt: true, shipped: Install);
        string Kinds(ModUpdate.Plan p) => string.Join(", ", p.Refused.Select(r => $"{r.Kind}:{r.Target}/{r.Source}")) +
                                          " | carried " + string.Join(", ", p.Carried.Select(c => c.Target));
        Check("a medium swapped by hand carries",
              strict.Carried.FirstOrDefault(c => c.Target == 2001) is { How: "in place", BuiltByWwise: false } h &&
              h.Bytes[8] == 0xC1, Kinds(strict));
        var r2002 = strict.Refused.FirstOrDefault(r => r.Target == 2002);
        Check("a medium the mod's bank built itself is NOT carried",
              strict.Carried.All(c => c.Target != 2002) && r2002?.Kind == ModUpdate.WwiseBuiltKind, Kinds(strict));
        Check("and the refusal says why in plain words, with its id, both sizes and the flag",
              r2002 is not null && r2002.Why.Contains("built this audio itself") &&
              r2002.Why.Contains("game's own older") && r2002.Why.Contains("undo a game update") &&
              r2002.Why.Contains("media 2002") && r2002.Why.Contains("mod's 90 B") &&
              r2002.Why.Contains("game's 60 B") && r2002.Why.Contains(ModUpdate.KeepWwiseBuiltFlag), r2002?.Why);
        var r2003 = strict.Refused.FirstOrDefault(r => r.Target == 2003);
        Check("a repointed medium the bank built itself is NOT carried either",
              strict.Carried.All(c => c.Target != 2003) && r2003 is { Kind: ModUpdate.WwiseBuiltKind, Source: 2913 },
              Kinds(strict));
        Check("a medium only some of whose Sounds declare its size still carries",
              strict.Carried.Any(c => c.Target == 2004 && c.Bytes[8] == 0xC4), Kinds(strict));
        Check("nothing else is refused", strict.Refused.Count == 2, Kinds(strict));
        Check($"{ModUpdate.KeepWwiseBuiltFlag} carries the in-place one, marked",
              kept.Carried.FirstOrDefault(c => c.Target == 2002) is { BuiltByWwise: true } k2 && k2.Bytes[8] == 0xC2,
              Kinds(kept));
        Check("and the repointed one, marked",
              kept.Carried.FirstOrDefault(c => c.Target == 2003) is { Source: 2913, BuiltByWwise: true } k3 &&
              k3.Bytes[8] == 0xC3, Kinds(kept));
        Check("and refuses nothing", kept.Refused.Count == 0 && kept.Carried.Count == 4, Kinds(kept));
        var none = new ModUpdate.Written([], null, 0, 0);
        var strictSaid = ModUpdate.Describe("x.bnk", "x", builtMod.Length, "x", builtNow.Length, strict, none, null, null, null);
        Check("the report counts these refusals and names the flag",
              strictSaid.Contains("NOT CARRIED 2, 2 of them because the mod's bank built that audio itself") &&
              strictSaid.Contains($"({ModUpdate.KeepWwiseBuiltFlag} carries it anyway)") &&
              strictSaid.Contains($"({ModUpdate.WwiseBuiltKind})"));
        var keptSaid = ModUpdate.Describe("x.bnk", "x", builtMod.Length, "x", builtNow.Length, kept, none, null, null, null);
        Check("and marks each one the flag carried",
              keptSaid.Split($"carried only because {ModUpdate.KeepWwiseBuiltFlag} was given").Length == 3);
        Check("neither report has an em-dash", !strictSaid.Contains((char)0x2014) && !keptSaid.Contains((char)0x2014));

        // The game moved 2101 out of the bank into a loose file; the mod's copy declares its
        // own size, so it is refused as the game's own audio there too.
        var movedMod = Bank([1], [(2101, Wem(0xC5, 50))], [(3101, 2101)], declared: new() { [3101] = 50 });
        var movedNow = Bank([1], [], [(3101, 2101)]);
        var file2101 = new ModUpdate.Streamed("Marvel/Content/WwiseAudio/Media/21/2101.wem", () => Wem(0x55, 300));
        var movedPlan = ModUpdate.Rebase(movedMod, movedNow, id => id == 2101 ? file2101 : null);
        Check("a medium the game now streams is refused the same way",
              movedPlan.Carried.Count == 0 && movedPlan.Refused.SingleOrDefault()?.Kind == ModUpdate.WwiseBuiltKind,
              Kinds(movedPlan));

        W("");
        W("audio a container of the game ships byte for byte");
        // The base pak keeps the banks a patch replaced, so a mod made before the patch can hold
        // the game's older audio byte for byte. Here a container ships the very 90 bytes the mod's
        // bank built for 2002. The switch carries audio the bank built itself only where that may
        // be the author's, so it must not carry this, and the refusal says where the copy is.
        var bytes2002 = ModUpdate.Read(builtMod).Media[2002];
        ModUpdate.GameCopy Base(uint id, byte[] bytes) =>
            id == 2002 && bytes.AsSpan().SequenceEqual(bytes2002)
                ? new(true, "pakchunkWwise-Windows.pak", "Marvel/Content/WwiseAudio/bnk_test_update.bnk")
                : Install(id, bytes);
        var basePlan = ModUpdate.Rebase(builtMod, builtNow, null, keepWwiseBuilt: true, shipped: Base);
        var b2002 = basePlan.Refused.FirstOrDefault(r => r.Target == 2002);
        Check($"D: {ModUpdate.KeepWwiseBuiltFlag} does not carry audio a container ships byte for byte, and names the container",
              basePlan.Carried.All(c => c.Target != 2002) &&
              b2002 is { Kind: ModUpdate.WwiseBuiltKind, ShippedIn.Container: "pakchunkWwise-Windows.pak", Flaggable: false } &&
              b2002.Why.Contains("provably the game's own older audio, found in pakchunkWwise-Windows.pak") &&
              b2002.Why.Contains("Marvel/Content/WwiseAudio/bnk_test_update.bnk"), b2002?.Why ?? Kinds(basePlan));
        Check("D: and still carries what no container ships, marked, and the hand swaps",
              basePlan.Carried.FirstOrDefault(c => c.Target == 2003) is { Source: 2913, BuiltByWwise: true } &&
              basePlan.Carried.Any(c => c.Target == 2001) && basePlan.Carried.Any(c => c.Target == 2004) &&
              basePlan.Refused.Count == 1, Kinds(basePlan));
        var baseStrict = ModUpdate.Rebase(builtMod, builtNow, null, shipped: Base);
        var baseSaid = ModUpdate.Describe("x.bnk", "x", builtMod.Length, "x", builtNow.Length, baseStrict, none, null, null, null);
        Check("D: the report counts it as proven and offers the switch only for the one that is not",
              baseStrict.Refused.Count == 2 &&
              baseSaid.Contains("NOT CARRIED 2, 2 of them because the mod's bank built that audio itself (1 provably the " +
                                "game's own older audio") &&
              baseSaid.Contains($"{ModUpdate.KeepWwiseBuiltFlag} carries the 1 of them that are not provably the game's") &&
              !baseSaid.Contains((char)0x2014),
              baseSaid.Split('\n').FirstOrDefault(l => l.Contains("NOT CARRIED"))?.Trim());

        W("");
        W("a line split off a shared medium, and ids the game never had");
        // 3601 and 3602 play 2601 in both banks. The mod moved 3601 alone onto a medium of its own,
        // 2960, and the editor that did it wrote 2960's size into 3601, so it reads as built by
        // Wwise. The game never had a 2960, so it is no older audio of the game's: it is a repoint
        // off a medium 3602 still plays, which is what the refusal must say.
        var splitMod = Bank([1], [(2601, Wem(0x61, 60)), (2960, Wem(0xF6, 108))], [(3601, 2960), (3602, 2601)],
                            declared: new() { [3601] = 108, [3602] = 60 });
        var splitNow = Bank([1], [(2601, Wem(0x61, 60))], [(3601, 2601), (3602, 2601)],
                            declared: new() { [3601] = 60, [3602] = 60 });
        var split = ModUpdate.Rebase(splitMod, splitNow, null);
        var r2601 = split.Refused.SingleOrDefault(r => r.Target == 2601);
        Check("A: a line split off a medium the game's other Sounds still play is refused as shared, not as the game's audio",
              split.Carried.Count == 0 && r2601 is { Kind: "shared", Source: 2960 } && r2601.Why.Contains("3602") &&
              !r2601.Why.Contains("older"), r2601 is null ? Kinds(split) : $"{r2601.Kind}: {r2601.Why}");
        // The same repoint where 3601 plays the medium alone, so nothing else changes with it: it is
        // refused only as built by the bank, and never called the game's own audio, since the game
        // ships no media 2960 anywhere.
        var aloneMod = Bank([1], [(2960, Wem(0xF6, 108))], [(3601, 2960)], declared: new() { [3601] = 108 });
        var aloneNow = Bank([1], [(2601, Wem(0x61, 60))], [(3601, 2601)], declared: new() { [3601] = 60 });
        var alone2960 = ModUpdate.Rebase(aloneMod, aloneNow, null);
        var r2960 = alone2960.Refused.SingleOrDefault();
        var aloneSaid = ModUpdate.Describe("x.bnk", "x", aloneMod.Length, "x", aloneNow.Length, alone2960, none, null, null, null);
        Check("A: built under an id the game ships nowhere, it is refused without calling it the game's audio, naming the switch",
              r2960 is { Kind: ModUpdate.WwiseBuiltKind, Foreign: true, Target: 2601, Source: 2960, Flaggable: true } &&
              !r2960.Why.Contains("game's own older") && r2960.Why.Contains("ships no media 2960") &&
              r2960.Why.Contains(ModUpdate.KeepWwiseBuiltFlag) && !aloneSaid.Contains("so it is the game's own older audio"),
              r2960?.Why ?? Kinds(alone2960));
        // A bank built in its author's own Wwise project: its Sound and its medium have ids of their
        // own, so the line has no slot, and it is said to be what it is, not the game's older audio.
        var ownMod = Bank([1], [(2970, Wem(0xF7, 90))], [(3701, 2970)], declared: new() { [3701] = 90 });
        var ownPlan = ModUpdate.Rebase(ownMod, aloneNow, null);
        var r2970 = ownPlan.Refused.SingleOrDefault();
        Check("E: audio of a bank built with its own ids is said to be that, and that it cannot be placed",
              r2970 is { Kind: ModUpdate.WwiseBuiltKind, NoSlot: true, Foreign: true } &&
              r2970.Why.Contains("built in Wwise with ids of its own") && r2970.Why.Contains("cannot be placed") &&
              !r2970.Why.Contains("game's own older"), r2970?.Why ?? Kinds(ownPlan));

        W("");
        W("a prefetched slot with no streamed file");
        // 3801 plays the start of 2801 from the bank and would stream the rest, but the game ships
        // no file for it, as for two lines of bnk_display_1047001. The mod's whole line goes into
        // the bank's slot, as it always did, and the report says so.
        var preMod = Bank([1], [(2801, Wem(0xF8, 120))], [(3801, 2801)], declared: new() { [3801] = 60 },
                          streams: new() { [3801] = 1 });
        var preNow = Bank([1], [(2801, Wem(0x28, 60))], [(3801, 2801)], declared: new() { [3801] = 60 },
                          streams: new() { [3801] = 1 });
        var prePlan = ModUpdate.Rebase(preMod, preNow, null);
        var preSaid = ModUpdate.Describe("x.bnk", "x", preMod.Length, "x", preNow.Length, prePlan, none, null, null, null);
        Check("G: the whole line is written into the prefetch slot, and the report says the game has no streamed file for it",
              prePlan.Carried.SingleOrDefault() is { Target: 2801, LoosePath: null, PrefetchNoFile: true } p2801 &&
              p2801.Bytes.Length == 120 &&
              preSaid.Contains("the game ships no streamed file for that slot") && preSaid.Contains("media 2801"),
              preSaid.Split('\n').FirstOrDefault(l => l.Contains("NOTE"))?.Trim() ?? Kinds(prePlan));

        W("");
        W("a loose file other banks stream too");
        // A loose file is read by every bank that streams its id, so a carry onto one that
        // another bank streams would change that bank as well. 2201 is an in-place change on
        // a medium the game moved out to a loose file, and 2202 a repoint onto one.
        var otherBank = "Marvel/Content/WwiseAudio/bnk_sfx_other.bnk";
        var looseMod = Bank([1], [(2201, Wem(0xD1)), (2950, Wem(0xD2))], [(3201, 2201), (3202, 2950)]);
        var looseNow = Bank([1], [], [(3201, 2201), (3202, 2202)]);
        ModUpdate.Streamed Shared(uint id, IReadOnlyList<string> others) =>
            id is 2201 or 2202
                ? new ModUpdate.Streamed($"Marvel/Content/WwiseAudio/Media/22/{id}.wem", () => Wem(0x22, 300))
                  { OtherBanks = () => others }
                : null;
        var across = ModUpdate.Rebase(looseMod, looseNow, id => Shared(id, [otherBank]));
        Check("an in-place carry onto a file another bank streams is NOT made, and that bank is named",
              across.Carried.All(c => c.Target != 2201) &&
              across.Refused.FirstOrDefault(r => r.Target == 2201) is { Kind: ModUpdate.OtherBanksKind } o1 &&
              o1.Why.Contains("bnk_sfx_other.bnk"), Kinds(across));
        Check("nor a repointed one",
              across.Carried.All(c => c.Target != 2202) &&
              across.Refused.FirstOrDefault(r => r.Target == 2202) is { Kind: ModUpdate.OtherBanksKind, Source: 2950 },
              Kinds(across));
        var alone = ModUpdate.Rebase(looseMod, looseNow, id => Shared(id, []));
        Check("the same two carry when no other bank streams the file",
              alone.Refused.Count == 0 && alone.Carried.Count(c => c.LoosePath is not null) == 2, Kinds(alone));

        W("");
        W("a shared medium and the Sounds the game added since");
        // 2401 is played today by 3401, which the mod repointed to its own 2961, and by 3402,
        // which the mod's bank does not have. 2402 is played by 3403, which the mod repointed
        // to 2962, by 3405, which the mod's bank does not have either, and by 3404, which the
        // mod's bank has and left on 2402: a sound the author could have changed and did not,
        // so 2402 is refused however many added Sounds share it as well.
        var addedMod = Bank([1], [(2961, Wem(0xE1)), (2962, Wem(0xE2)), (2402, Wem(0x42))],
                            [(3401, 2961), (3403, 2962), (3404, 2402)]);
        var addedNow = Bank([1], [(2401, Wem(0x41)), (2402, Wem(0x42))],
                            [(3401, 2401), (3402, 2401), (3403, 2402), (3404, 2402), (3405, 2402)]);
        var addedPlan = ModUpdate.Rebase(addedMod, addedNow, null);
        Check("a medium whose only other Sound the mod's bank never had carries, naming that Sound",
              addedPlan.Carried.FirstOrDefault(c => c.Target == 2401) is { Source: 2961 } a2401 &&
              a2401.Bytes[8] == 0xE1 && a2401.GameAdded.SequenceEqual([3402u]), Kinds(addedPlan));
        var r2402 = addedPlan.Refused.FirstOrDefault(r => r.Target == 2402);
        Check("one that a Sound the mod's bank has and left alone also plays is still refused as shared",
              addedPlan.Carried.All(c => c.Target != 2402) && r2402 is { Kind: "shared", Source: 2962 } &&
              r2402.Why.Contains("3404") && !r2402.Why.Contains("3405"), r2402?.Why ?? Kinds(addedPlan));
        // The rule rests on a fact read from the mod's bank, so a bank whose HIRC declares
        // more objects than the walk reached cannot say a Sound is absent, only unread.
        var cut = (byte[])addedMod.Clone();
        var hircAt = cut.AsSpan().IndexOf("HIRC"u8) + 8;
        BinaryPrimitives.WriteUInt32LittleEndian(cut.AsSpan(hircAt),
                                                 BinaryPrimitives.ReadUInt32LittleEndian(cut.AsSpan(hircAt)) + 1);
        var cutPlan = ModUpdate.Rebase(cut, addedNow, null);
        Check("a mod bank not read to its end counts no Sound as added, so that carry is refused",
              ModUpdate.Read(addedMod).EveryObjectRead && !ModUpdate.Read(cut).EveryObjectRead &&
              cutPlan.Carried.All(c => c.Target != 2401) &&
              cutPlan.Refused.FirstOrDefault(r => r.Target == 2401) is { Kind: "shared" } r2401 &&
              r2401.Why.Contains("3402"), Kinds(cutPlan));

        W("");
        W("the read check");
        // 3301 is prefetched (the bank holds the start, the rest streams), 3302 is plain, and
        // 3303 is a Silence Sound: a source plugin, whose source id names no medium.
        var readNow = Bank([1], [(2301, Wem(0x23)), (2302, Wem(0x24))], [(3301, 2301), (3302, 2302), (3303, 777)],
                           plugins: new() { [3303] = 0x00650002 }, streams: new() { [3301] = 1 });
        var readShape = ModUpdate.Read(readNow);
        Check("a Sound's plugin, stream type and declared size are read from its body",
              readShape.SoundBodies[3303].Plugin == 0x00650002 && !readShape.SoundBodies[3303].PlaysMedia &&
              readShape.SoundBodies[3301].StreamType == 1 && readShape.SoundBodies[3302].Declared == 0 &&
              ModUpdate.Read(builtMod).SoundBodies[3002].Declared == 90);
        var readSaid = ModUpdate.Describe("x.bnk", "x", readNow.Length, "x", readNow.Length,
                                          ModUpdate.Rebase(readNow, readNow, null), none, null, null, null);
        Check("a source-plugin Sound is counted apart, not as a medium found nowhere",
              readSaid.Contains("2 of 3 Sounds in the current bank decode a medium, and the other 1 use a source plugin") &&
              readSaid.Contains(", 0 neither"), readSaid.Split('\n').FirstOrDefault(l => l.Contains("read check")));
        Check("and a prefetched Sound is counted as embedded, said to stream the rest",
              readSaid.Contains("2 play a medium the bank embeds (1 of them only its start, streaming the rest)"));

        PlacedBySound(W, Check);
        PlacedOnlyOnEvidence(W, Check);
        LooseFollowsTheSound(W, Check);
        FolderInput(W, Check, renamedMod);

        W("");
        W("which of the game's banks a mod's bank replaces");
        string[] game =
        [
            "Marvel/Content/WwiseAudio/Chinese(CN)/bnk_vo_system.bnk",
            "Marvel/Content/WwiseAudio/English(US)/bnk_vo_system.bnk",
            "Marvel/Content/WwiseAudio/Japanese(JP)/bnk_vo_system.bnk",
        ];
        Check("the same language folder, not the first of that name",
              ModUpdate.Counterpart("Marvel/Content/WwiseAudio/English(US)/bnk_vo_system.bnk", game) == game[1]);
        Check("a path the game does not ship has no counterpart",
              ModUpdate.Counterpart("Marvel/Content/WwiseAudio/bnk_vo_system.bnk", game) is null);

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// Every line is placed by the Sound that played it, and the written bank is checked
    /// against that afterwards.
    ///
    /// One pair of banks holds a case of each kind the update has to tell apart. Each is a way
    /// a real patch changes a bank, and each is a way a line can land on the wrong slot without
    /// anything in the output looking broken:
    ///   4001 kept its id, while the game renamed its medium 4100 to 4101 and gave 4100 to the
    ///     line of Sound 4002, as media 228693237 and 350422206 of bnk_vo_1053001 were given away;
    ///   4301 was removed, and event 7001, which played it, now plays one new Sound, 4302;
    ///   4401 was removed from container 4490, which gained one new Sound, 4404, and now lists
    ///     4402 first, where 4401 used to be;
    ///   4501 was removed from mixer 4590, which gained layer 4595 in its place, and of the two
    ///     Sounds in that layer only 4503 declares the size 4501 declared, while event 7007
    ///     reached 4501 and reaches the layer now, as Sound 783910190 of bnk_sfx_1044001 became
    ///     layer 873776568 under event 1811877838;
    ///   4601 was removed, and event 7004 now plays two new Sounds, so its slot is not unique;
    ///   4701 was removed with its event and its container, and its old medium id now belongs
    ///     to a new Sound, 4702;
    ///   music track 4801 still plays 4810, and track 4802 no longer plays 4820.
    /// </summary>
    private static void PlacedBySound(Action<string> W, Action<string, bool, string> Check)
    {
        W("");
        W("every line placed by its Sound, never by a media id");

        // Each of the author's lines is a hand swap: twice the size its Sound declares.
        static byte[] Line(byte mark) => Wem(mark, 120);
        var mod = Bank(
            events: [1, 2, 7001, 7002, 7004, 7005, 7007],
            media:
            [
                (4100, Line(0xB1)), (4200, Wem(0x42)),
                (4310, Line(0xB3)), (4330, Wem(0x33)),
                (4410, Line(0xB4)), (4420, Wem(0x24)), (4430, Wem(0x34)),
                (4510, Line(0xB5)), (4520, Wem(0x25)),
                (4610, Line(0xB6)),
                (4710, Line(0xB7)),
                (4810, Line(0xB8)), (4820, Line(0xB9)),
            ],
            sounds:
            [
                (4001, 4100), (4002, 4200),
                (4301, 4310), (4303, 4330),
                (4401, 4410), (4402, 4420), (4403, 4430),
                (4501, 4510), (4502, 4520),
                (4601, 4610),
                (4701, 4710),
            ],
            declared: new() { [4001] = 60, [4301] = 60, [4401] = 60, [4501] = 60, [4601] = 60, [4701] = 60 },
            parents: new() { [4401] = 4490, [4402] = 4490, [4403] = 4490, [4501] = 4590, [4502] = 4590, [4701] = 4790 },
            containers:
            [
                (4490, RandomKind, 0, [4401, 4402, 4403]),
                (4590, MixerKind, 0, [4501, 4502]),
                (4790, RandomKind, 0, [4701]),
            ],
            plays: new()
            {
                [1] = [4001], [2] = [4002], [7001] = [4301, 4303], [7002] = [4490], [7004] = [4601], [7005] = [4790],
                [7007] = [4501],
            },
            tracks: [(4801, [4810]), (4802, [4820])]);
        (uint Id, byte[] Wem)[] nowFiles =
        [
            (4101, Wem(0x41, 60)), (4100, Wem(0x42)),
            (4320, Wem(0x32, 60)), (4330, Wem(0x33)),
            (4420, Wem(0x24)), (4430, Wem(0x34)), (4440, Wem(0x44, 60)),
            (4510, Wem(0x51, 60)), (4520, Wem(0x25)), (4530, Wem(0x53)),
            (4620, Wem(0x62)), (4630, Wem(0x63)),
            (4710, Wem(0x47, 60)),
            (4810, Wem(0x48, 60)), (4821, Wem(0x49, 60)),
        ];
        (uint Id, uint Source)[] nowSounds =
        [
            (4001, 4101), (4002, 4100),
            (4302, 4320), (4303, 4330),
            (4402, 4420), (4403, 4430), (4404, 4440),
            (4502, 4520), (4503, 4510), (4504, 4530),
            (4602, 4620), (4603, 4630),
            (4702, 4710),
        ];
        var now = Bank(
            events: [1, 2, 7001, 7003, 7004, 7006, 7007],
            media: nowFiles,
            sounds: nowSounds,
            declared: AsBuilt(nowFiles, nowSounds),
            parents: new() { [4402] = 4490, [4403] = 4490, [4404] = 4490, [4502] = 4590, [4503] = 4595, [4504] = 4595 },
            containers:
            [
                (4490, RandomKind, 0, [4402, 4403, 4404]),
                (4590, MixerKind, 0, [4502, 4595]),
                (4595, LayerKind, 4590, [4503, 4504]),
            ],
            plays: new()
            {
                [1] = [4001], [2] = [4002], [7001] = [4302, 4303], [7003] = [4490], [7004] = [4602, 4603], [7006] = [4702],
                [7007] = [4595],
            },
            tracks: [(4801, [4810]), (4802, [4821])]);

        // The fixture has to read the way the game's banks do, or every check below could pass
        // or fail for a reason that has nothing to do with the update.
        var lm = ModUpdate.Lines.Of(mod, "mod");
        var ln = ModUpdate.Lines.Of(now, "now");
        Check("the hierarchy walk reads every object of both banks, links included",
              lm.Whole && ln.Whole && lm.SoundMedia.Count == 11 && ln.SoundMedia.Count == 13 &&
              lm.Parent.GetValueOrDefault(4401u) == 4490 && ln.Children.GetValueOrDefault(4595u)?.Length == 2 &&
              lm.EventSounds.GetValueOrDefault(7001u)?.SetEquals([4301u, 4303u]) == true &&
              lm.EventSounds.GetValueOrDefault(7002u)?.SetEquals([4401u, 4402u, 4403u]) == true &&
              ln.TrackMedia.GetValueOrDefault(4802u)?.SequenceEqual([4821u]) == true,
              $"{lm.Unreadable} / {ln.Unreadable} unread, {lm.SoundMedia.Count} / {ln.SoundMedia.Count} Sounds");

        var plan = ModUpdate.Rebase(mod, now, null);
        ModUpdate.Carried Got(uint target) => plan.Carried.FirstOrDefault(c => c.Target == target);
        ModUpdate.Refused No(uint source) => plan.Refused.FirstOrDefault(r => r.Source == source);
        string All() => string.Join(", ", plan.Carried.Select(c => $"{c.Source}->{c.Target} {c.Anchor}")) + " | " +
                        string.Join(", ", plan.Refused.Select(r => $"{r.Kind}:{r.Target}/{r.Source}"));

        Check("a line on a media id the game gave to another Sound follows its own Sound to its new medium",
              Got(4101) is { Source: 4100, How: "repointed", Anchor: ModUpdate.SameSound } a && a.Bytes[8] == 0xB1, All());
        Check("and is NOT carried in place onto the id the game gave away",
              Got(4100) is null && plan.Stock.Any(s => s.Target == 4100 && s.Source == 4200), All());

        Check("a removed Sound whose event now plays exactly one new Sound is placed there by the event",
              Got(4320) is { Source: 4310, Anchor: ModUpdate.EventFallback } b && b.Bytes[8] == 0xB3 &&
              b.Placements.Single() is { Was: 4301, Now: 4302, Event: 7001 }, All());

        Check("a removed Sound whose container swapped it for exactly one new Sound is placed there",
              Got(4440) is { Source: 4410, Anchor: ModUpdate.ContainerFallback } k && k.Bytes[8] == 0xB4 &&
              k.Placements.Single() is { Was: 4401, Now: 4404 }, All());
        Check("and not by position: 4402, now first in the container's list, keeps its own line",
              Got(4420) is null && plan.Carried.All(c => c.Source != 4410 || c.Target == 4440), All());
        Check("a removed Sound the game replaced with a container lands on the one Sound in it on its medium",
              Got(4510) is { Source: 4510, Anchor: ModUpdate.ContainerFallback } l && l.Bytes[8] == 0xB5 &&
              l.Placements.Single() is { Was: 4501, Now: 4503 } && Got(4530) is null, All());

        Check("a removed Sound whose event now plays two new Sounds is reported, not placed",
              Got(4620) is null && Got(4630) is null &&
              No(4610) is { Kind: ModUpdate.NoHomeKind } d && d.Why.Contains("not unique"), No(4610)?.Why ?? All());
        Check("a removed Sound nothing pairs is reported, and NOT carried onto its old id, which another Sound has now",
              Got(4710) is null && No(4710) is { Kind: ModUpdate.NoHomeKind } e &&
              e.Why.Contains("is not in the current game") && e.Why.Contains("an id alone is not trusted"),
              No(4710)?.Why ?? All());

        Check("a medium only a music track plays carries in place while that track still plays it",
              Got(4810) is { Source: 4810, How: "in place", Anchor: ModUpdate.SameTrack } f && f.Bytes[8] == 0xB8, All());
        Check("and is reported, never placed by position, when the track plays something else now",
              Got(4821) is null && No(4820) is { Kind: ModUpdate.NoHomeKind }, All());
        Check("nothing else is carried or refused",
              plan.Carried.Count == 5 && plan.Refused.Count == 3, All());

        var report = ModUpdate.Describe("x.bnk", "x", mod.Length, "x", now.Length, plan,
                                        new ModUpdate.Written([], null, 0, 0), null, null, null);
        Check("the report names the anchor that placed each carry",
              report.Contains($"anchor: {ModUpdate.SameSound} (Sound 4001)") &&
              report.Contains("anchor: event 7001 fallback: Sound 4301 is gone") &&
              report.Contains($"anchor: {ModUpdate.ContainerFallback}: Sound 4401 was the only child container 4490 lost") &&
              report.Contains("Sound 4503 is the only one of its 2 Sounds that declares the 60 B Sound 4501 declared") &&
              report.Contains("event 7007 reached Sound 4501 and reaches Sound 4503") &&
              report.Contains($"anchor: {ModUpdate.SameTrack} (music track 4801)"),
              string.Join(" / ", report.Split('\n').Where(x => x.Contains("anchor:")).Select(x => x.Trim())));
        Check("and the report has no em-dash", !report.Contains((char)0x2014), null);

        W("");
        W("the placement audit");
        var root = Path.Combine(Path.GetTempPath(), "XzoundWave", "updatetest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ModUpdate.Audit Audited(ModUpdate.Plan p, string folder, ModUpdate.Plan claims = null)
            {
                var dir = Path.Combine(root, folder);
                var w = ModUpdate.Write(p, now, GamePath, dir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                var written = w.BankPath is null ? now : File.ReadAllBytes(Path.Combine(dir, w.BankPath));
                return ModUpdate.AuditPlacement(claims ?? p, mod, now, written, _ => null);
            }

            // Three of the five lines sit where a fallback put them, so they are placed but only
            // inferred: no object is the same, and the audit never calls one verified.
            var good = Audited(plan, "good");
            Check("every line on the slot it was written for, fallbacks included, reads N of N",
                  good.Clean && good.Lines == 5 && good.Placed == 5 && good.Verified == 2 && good.Inferred == 3 &&
                  good.Line == "placement audit: 5 of 5 placed; 2 verified by the same sound object, 3 inferred by a " +
                               "fallback (listed below)",
                  good.Line + " " + string.Join("; ", good.Problems));
            var goodSaid = ModUpdate.Describe("x.bnk", "x", mod.Length, "x", now.Length, plan,
                                              new ModUpdate.Written([], null, 0, 0), null, null, null, good);
            Check("and each inferred line is listed with the evidence the audit found for it",
                  good.Inferences.Count == 3 &&
                  good.Inferences.Any(i => i.StartsWith("mod media 4310 -> media 4320: on Sound 4302 in place of Sound 4301") &&
                                           i.Contains("by the event fallback: event 7001") && i.Contains("declares the 60 B")) &&
                  good.Inferences.Any(i => i.StartsWith("mod media 4410 -> media 4440: on Sound 4404 in place of Sound 4401") &&
                                           i.Contains("container 4490 lost only Sound 4401 and gained only Sound 4404")) &&
                  good.Inferences.Any(i => i.StartsWith("mod media 4510 -> media 4510: on Sound 4503 in place of Sound 4501") &&
                                           i.Contains("Sound 4503 is the only one of its 2 Sounds that declares the 60 B") &&
                                           i.Contains("corroborated by event 7007")) &&
                  goodSaid.Split("INFERRED").Length == 4 && !goodSaid.Contains((char)0x2014),
                  string.Join(" / ", good.Inferences));

            // The two mistakes this update exists to stop, made on purpose: the line of the
            // removed 4401 put on the Sound that took its place in the list, and 4001's line put
            // back on the id the game gave to 4002. Built from the banks rather than from the
            // plan, so the check stands whatever the plan decided.
            var modMedia = ModUpdate.Read(mod).Media;
            var nowMedia = ModUpdate.Read(now).Media;
            var byPosition = new ModUpdate.Carried(4420, 4410, "repointed", modMedia[4410], nowMedia[4420], [4402], null);
            var byId = new ModUpdate.Carried(4100, 4100, "in place", modMedia[4100], nowMedia[4100], [4002], null);
            var wrong = plan with
            {
                Carried = [.. plan.Carried.Where(c => c.Source is not (4410 or 4100)), byPosition, byId],
            };
            var caught = Audited(wrong, "wrong");
            Check("a line put on the wrong Sound is caught and named, and the update is not clean",
                  !caught.Clean && caught.Lines == 5 && caught.Placed == 3 && caught.Problems.Count == 2 &&
                  caught.Problems.Any(p => p.Contains("mod media 4410 -> media 4420") && p.Contains("Sound 4402")) &&
                  caught.Problems.Any(p => p.Contains("mod media 4100 -> media 4100") && p.Contains("Sound 4002")),
                  caught.Line + " " + string.Join("; ", caught.Problems));

            // A change the plan does not own: the bank is written with every carry, then audited
            // against a plan that does not mention one of them.
            var unclaimed = Audited(plan, "unclaimed", plan with { Carried = plan.Carried.Where(c => c.Target != 4320).ToList() });
            Check("a medium changed that no carry claims is caught",
                  !unclaimed.Clean && unclaimed.Problems.Any(p => p.Contains("media 4320 differs")),
                  string.Join("; ", unclaimed.Problems));

            var said = ModUpdate.Describe("x.bnk", "x", mod.Length, "x", now.Length, wrong,
                                          new ModUpdate.Written([], null, 0, 0), null, null, null, caught);
            Check("the report gives the audit line and lists each misplaced line under it",
                  said.Contains("placement audit: 3 of 5 placed; 1 verified by the same sound object, 2 inferred by a fallback") &&
                  said.Contains("NOT a clean update") && said.Split("MISPLACED").Length == 3 && !said.Contains((char)0x2014),
                  string.Join(" / ", said.Split('\n').Where(x => x.Contains("placement audit") || x.Contains("MISPLACED")).Select(x => x.Trim())));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// A line whose Sound the game removed is placed only on evidence, and reported otherwise.
    ///
    /// Two reviewers built eight cases that put such a line on the wrong slot through the
    /// fallbacks, and the audit passed all eight because it shared the fallbacks' assumptions.
    /// Each is here, and each must end reported, written nowhere. The misplacement the old rules
    /// made in each is also forced into a plan and audited, so the audit is shown to reject it on
    /// its own and not merely to agree with the plan. Beside them are the cases the same rules
    /// must still place, so no guard passes by refusing everything: a container where only one
    /// Sound declares the removed Sound's size, a switch child that kept its state, and an event's
    /// new Sound in the removed Sound's own pool. Every current bank here declares its media's
    /// sizes, as the game's banks do, and every removed Sound declares the 60 B of the game's line
    /// it played, which a hand swap of the author's 120 B line leaves as it was.
    /// </summary>
    private static void PlacedOnlyOnEvidence(Action<string> W, Action<string, bool, string> Check)
    {
        W("");
        W("a removed Sound's line is placed only on evidence, and reported otherwise");

        static byte[] L(byte mark) => Wem(mark, 120);     // the author's line, swapped in by hand
        static byte[] G(byte mark) => Wem(mark, 60);      // the game's own audio
        static Dictionary<uint, uint> Sixty(params uint[] sounds) => sounds.ToDictionary(s => s, _ => 60u);
        static byte[] Now(uint[] events, (uint Id, byte[] Wem)[] media, (uint Id, uint Source)[] sounds,
                          Dictionary<uint, uint> parents, (uint Id, byte Kind, uint Parent, uint[] Children)[] containers,
                          Dictionary<uint, uint[]> plays,
                          Dictionary<uint, (uint Group, Dictionary<uint, uint[]> States)> switches = null,
                          Dictionary<uint, uint[]> stops = null) =>
            Bank(events, media, sounds, declared: AsBuilt(media, sounds), parents: parents, containers: containers,
                 plays: plays, switches: switches, stops: stops);

        var root = Path.Combine(Path.GetTempPath(), "XzoundWave", "updatetest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // The plan, the bank it writes, and the audit of that bank as read back from disk.
            (ModUpdate.Plan Plan, ModUpdate.Audit Audit, byte[] Written) Run(string name, byte[] mod, byte[] now,
                                                                            ModUpdate.Plan forced = null)
            {
                var plan = forced ?? ModUpdate.Rebase(mod, now, null);
                var dir = Path.Combine(root, name);
                var w = ModUpdate.Write(plan, now, GamePath, dir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                var written = w.BankPath is null ? now : File.ReadAllBytes(Path.Combine(dir, w.BankPath));
                return (plan, ModUpdate.AuditPlacement(plan, mod, now, written, _ => null), written);
            }
            // Where a line of the mod's is in a written bank, found by its bytes alone, whatever a plan says.
            static uint[] Holding(byte[] written, byte[] mod, uint line)
            {
                var bytes = ModUpdate.Read(mod).Media[line];
                return ModUpdate.Read(written).Media.Where(kv => kv.Value.AsSpan().SequenceEqual(bytes))
                                .Select(kv => kv.Key).OrderBy(x => x).ToArray();
            }
            static string Said(ModUpdate.Plan p, ModUpdate.Audit a) =>
                string.Join(", ", p.Carried.Select(c => $"{c.Source}->{c.Target} {c.Anchor}")) + " | " +
                string.Join(" / ", p.Refused.Select(r => $"{r.Kind}:{r.Target}/{r.Source} {r.Why}")) + " | " +
                a.Line + " " + string.Join("; ", a.Problems) + string.Join("; ", a.Inferences);

            void Reported(string name, string what, byte[] mod, byte[] now, uint target, uint landing, string anchor, uint ev = 0)
            {
                var (plan, audit, written) = Run(name, mod, now);
                Check($"{name}: {what}, is reported, not placed",
                      plan.Carried.All(c => c.Source != 100) && Holding(written, mod, 100).Length == 0 &&
                      plan.Refused.Any(r => r.Source == 100 && r.Kind == ModUpdate.NoHomeKind),
                      Said(plan, audit));
                var m = ModUpdate.Read(mod).Media;
                var n = ModUpdate.Read(now).Media;
                var forced = plan with
                {
                    Carried =
                    [
                        new ModUpdate.Carried(target, 100, target == 100 ? "in place" : "repointed", m[100], n[target],
                                              [landing], null)
                        { Placements = [new ModUpdate.Placement(11, landing, anchor, ev)] },
                    ],
                };
                var (_, caught, _) = Run(name + "_forced", mod, now, forced);
                Check($"{name}: the audit rejects the misplacement the old rules made there, onto Sound {landing}",
                      !caught.Clean && caught.Placed == 0 &&
                      caught.Problems.Any(p => p.Contains($"mod media 100 -> media {target}") && p.Contains($"Sound {landing}")),
                      caught.Line + " " + string.Join("; ", caught.Problems));
            }

            void Placed(string name, string what, byte[] mod, byte[] now, uint line, uint was, uint target, uint landing,
                        string anchor, params string[] evidence)
            {
                var (plan, audit, written) = Run(name, mod, now);
                Check($"{name}: {what}, is placed on Sound {landing}, inferred and never verified, with its evidence",
                      plan.Carried.SingleOrDefault(x => x.Source == line) is { } c && c.Target == target &&
                      c.Placements.Single() is { } p && p.Was == was && p.Now == landing && p.Anchor == anchor &&
                      Holding(written, mod, line).SequenceEqual([target]) &&
                      audit.Clean && audit.Lines == 1 && audit.Verified == 0 && audit.Inferred == 1 &&
                      audit.Inferences.Single() is { } i && evidence.All(i.Contains),
                      Said(plan, audit));
            }

            // ---- CONTENT. The game replaced Sound 11 with a container of two new Sounds and handed
            // 11's old media id 100 to the first, as it handed ids to sibling lines of bnk_vo_1053001.
            // Placed by that id, the line lands on whichever Sound now has it.
            var a5Mod = Bank([1, 3], [(100, L(0xA5)), (300, G(0x30))], [(11, 100), (13, 300)], declared: Sixty(11, 13),
                             parents: new() { [11] = 90, [13] = 90 }, containers: [(90, MixerKind, 0, [11, 13])],
                             plays: new() { [1] = [11], [3] = [13] });
            byte[] A5Now(byte kind, int first, int second) =>
                Now([1, 3], [(100, Wem(0x21, first)), (101, Wem(0x22, second)), (300, G(0x30))],
                    [(13, 300), (21, 100), (22, 101)],
                    parents: new() { [13] = 90, [21] = 95, [22] = 95 },
                    containers: [(90, MixerKind, 0, [13, 95]), (95, kind, 90, [21, 22])],
                    plays: new() { [1] = [95], [3] = [13] });
            Reported("A5", "Sound 11 replaced by a random container whose two Sounds both declare its 60 B, its old id on one",
                     a5Mod, A5Now(RandomKind, 60, 60), 100, 21, ModUpdate.ContainerFallback);
            Reported("A5b", "the same with a layer, the shape of Vergil's sfx bank",
                     a5Mod, A5Now(LayerKind, 60, 60), 100, 21, ModUpdate.ContainerFallback);
            Reported("A5c", "the same where neither declares its 60 B and its old id holds another line of 72 B",
                     a5Mod, A5Now(RandomKind, 72, 64), 100, 21, ModUpdate.ContainerFallback);
            Placed("A5d", "the same where only the Sound on its old id declares its 60 B",
                   a5Mod, A5Now(LayerKind, 60, 64), 100, 11, 100, 21, ModUpdate.ContainerFallback,
                   "Sound 21 is the only one of its 2 Sounds that declares the 60 B Sound 11 declared",
                   "corroborated by event 1");

            // A reviewer's own case: the game re-exported 4501's line under a new id 4540 on Sound
            // 4503 and gave the old id 4510 to an unrelated 200 B layer, 4504. The old rule put the
            // line in place on 4510, on 4504; the size puts it on 4503.
            var trMod = Bank([1], [(4510, L(0xB5)), (4520, Wem(0x25))], [(4501, 4510), (4502, 4520)],
                             declared: new() { [4501] = 60, [4502] = 40 },
                             parents: new() { [4501] = 4590, [4502] = 4590 }, containers: [(4590, MixerKind, 0, [4501, 4502])],
                             plays: new() { [1] = [4590] });
            var trNow = Now([1], [(4510, Wem(0x99, 200)), (4520, Wem(0x25)), (4540, Wem(0x51, 60))],
                            [(4502, 4520), (4503, 4540), (4504, 4510)],
                            parents: new() { [4502] = 4590, [4503] = 4595, [4504] = 4595 },
                            containers: [(4590, MixerKind, 0, [4502, 4595]), (4595, LayerKind, 4590, [4503, 4504])],
                            plays: new() { [1] = [4590] });
            Placed("T1", "a line whose old id the game gave to an unrelated 200 B Sound in its replacement layer",
                   trMod, trNow, 4510, 4501, 4540, 4503, ModUpdate.ContainerFallback,
                   "Sound 4503 is the only one of its 2 Sounds that declares the 60 B Sound 4501 declared");

            // ---- ACTOR-MIXER. A folder of unrelated Sounds, where 850 of the 967 Sounds of one voice
            // bank sit; one line out and one in says nothing about their being the same line.
            var e3Mod = Bank([1, 8, 9], [(100, L(0xE3)), (180, G(0x18)), (190, G(0x19))],
                             [(11, 100), (18, 180), (19, 190)], declared: Sixty(11, 18, 19),
                             parents: new() { [11] = 90, [19] = 90, [18] = 91 },
                             containers: [(90, MixerKind, 0, [11, 19]), (91, RandomKind, 0, [18])],
                             plays: new() { [1] = [11], [8] = [18], [9] = [19] });
            var e3Now = Now([5, 6, 8, 9], [(200, G(0x20)), (210, G(0x21)), (180, G(0x18)), (190, G(0x19))],
                            [(21, 200), (22, 210), (18, 180), (19, 190)],
                            parents: new() { [21] = 91, [22] = 90, [19] = 90, [18] = 91 },
                            containers: [(90, MixerKind, 0, [19, 22]), (91, RandomKind, 0, [18, 21])],
                            plays: new() { [5] = [21], [6] = [22], [8] = [18], [9] = [19] });
            Reported("E3", "Sound 11 moved under a new id to another container, its event renamed, and its old folder " +
                     "given an unrelated new Sound", e3Mod, e3Now, 210, 22, ModUpdate.ContainerFallback);
            var f1Mod = Bank([1, 2, 3], [(100, L(0xF1)), (120, G(0x12)), (130, G(0x13))],
                             [(11, 100), (12, 120), (13, 130)], declared: Sixty(11, 12, 13),
                             parents: new() { [11] = 90, [12] = 90, [13] = 90 },
                             containers: [(90, MixerKind, 0, [11, 12, 13])],
                             plays: new() { [1] = [11], [2] = [12], [3] = [13] });
            var f1Now = Now([5, 6, 7], [(120, G(0x12)), (130, G(0x13)), (200, G(0x20))],
                            [(12, 120), (13, 130), (21, 200)],
                            parents: new() { [12] = 90, [13] = 90, [21] = 90 },
                            containers: [(90, MixerKind, 0, [12, 13, 21])],
                            plays: new() { [5] = [12], [6] = [13], [7] = [21] });
            Reported("F1", "a hero's voice folder lost line 11 and gained an unrelated line 21 while every event was renamed",
                     f1Mod, f1Now, 200, 21, ModUpdate.ContainerFallback);

            // ---- SWITCH. A switch container plays a child for its switch state, not for its place
            // in the child list.
            var s1Mod = Bank([1], [(100, L(0x51)), (300, G(0x30))], [(11, 100), (13, 300)], declared: Sixty(11, 13),
                             parents: new() { [11] = 90, [13] = 90 }, containers: [(90, SwitchKind, 0, [11, 13])],
                             plays: new() { [1] = [90] },
                             switches: new() { [90] = (500, new() { [601] = [11], [602] = [13] }) });
            byte[] S1Now(uint state) =>
                Now([1], [(200, G(0x20)), (300, G(0x30))], [(13, 300), (21, 200)],
                    parents: new() { [13] = 90, [21] = 90 }, containers: [(90, SwitchKind, 0, [13, 21])],
                    plays: new() { [1] = [90] },
                    switches: new() { [90] = (500, new() { [602] = [13], [state] = [21] }) });
            Reported("S1", "a switch container lost the Sound for state 601 and gained one for a new state 603",
                     s1Mod, S1Now(603), 200, 21, ModUpdate.EventFallback, 1);
            Placed("S2", "the same where the new Sound plays for state 601", s1Mod, S1Now(601), 100, 11, 200, 21,
                   ModUpdate.EventFallback, "by the event fallback: event 1",
                   "both play under switch container 90 (group 500) on state 601");

            // Two pools, the line's own and another. S3 under a switch, S3r under a random
            // container: the event's one new Sound arrived in the other pool, while the line's own
            // pool is still in the game and gained nothing.
            byte[] PoolsMod(byte top) =>
                Bank([1], [(100, L(0x53)), (140, G(0x14)), (130, G(0x13))], [(11, 100), (14, 140), (13, 130)],
                     declared: Sixty(11, 13, 14), parents: new() { [11] = 91, [14] = 91, [13] = 92 },
                     containers: [(90, top, 0, [91, 92]), (91, RandomKind, 90, [11, 14]), (92, RandomKind, 90, [13])],
                     plays: new() { [1] = [90] },
                     switches: new() { [90] = (500, new() { [601] = [91], [602] = [92] }) });
            byte[] PoolsNow(byte top, uint pool) =>
                Now([1], [(140, G(0x14)), (130, G(0x13)), (200, G(0x20))], [(14, 140), (13, 130), (21, 200)],
                    parents: new() { [14] = 91, [13] = 92, [21] = pool },
                    containers: [(90, top, 0, [91, 92]),
                                 (91, RandomKind, 90, pool == 91 ? new uint[] { 14, 21 } : new uint[] { 14 }),
                                 (92, RandomKind, 90, pool == 92 ? new uint[] { 13, 21 } : new uint[] { 13 })],
                    plays: new() { [1] = [90] },
                    switches: new() { [90] = (500, new() { [601] = [91], [602] = [92] }) });
            Reported("S3", "under a switch, line 11 left the pool for state 601 and a new Sound arrived in the pool for 602",
                     PoolsMod(SwitchKind), PoolsNow(SwitchKind, 92), 200, 21, ModUpdate.EventFallback, 1);

            // ---- OWN PARENT.
            Reported("S3r", "the same under a random container, so only the line's own pool still in the game says it",
                     PoolsMod(RandomKind), PoolsNow(RandomKind, 92), 200, 21, ModUpdate.EventFallback, 1);
            Placed("S3o", "the same where the new Sound arrived in the line's own pool", PoolsMod(RandomKind),
                   PoolsNow(RandomKind, 91), 100, 11, 200, 21, ModUpdate.EventFallback, "by the event fallback: event 1");

            // ---- STOP. An event names the objects every one of its actions acts on, and one that
            // only stops a Sound says nothing about which line is heard there. In C1, Sound 11 was
            // played by event 1 and stopped by event 2; the game renamed event 1 and removed 11, and
            // event 2 now stops one new Sound, 21, which declares 11's size. Counted as a witness,
            // event 2 alone put the line on 21. In C2 the same happens in an actor-mixer, where one
            // Sound out and one in is evidence only with an event that played both: event 2 stops 11
            // and 21, and also the removed 12, so it is no event witness and only the folder's
            // corroboration put the line there.
            var c1Mod = Bank([1, 2], [(100, L(0xC5))], [(11, 100)], declared: Sixty(11),
                             plays: new() { [1] = [11] }, stops: new() { [2] = [11] });
            var c1Now = Now([2, 5], [(200, G(0x20))], [(21, 200)], parents: null, containers: null,
                            plays: new() { [5] = [21] }, stops: new() { [2] = [21] });
            var c2Mod = Bank([1, 2, 3, 4], [(100, L(0xC6)), (120, G(0x12)), (130, G(0x13))],
                             [(11, 100), (12, 120), (13, 130)], declared: Sixty(11, 12, 13),
                             parents: new() { [11] = 90, [13] = 90 }, containers: [(90, MixerKind, 0, [11, 13])],
                             plays: new() { [1] = [11], [3] = [13], [4] = [12] }, stops: new() { [2] = [11, 12] });
            var c2Now = Now([2, 3, 5], [(200, G(0x20)), (130, G(0x13))], [(21, 200), (13, 130)],
                            parents: new() { [21] = 90, [13] = 90 }, containers: [(90, MixerKind, 0, [13, 21])],
                            plays: new() { [5] = [21], [3] = [13] }, stops: new() { [2] = [21] });
            // The fixtures must read as the game's banks do, with the stop read as a stop, or both
            // checks below would pass for want of a hierarchy rather than for the rule.
            var c1m = ModUpdate.Lines.Of(c1Mod, "mod");
            var c1n = ModUpdate.Lines.Of(c1Now, "now");
            var c2m = ModUpdate.Lines.Of(c2Mod, "mod");
            Check("C: a Stop action is read, reaches its Sound, and is not counted as playing it",
                  c1m.Whole && c1n.Whole && c2m.Whole &&
                  c1m.EventSounds.GetValueOrDefault(2u)?.SetEquals([11u]) == true && !c1m.PlaySounds.ContainsKey(2) &&
                  c1m.PlayEvents.GetValueOrDefault(11u)?.SetEquals([1u]) == true &&
                  c1n.EventSounds.GetValueOrDefault(2u)?.SetEquals([21u]) == true && !c1n.PlaySounds.ContainsKey(2) &&
                  c2m.EventSounds.GetValueOrDefault(2u)?.SetEquals([11u, 12u]) == true,
                  $"{c1m.Unreadable} / {c1n.Unreadable} / {c2m.Unreadable} unread; event 2 reaches " +
                  string.Join(",", c1m.EventSounds.GetValueOrDefault(2u) ?? []));
            Reported("C1", "Sound 11's only surviving event stops it, and now stops one new Sound of 11's size",
                     c1Mod, c1Now, 200, 21, ModUpdate.EventFallback, 2);
            Reported("C2", "an actor-mixer swapped Sound 11 for Sound 21, and the only event that reaches both stops them",
                     c2Mod, c2Now, 200, 21, ModUpdate.ContainerFallback);

            // ---- THE AUDIT ON ITS OWN. Two gaps a reviewer found, each shown with a carry forced
            // onto the wrong slot, since the plan itself refuses both.
            //
            // A line put on a Sound the mod's bank never had, which no placement of this line claims,
            // but which a fallback gives to another of the mod's Sounds: 5003 replaced 5002, whose
            // line is the game's own 5200, and it plays the same medium 5300 as 5001, whose line the
            // author changed. Carrying 5001's line there replaces 5002's.
            var t2Mod = Bank([1, 2], [(5100, L(0xC1)), (5200, G(0x52))], [(5001, 5100), (5002, 5200)],
                             declared: Sixty(5001, 5002), parents: new() { [5002] = 5090 },
                             containers: [(5090, MixerKind, 0, [5002])], plays: new() { [1] = [5001], [2] = [5090] });
            var t2Now = Now([1, 2], [(5300, G(0x53))], [(5001, 5300), (5003, 5300)],
                            parents: new() { [5003] = 5090 }, containers: [(5090, MixerKind, 0, [5003])],
                            plays: new() { [1] = [5001], [2] = [5090] });
            var (t2Plan, _, _) = Run("T2", t2Mod, t2Now);
            // Refused as split: 5001 and 5003 land on 5300 from two of the mod's media, and 5200 is
            // not set aside as the game's older audio, because a line is only that where every Sound
            // landing on the medium plays it.
            Check("T2: a line on a medium another removed Sound's fallback slot also plays is refused by the plan",
                  t2Plan.Carried.Count == 0 && t2Plan.Refused.Any(r => r.Target == 5300 && r.Kind is "shared" or "split"),
                  string.Join(" / ", t2Plan.Refused.Select(r => $"{r.Kind}:{r.Target}/{r.Source} {r.Why}")));
            var t2Media = ModUpdate.Read(t2Mod).Media;
            var t2Forced = t2Plan with
            {
                Carried =
                [
                    new ModUpdate.Carried(5300, 5100, "repointed", t2Media[5100], ModUpdate.Read(t2Now).Media[5300],
                                          [5001, 5003], null)
                    { Placements = [new ModUpdate.Placement(5001, 5001, ModUpdate.SameSound)] },
                ],
            };
            var (_, t2Audit, _) = Run("T2_forced", t2Mod, t2Now, t2Forced);
            Check("T2: and the audit flags it when forced, naming the Sound whose slot it took",
                  !t2Audit.Clean && t2Audit.Problems.Any(p => p.Contains("Sound 5003, which a fallback pairs with Sound 5002")),
                  t2Audit.Line + " " + string.Join("; ", t2Audit.Problems));

            // A removed Sound two surviving events reached, each of which now reaches a different
            // new Sound. One event alone pairs it with 6002 and the other with 6003, so the events
            // disagree, and the plan reports it. The audit must not accept either on one event.
            var t3Mod = Bank([1, 2], [(6100, L(0xD1))], [(6001, 6100)], declared: Sixty(6001),
                             plays: new() { [1] = [6001], [2] = [6001] });
            var t3Now = Now([1, 2], [(6200, G(0x62)), (6300, G(0x63))], [(6002, 6200), (6003, 6300)],
                            parents: null, containers: null, plays: new() { [1] = [6002], [2] = [6003] });
            var (t3Plan, _, _) = Run("T3", t3Mod, t3Now);
            Check("T3: a removed Sound whose two surviving events now reach different new Sounds is reported",
                  t3Plan.Carried.Count == 0 && t3Plan.Refused.Any(r => r.Source == 6100 && r.Kind == ModUpdate.NoHomeKind),
                  string.Join(" / ", t3Plan.Refused.Select(r => $"{r.Kind}:{r.Target}/{r.Source} {r.Why}")));
            var t3Forced = t3Plan with
            {
                Carried =
                [
                    new ModUpdate.Carried(6200, 6100, "repointed", ModUpdate.Read(t3Mod).Media[6100],
                                          ModUpdate.Read(t3Now).Media[6200], [6002], null)
                    { Placements = [new ModUpdate.Placement(6001, 6002, ModUpdate.EventFallback, 1)] },
                ],
            };
            var (_, t3Audit, _) = Run("T3_forced", t3Mod, t3Now, t3Forced);
            Check("T3: and the audit rejects it when forced onto the one Sound a single event names",
                  !t3Audit.Clean && t3Audit.Placed == 0 &&
                  t3Audit.Problems.Any(p => p.Contains("which no unique fallback pairs with Sound 6001")),
                  t3Audit.Line + " " + string.Join("; ", t3Audit.Problems));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// A loose file the mod ships follows the Sound that played it, and is never written where
    /// its id points.
    ///
    /// L1, from a reviewer: Sound 11 streams its whole line, so the mod ships the line as the loose
    /// file 100.wem and its bank holds none of it. The game moved 11 onto 101 and gave 100 to a new
    /// Sound, 19. Written by its path, as every loose file used to be, the author's line replaced
    /// Sound 19's and no audit saw it. L2 is a line the game keeps the start of in the bank and
    /// streams the rest of, which needs both halves written.
    /// </summary>
    private static void LooseFollowsTheSound(Action<string> W, Action<string, bool, string> Check)
    {
        W("");
        W("a loose file follows its Sound, never its path");

        const string English = "Marvel/Content/WwiseAudio/Media/English(US)/";
        var mod = Bank([1, 2], [(120, Wem(0xCA, 300)[..80])], [(11, 100), (12, 120)],
                       declared: new() { [11] = 60, [12] = 80 }, streams: new() { [11] = 2, [12] = 1 },
                       plays: new() { [1] = [11], [2] = [12] });
        var now = Bank([1, 2, 9], [(121, Wem(0x12, 300)[..80])], [(11, 101), (19, 100), (12, 121)],
                       declared: new() { [11] = 60, [19] = 60, [12] = 80 }, streams: new() { [11] = 2, [19] = 2, [12] = 1 },
                       plays: new() { [1] = [11], [2] = [12], [9] = [19] });
        var line = Wem(0xC7, 120);
        var whole = Wem(0xCA, 300);
        ModBank.LooseMedia[] files =
        [
            new(English + "100.wem", line),
            new(English + "120.wem", whole),
            new("Marvel/Content/WwiseAudio/Media/Japanese(JP)/100.wem", Wem(0xC8, 120)),   // the Japanese bank's
            new(English + "555.wem", Wem(0xC9, 120)),                                       // no Sound plays it
        ];
        var game = new Dictionary<uint, (string Path, byte[] Bytes)>
        {
            [100] = (English + "100.wem", Wem(0x19, 300)),
            [101] = (English + "101.wem", Wem(0x11, 300)),
            [121] = (English + "121.wem", Wem(0x12, 300)),
        };
        ModUpdate.Streamed StreamedAt(uint id) =>
            game.TryGetValue(id, out var f) ? new ModUpdate.Streamed(f.Path, () => f.Bytes) : null;

        var picked = ModUpdate.LooseFor(mod, "English(US)", files);
        Check("only the files a Sound of the bank plays, from the bank's own language folder, are handed to it",
              picked.Keys.Order().SequenceEqual([100u, 120u]) && picked[100].MountedAt == English + "100.wem",
              string.Join(", ", picked.Values.Select(v => v.MountedAt)));
        var loose = picked.ToDictionary(kv => kv.Key, kv => kv.Value.Bytes);
        var plan = ModUpdate.Rebase(mod, now, StreamedAt, loose: loose);
        string All() => string.Join(", ", plan.Carried.Select(c => $"{c.Source}->{c.Target} {c.How} {c.LoosePath}")) + " | " +
                        string.Join(" / ", plan.Refused.Select(r => $"{r.Kind}:{r.Target}/{r.Source} {r.Why}"));
        Check("L1: the line follows Sound 11 to the file it streams today, and nothing lands on Sound 19's file",
              plan.Carried.Where(c => c.Source == 100).ToList() is [{ Target: 101, LoosePath: English + "101.wem" } c1] &&
              c1.Anchor == ModUpdate.SameSound && c1.Bytes.AsSpan().SequenceEqual(line) &&
              plan.Carried.All(c => c.Target != 100 && c.LoosePath != English + "100.wem"), All());
        Check("L2: a line the game starts from the bank and streams the rest of is written in both places",
              plan.Carried.Where(c => c.Source == 120).OrderBy(c => c.LoosePath is not null).ToList() is
                  [{ Target: 121, LoosePath: null } start, { Target: 121, LoosePath: English + "121.wem" } rest] &&
              start.Bytes.AsSpan().SequenceEqual(whole.AsSpan(0, 80)) && rest.Bytes.AsSpan().SequenceEqual(whole) &&
              plan.Refused.Count == 0, All());

        var root = Path.Combine(Path.GetTempPath(), "XzoundWave", "updatetest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var w = ModUpdate.Write(plan, now, GamePath, root, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            byte[] OnDisk(string at)
            {
                var f = Path.Combine(root, at.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(f) ? File.ReadAllBytes(f) : null;
            }
            var written = w.BankPath is null ? now : File.ReadAllBytes(Path.Combine(root, w.BankPath));
            Check("the author's line is on disk at Sound 11's file, and Sound 19's file is not written",
                  OnDisk(English + "101.wem")?.AsSpan().SequenceEqual(line) == true && OnDisk(English + "100.wem") is null,
                  string.Join(", ", w.Outputs));
            var audit = ModUpdate.AuditPlacement(plan, mod, now, written, OnDisk, null, loose);
            Check("the audit counts every loose carry and verifies each on the Sound it was written for",
                  audit.Clean && audit.Lines == 2 && audit.Verified == 2 && audit.Inferred == 0,
                  audit.Line + " " + string.Join("; ", audit.Problems));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// A mod given as a folder reads both ways a person hands one over: laid out as the game lays
    /// its files out, so each bank's path is where the game loads it from, and as loose banks at
    /// the top of the folder, which never said where they go, the same as a bare .bnk. Mounted as
    /// a container folder, the first read nothing at all and the second named its banks by the
    /// folder, a path the game does not have.
    /// </summary>
    private static void FolderInput(Action<string> W, Action<string, bool, string> Check, byte[] bank)
    {
        W("");
        W("a mod given as a folder");
        var root = Path.Combine(Path.GetTempPath(), "XzoundWave", "updatetest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            void Put(string at, byte[] bytes)
            {
                var file = Path.Combine(root, at.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, bytes);
            }
            Put("laid_out/" + GamePath, bank);
            Put("laid_out/" + Media700, Wem(0x70));
            Put("loose/bnk_test_top.bnk", bank);

            ModBank.Opened Opened(string folder)
            {
                try { return ModBank.Open(Path.Combine(root, folder), null, withMedia: true); }
                catch (Exception ex) { W($"        {folder}: {ex.Message.Split('\n')[0]}"); return null; }
            }
            var laidOut = Opened("laid_out");
            Check("F: a folder laid out as the game's files are gives each bank and loose medium its game path",
                  laidOut is not null && laidOut.Banks.SingleOrDefault() is { Name: "bnk_test_update.bnk", MountedAt: GamePath } &&
                  laidOut.Media.SingleOrDefault()?.MountedAt == Media700,
                  laidOut is null ? "(nothing opened)" : string.Join(", ", laidOut.Banks.Select(b => b.MountedAt ?? "(no path)")));
            var loose = Opened("loose");
            Check("F: a bank at the top of a folder is one that named no path, as a bare .bnk is",
                  loose is not null && loose.Banks.SingleOrDefault() is { Name: "bnk_test_top.bnk", MountedAt: null } b &&
                  b.Bytes.AsSpan().SequenceEqual(bank),
                  loose is null ? "(nothing opened)" : string.Join(", ", loose.Banks.Select(x => x.MountedAt ?? "(no path)")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>A wem just real enough to be inspected: RIFF, a fmt chunk, one marker byte.</summary>
    private static byte[] Wem(byte mark, int length = 40)
    {
        var data = new byte[length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        Encoding.ASCII.GetBytes("WAVE").CopyTo(data, 8);
        // The marker sits on the "WAVE" tag's first byte, which no check here reads as a
        // tag, so one byte tells every assertion above which copy it is holding.
        data[8] = mark;
        Encoding.ASCII.GetBytes("fmt ").CopyTo(data, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 0xFFFF);   // VORBIS
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 1);        // mono
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 48000);
        for (var i = 40; i < length; i++) data[i] = (byte)(i * 7 + mark);
        return data;
    }

    private const byte RandomKind = 0x05;
    private const byte SwitchKind = 0x06;
    private const byte MixerKind = 0x07;
    private const byte LayerKind = 0x09;

    /// <summary>
    /// Each Sound's declared in-memory size as Wwise writes it for audio it built: the size of
    /// the medium the Sound plays. The game's own banks all read this way, so a current bank in
    /// a fixture does too, or every fallback would be refused for want of a fingerprint.
    /// </summary>
    private static Dictionary<uint, uint> AsBuilt((uint Id, byte[] Wem)[] media, (uint Id, uint Source)[] sounds) =>
        sounds.Where(s => media.Any(m => m.Id == s.Source))
              .ToDictionary(s => s.Id, s => (uint)media.First(m => m.Id == s.Source).Wem.Length);

    /// <summary>
    /// A .bnk with a header, a DIDX, a DATA and a HIRC of Events, their Play actions, Sounds,
    /// containers and music tracks, each written the way a version 145 bank writes it, so
    /// the hierarchy parser the index and the update's line walk both use reads every object
    /// here as it reads the game's. A Sound's body starts as the game's do (id, plugin, stream
    /// type, source id, in-memory size, source bits) and goes on with the inherited block that
    /// holds its parent container. That block's bus id is nonsense on purpose: nothing here
    /// reads it, and a header reader that strays past the in-memory size lands on it and
    /// fails here.
    /// </summary>
    /// <param name="declared">
    /// Each Sound's declared in-memory size, by Sound id. Left out it is 0, which Wwise never
    /// writes for a medium that has bytes, so every medium reads as one swapped by hand
    /// unless a check says otherwise.
    /// </param>
    /// <param name="plugins">Each Sound's plugin, by Sound id; the Vorbis codec when left out.</param>
    /// <param name="streams">Each Sound's stream type, by Sound id; 0, all in the bank, when left out.</param>
    /// <param name="parents">The container each Sound sits in directly, by Sound id; none when left out.</param>
    /// <param name="containers">Containers: id, kind (random, switch, actor-mixer or layer), parent and children.</param>
    /// <param name="plays">What each event plays, by event id: one Play action per object named.</param>
    /// <param name="tracks">Music tracks and the media each plays.</param>
    /// <param name="switches">
    /// For each switch container, by id, its switch group and the children each switch state
    /// plays. A switch container left out of this has no states at all.
    /// </param>
    /// <param name="stops">What each event stops, by event id: one Stop action per object named, after its Play actions.</param>
    private static byte[] Bank(uint[] events, (uint Id, byte[] Wem)[] media, (uint Id, uint Source)[] sounds,
                               Dictionary<uint, uint> declared = null, Dictionary<uint, uint> plugins = null,
                               Dictionary<uint, byte> streams = null, Dictionary<uint, uint> parents = null,
                               (uint Id, byte Kind, uint Parent, uint[] Children)[] containers = null,
                               Dictionary<uint, uint[]> plays = null, (uint Id, uint[] Sources)[] tracks = null,
                               Dictionary<uint, (uint Group, Dictionary<uint, uint[]> States)> switches = null,
                               Dictionary<uint, uint[]> stops = null)
    {
        containers ??= [];
        tracks ??= [];
        const int Align = 16;
        var didx = new byte[media.Length * 12];
        var data = new MemoryStream();
        for (var i = 0; i < media.Length; i++)
        {
            while (data.Length % Align != 0) data.WriteByte(0);
            var offset = (uint)data.Length;
            data.Write(media[i].Wem, 0, media[i].Wem.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12), media[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12 + 4), offset);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12 + 8), (uint)media[i].Wem.Length);
        }

        var hirc = new MemoryStream();
        void U32(Stream s, uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            s.Write(b);
        }
        void U16(Stream s, ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            s.Write(b);
        }
        void Object(byte type, MemoryStream body)
        {
            hirc.WriteByte(type);
            U32(hirc, (uint)body.Length);
            body.WriteTo(hirc);
        }
        // The block every Sound, container and track inherits, empty but for its parent.
        void Inherited(Stream b, uint parent)
        {
            b.WriteByte(0);                 // override the parent's effects
            b.WriteByte(0);                 // no effects
            b.WriteByte(0);                 // override the parent's metadata
            b.WriteByte(0);                 // no metadata effects
            b.WriteByte(0);                 // override attachment params
            U32(b, 0xEEEEEEEE);             // bus id, nonsense on purpose
            U32(b, parent);                 // the container it sits in directly
            b.WriteByte(0);                 // priority and MIDI flags
            b.WriteByte(0);                 // no properties
            b.WriteByte(0);                 // no ranged properties
            b.WriteByte(0);                 // no positioning
            b.WriteByte(0);                 // no aux sends
            U32(b, 0);                      // reflections bus
            b.WriteByte(0); b.WriteByte(0); U16(b, 0); b.WriteByte(0); b.WriteByte(0);   // advanced settings
            b.WriteByte(0);                 // no state properties
            b.WriteByte(0);                 // no state groups
            U16(b, 0);                      // no runtime curves
        }
        void Source(Stream b, uint plugin, byte stream, uint id, uint size)
        {
            U32(b, plugin);
            b.WriteByte(stream);
            U32(b, id);
            U32(b, size);                   // in-memory size
            b.WriteByte(0);                 // source bits
        }

        var actions = new List<(uint Id, uint Target, bool Stop)>();
        var eventActions = new Dictionary<uint, List<uint>>();
        foreach (var e in events)
        {
            eventActions[e] = [];
            foreach (var (target, stop) in (plays?.GetValueOrDefault(e) ?? []).Select(t => (t, false))
                                           .Concat((stops?.GetValueOrDefault(e) ?? []).Select(t => (t, true))))
            {
                var actionId = 0x70000000u + (uint)actions.Count;
                actions.Add((actionId, target, stop));
                eventActions[e].Add(actionId);
            }
        }

        U32(hirc, (uint)(sounds.Length + containers.Length + tracks.Length + actions.Count + events.Length));
        foreach (var (id, source) in sounds)
        {
            var body = new MemoryStream();
            U32(body, id);
            Source(body, plugins is not null && plugins.TryGetValue(id, out var plugin) ? plugin : 0x00040001u,  // Vorbis codec
                   streams?.GetValueOrDefault(id) ?? 0, source, declared?.GetValueOrDefault(id) ?? 0);
            Inherited(body, parents?.GetValueOrDefault(id) ?? 0);
            Object(0x02, body);
        }
        foreach (var (id, kind, parent, children) in containers)
        {
            var body = new MemoryStream();
            U32(body, id);
            Inherited(body, parent);
            if (kind == RandomKind)
            {
                U16(body, 1); U16(body, 0); U16(body, 0);                  // loop once
                U32(body, 0); U32(body, 0); U32(body, 0);                  // no transition
                U16(body, 0);                                              // avoid no repeats
                body.WriteByte(0); body.WriteByte(0); body.WriteByte(0);   // transition, random mode, random
                body.WriteByte(0);                                         // playlist flags
            }
            var (group, states) = switches is not null && switches.TryGetValue(id, out var sw)
                ? sw : (0u, new Dictionary<uint, uint[]>());
            if (kind == SwitchKind)
            {
                body.WriteByte(0);                                         // a switch group, not a state group
                U32(body, group);
                U32(body, 0);                                              // no default switch
                body.WriteByte(0);                                         // not continuous
            }
            U32(body, (uint)children.Length);
            foreach (var child in children) U32(body, child);
            if (kind == RandomKind)
            {
                U16(body, (ushort)children.Length);
                foreach (var child in children) { U32(body, child); U32(body, 50000); }   // even weights
            }
            if (kind == LayerKind)
            {
                U32(body, 0);                                              // no layers
                body.WriteByte(0);                                         // not continuous
            }
            if (kind == SwitchKind)
            {
                // Which children each switch state plays, then each child's own switch params.
                U32(body, (uint)states.Count);
                foreach (var (state, nodes) in states.OrderBy(kv => kv.Key))
                {
                    U32(body, state);
                    U32(body, (uint)nodes.Length);
                    foreach (var n in nodes) U32(body, n);
                }
                U32(body, (uint)children.Length);
                foreach (var child in children)
                {
                    U32(body, child);
                    body.WriteByte(0); body.WriteByte(0);                  // first only, continue playback
                    U32(body, 0); U32(body, 0);                            // fade out, fade in
                }
            }
            Object(kind, body);
        }
        foreach (var (id, sources) in tracks)
        {
            var body = new MemoryStream();
            U32(body, id);
            body.WriteByte(0);              // track flags
            U32(body, (uint)sources.Length);
            foreach (var s in sources) Source(body, 0x00040001u, 0, s, 0);
            U32(body, 0);                   // no playlist
            U32(body, 0);                   // no clip automation
            Inherited(body, 0);
            body.WriteByte(0);              // a normal track
            U32(body, 0);                   // look-ahead time
            Object(0x0B, body);
        }
        foreach (var (id, target, stop) in actions)
        {
            var body = new MemoryStream();
            U32(body, id);
            body.WriteByte(0x03);           // on the game object
            body.WriteByte(stop ? (byte)0x01 : (byte)0x04);   // stop, or play
            U32(body, target);
            body.WriteByte(0);              // not a bus
            body.WriteByte(0);              // no properties
            body.WriteByte(0);              // no ranged properties
            body.WriteByte(0x04);           // fade curve
            if (stop)
            {
                body.WriteByte(0);          // stop's own flags
                body.WriteByte(0);          // no exceptions
            }
            else
            {
                U32(body, 0);               // bank id
                U32(body, 0);               // bank type
            }
            Object(0x03, body);
        }
        foreach (var id in events)
        {
            var body = new MemoryStream();
            U32(body, id);
            body.WriteByte((byte)eventActions[id].Count);
            foreach (var a in eventActions[id]) U32(body, a);
            Object(0x04, body);
        }

        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 145);   // the version the game's banks are at

        var bnk = new MemoryStream();
        void Section(string tag, byte[] body)
        {
            bnk.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
            U32(bnk, (uint)body.Length);
            bnk.Write(body, 0, body.Length);
        }
        Section("BKHD", header);
        Section("DIDX", didx);
        Section("DATA", data.ToArray());
        Section("HIRC", hirc.ToArray());
        return bnk.ToArray();
    }

    private static byte[] Section(byte[] bnk, string tag) =>
        BnkBuilder.ReadSections(bnk).FirstOrDefault(s => s.Tag == tag)?.Body ?? [];
}
