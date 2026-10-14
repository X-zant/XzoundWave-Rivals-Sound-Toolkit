using System.Reflection;

namespace MRAudioKit;

/// <summary>
/// Everything the tool can do without a window.
///
/// These used to be forty-odd copies of the same six lines inside a WPF Application's
/// startup, which had three costs. A verb could only run from a build that had a
/// message pump, so none of them could run on a platform without one. Adding a verb
/// meant copying a block and changing three names in it. And a verb invoked with too
/// few arguments matched nothing, fell out of the bottom, and silently opened the main
/// window instead of saying what was missing -- which is a confusing way to learn you
/// mistyped a skin id.
///
/// One table fixes all three. The bodies are unchanged: each entry reads its own
/// arguments exactly as its block did, because the shapes genuinely differ -- some take
/// a list until the next flag, some take optional trailing values, some read a flag
/// from anywhere in the line.
/// </summary>
public static class Verbs
{
    /// <param name="Needs">
    /// Arguments that must follow the verb. Below it, the verb reports what it wanted
    /// rather than the host falling through to something else entirely.
    /// </param>
    /// <param name="Run">Given the whole command line and where the verb appeared.</param>
    public sealed record Verb(string Name, int Needs, string Usage, string What,
                              Func<string[], int, int> Run)
    {
        public string[] Aliases { get; init; } = [];
        public bool Matches(string arg) =>
            arg.Equals(Name, StringComparison.OrdinalIgnoreCase) ||
            Aliases.Any(a => arg.Equals(a, StringComparison.OrdinalIgnoreCase));
    }

    private static string Arg(string[] a, int i) => i < a.Length ? a[i] : null;

    private static int OrganizeVerb(string input, string output, string rules)
    {
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });
        var r = WemOrganizer.Run(session, input, rules, output, Console.WriteLine);
        Console.WriteLine(r.Report);
        return 0;
    }
    private static bool Flag(string[] a, string name) =>
        a.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The value after a named flag, wherever it appears on the line.</summary>
    private static string After(string[] a, string name)
    {
        var i = Array.FindIndex(a, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? Arg(a, i + 1) : null;
    }

    private static int ListVoiceSets()
    {
        var session = new GameSession();
        session.Mount(Settings.Load(), _ => { });
        var sets = VoiceSets.Discover(session);
        foreach (var s in sets)
            Console.WriteLine($"{s.HeroId}  {s.Label,-40}  {Path.GetFileName(s.TablePackage)} / {string.Join(", ", s.ItemPackages.Select(Path.GetFileName))}  ({s.SkinIds.Count} skin{(s.SkinIds.Count == 1 ? "" : "s")}, sculpts {string.Join("/", s.Sculpts)})");
        Console.WriteLine($"{sets.Count} voice sets, {sets.Count(s => !s.IsDefault)} of them a skin's own");
        return 0;
    }

    /// <summary>What the voice-lines window offers for one set: each trigger's entries, keyed, with a line of each.</summary>
    private static int ListVoiceEntries(string which)
    {
        var session = new GameSession();
        session.Mount(Settings.Load(), _ => { });
        var sets = VoiceSets.Discover(session);
        var set = sets.FirstOrDefault(s => s.IsDefault && s.HeroId == which)
                  ?? sets.FirstOrDefault(s => s.Label.Equals(which, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"no voice set {which}");
        var subs = VoiceTriggers.Subtitles(session, set);
        Console.WriteLine($"{set.Label}: {set.TablePackage} ({subs.Count} lines)");
        // Every form's VoiceItem: a hero with several forms keeps one per form, and they differ.
        foreach (var item in set.ItemPackages)
        foreach (var t in VoiceTriggers.All)
        {
            var entries = VoiceTriggers.Entries(session, set, t, item);
            Console.WriteLine($"  {Path.GetFileName(item)} {t.Field} ({t.Name}): {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}");
            foreach (var e in entries.Take(12))
                Console.WriteLine($"    [{e.Index}] {string.Join(", ", e.Keys.Select(k => $"{k.Key}={k.Value}"))}  {e.Voices.Count} line(s)" +
                                  (e.Voices.Count > 0 ? $"  \"{subs.GetValueOrDefault(e.Voices[0])}\"" : ""));
        }
        return 0;
    }

    public static readonly IReadOnlyList<Verb> All =
    [
        // ---- what a person would actually run -----------------------------
        new("--build", 3, "<skinId> <wemDir> <outDir> [--prefetch]",
            "build a mod from a folder of named wems",
            (a, i) => SelfTest.BuildMod(a[i + 1], a[i + 2], a[i + 3], Flag(a, "--prefetch"))),

        new("--testbank", 3, "<skinId> <clipDir> <outDir> [bankFilter]",
            "numbered test .bnk, so a sound says which one it is",
            (a, i) => SelfTest.TestBank(a[i + 1], a[i + 2], a[i + 3], Arg(a, i + 4))),

        new("--volume", 3, "<skinId> <wemDir> <outDir> [gain]",
            "rebuild at a different level",
            (a, i) => SelfTest.Volume(a[i + 1], a[i + 2], a[i + 3], Arg(a, i + 4))),

        new("--import", 2, "<file> <outWem>", "any supported audio -> wem",
            (a, i) => SelfTest.Import(a[i + 1], a[i + 2])),

        new("--recompress", 2, "<bnk|wem|pak|folder> <outDir>", "re-encode PCM audio to Vorbis: a .bnk, loose .wem files, or a mod .pak",
            (a, i) => SelfTest.Recompress(a[i + 1], a[i + 2])),

        new("--merge", 3, "<modA> <modB> <outDir>", "combine two mods; A wins collisions",
            (a, i) => SelfTest.Merge(a[i + 1], a[i + 2], a[i + 3])),

        // A mod that ships a whole .bnk puts the old bank back after every patch. This
        // rebuilds the game's current bank with only the mod's audio carried onto it.
        new("--updatemod", 2, $"<mod.pak|mod.bnk|folder> <outDir> [{ModUpdate.KeepWwiseBuiltFlag}] [--made-for <version>] [--history-dir <dir>]",
            "carry an old mod's audio onto the game's current banks",
            (a, i) => ModUpdate.Run(a[i + 1], a[i + 2], Flag(a, ModUpdate.KeepWwiseBuiltFlag),
                                    madeFor: After(a, "--made-for"), historyDir: After(a, "--history-dir"))),

        new("--organizewems", 2, "<inputDir> <outDir> [0-WEM-FOLDER-NAME-LIST.txt]",
            "copy a folder of wems into one folder per mod: by a SoundKit rule list, or by character, skin and bank",
            (a, i) => OrganizeVerb(a[i + 1], a[i + 2], Arg(a, i + 3) is { } r && !r.StartsWith("--") ? r : null)),

        new("--snapshot", 0, "[paksDir] [--history-dir <dir>]",
            "record which sound plays every media id in this version of the game (or the one in paksDir)",
            (a, i) => HistoryVerbs.Snapshot(Arg(a, i + 1) is { } d && !d.StartsWith("--") ? d : null, After(a, "--history-dir"))),

        new("--history", 0, "[dir]", "the game versions and SoundKit lists an update can read old media ids against",
            (a, i) => HistoryVerbs.List(Arg(a, i + 1))),

        new("--importsubs", 1, "<soundKitFolder> [--history-dir <dir>]", "copy SoundKit's old subtitle lists into the history",
            (a, i) => HistoryVerbs.ImportSubs(a[i + 1], After(a, "--history-dir"))),

        new("--openbank", 1, "<bnk|pak> [rows]", "diff someone else's .bnk against the game",
            (a, i) => SelfTest.OpenBank(a[i + 1],
                int.TryParse(Arg(a, i + 2), out var n) ? n : 8)),

        new("--media", 1, "<id> [<id>…] [--bank <name>] [--out <file>]",
            "where a media lives, and how",
            (a, i) => SelfTest.Media(
                a.Skip(i + 1).TakeWhile(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray(),
                After(a, "--bank"), After(a, "--out"))),

        new("--banks", 0, "", "every .bnk in the game, grouped", (a, i) => SelfTest.Banks()),
        new("--medialayout", 0, "", "loose-media layout and overlap", (a, i) => MediaLayout.Run()),
        new("--usmap", 0, "[--force]", "fetch the newest mappings from the depot",
            (a, i) => UsmapFetch.Run(Flag(a, "--force"))),
        new("--doctor", 0, "", "what this machine provides, and what is wrong",
            (a, i) => EnvCheck.Run()),
        new("--licences", 0, "", "every notice compiled into the binary",
            (a, i) => Bundled.PrintLicences()) { Aliases = ["--licenses"] },
        new("--tts", 3, "<outDir> <first> <last>", "generate spoken numbers",
            (a, i) => SelfTest.TtsTest(a[i + 1], int.Parse(a[i + 2]), int.Parse(a[i + 3]))),
        new("--numbers", 0, "", "spoken numbers compose from word clips",
            (a, i) => NumberSpeech.SelfTest()),
        // Run once, on Windows, by whoever is preparing a release: it is the only way
        // to produce the clips, and once they are recorded and embedded every platform
        // can speak a number without a voice of its own.
        new("--numberclips", 1, "<outDir>", "record the spoken word clips (Windows, once)",
            (a, i) => NumberSpeech.RecordWordClips(a[i + 1])),
        new("--translate", 1, "<skinId> [--fetch]", "translate the category column",
            (a, i) => SelfTest.TranslateCategories(a[i + 1], Flag(a, "--fetch"))),

        // ---- self-tests, each printing ALL CHECKS PASSED or a numbered failure ----
        new("--selftest", 1, "<skinId> [report.txt]", "the full pipeline check",
            (a, i) => SelfTest.Run(a[i + 1], Arg(a, i + 2))),
        new("--seltest", 2, "<skinId> <wemDir> [count]", "selection-driven build",
            (a, i) => SelectionSelfTest.Run(a[i + 1], a[i + 2],
                int.TryParse(Arg(a, i + 3), out var n) ? n : 6)),
        new("--bulktest", 4, "<a> <b> <c> <d>", "bulk build across skins",
            (a, i) => BulkSelfTest.Run(a[i + 1], a[i + 2], a[i + 3], a[i + 4])),
        new("--mutetest", 1, "<skinId>", "a mute silences the sound in every build",
            (a, i) => MuteSelfTest.Run(a[i + 1])),
        new("--noskinbuild", 2, "<skinIdA> <skinIdB>", "staged files build without a skin",
            (a, i) => NoSkinBuildTest.Run(a[i + 1], a[i + 2])),
        new("--cachetest", 1, "<file>", "conversions are remembered",
            (a, i) => CacheSelfTest.Run(a[i + 1])),
        new("--roundtrip", 2, "<bnk> <outDir>", "extract and rebuild gives the same bytes",
            (a, i) => RoundTripTest.Run(a[i + 1], a[i + 2])),
        new("--fmtcheck", 1, "<dir>", "what format a folder of wems is in",
            (a, i) => FormatCheck.Run(a[i + 1])),
        new("--sorting", 1, "<skinId>", "columns sort by the number, not the text",
            (a, i) => SortingSelfTest.Run(a[i + 1])),
        new("--ingame", 0, "", "the .bnk's volume, pitch, delay and loop are applied",
            (a, i) => InGameChainTest.Run()),
        new("--applied", 0, "", "what the .bnk does to a sound, against the game",
            (a, i) => AppliedPropsTest.Run()),
        new("--variations", 0, "", "a set of alternatives is what its container holds",
            (a, i) => VariationSetSelfTest.Run()),
        new("--prefetchpreview", 1, "<skinId>", "a streamed line previews in full",
            (a, i) => PrefetchPreviewTest.Run(a[i + 1])),
        new("--modset", 0, "", "several mods at once, and building on them",
            (a, i) => ModSetSelfTest.Run()),
        new("--updatetest", 0, "", "an old mod's audio lands on the current bank, and only where it should",
            (a, i) => ModUpdateSelfTest.Run()),
        new("--voicesets", 0, "", "every voice set: each hero's, and the skins that carry their own",
            (a, i) => ListVoiceSets()),
        new("--voicelinecheck", 2, "<project.xzw> <modDir> [swappedMediaId]", "a built voice-line mod against its project, read the way the game reads it",
            (a, i) => VoiceLineCheck.Run(a[i + 1], a[i + 2], uint.TryParse(Arg(a, i + 3), out var sw) ? sw : 0)),
        new("--voiceentries", 1, "<heroId|set label>", "each trigger's entries in one voice set, as the voice-lines window lists them",
            (a, i) => ListVoiceEntries(a[i + 1])),
        new("--voicelinetest", 2, "<audio> <outDir>", "a new voice line, built, packaged and read back as the game would",
            (a, i) => VoiceLineSelfTest.Run(a[i + 1], a[i + 2])),
        new("--openorder", 1, "<bnk>", "opening a mod before the game is loaded",
            (a, i) => OpenOrderSelfTest.Run(a[i + 1])),
        new("--platform", 0, "", "the filesystem rules this host actually follows",
            (a, i) => PlatformSelfTest.Run()),
        new("--culture", 0, "", "the machine's locale stays out of the files it writes",
            (a, i) => CultureSelfTest.Run()),
        new("--pipeaudio", 0, "", "sound through a system player, on a host with no device",
            (a, i) => PipeAudioSelfTest.Run()),
        new("--decode", 0, "", "which audio formats import, and by which route",
            (a, i) => AudioDecodeSelfTest.Run()),
        new("--rowfilters", 0, "", "hiding a kind of row removes only those rows",
            (a, i) => RowFilterSelfTest.Run()),
        new("--music", 0, "", "music media are rows, named and timed",
            (a, i) => MusicSelfTest.Run()),
        new("--tags", 0, "", "the tag vocabulary", (a, i) => TagSelfTest.Run()),
        new("--mediapath", 0, "[count]", "where loose media lives",
            (a, i) => MediaPathSelfTest.Run(int.TryParse(Arg(a, i + 1), out var n) ? n : 400)),
        new("--playtest", 0, "[mediaId]", "decode a real sound and play it",
            (a, i) => EnvCheck.PlayTest(uint.TryParse(Arg(a, i + 1), out var id) ? id : 1641578u)),
        new("--vorbistest", 2, "<wav> <outWem>", "the encoder against a known clip",
            (a, i) => SelfTest.VorbisTest(a[i + 1], a[i + 2])),
        new("--wav2wem", 2, "<wav> <outWem>", "encode one wav",
            (a, i) => SelfTest.WavToWem(a[i + 1], a[i + 2])),
        new("--setuproundtrip", 1, "<bnk>", "the Vorbis setup packet survives a rewrite",
            (a, i) => SelfTest.SetupRoundTrip(a[i + 1])),

        // ---- probes, for working out what the game is doing ----------------
        new("--wwiseprobe", 1, "<skinId> [event|?]", "what a .bnk says beyond the audio",
            (a, i) => WwiseProbe.Run(a[i + 1], Arg(a, i + 2))),
        new("--mountdiag", 1, "<paksDir>", "why the paks did or did not mount",
            (a, i) => MountDiag.Run(a[i + 1])),
        new("--dumpmedia", 2, "<mediaId> <outWem>", "write out one loose media file",
            (a, i) => SelfTest.DumpMedia(a[i + 1], a[i + 2])),
        new("--subprobe", 1, "<skinId>", "where subtitles come from",
            (a, i) => SubtitleProbe.Run(a[i + 1])),
        new("--catprobe", 1, "<skinId>", "where categories come from",
            (a, i) => CategoryProbe.Run(a[i + 1])),
        new("--extstats", 0, "[dir]", "what file types are in there",
            (a, i) => ExtStats.Run(Arg(a, i + 1))),
        new("--streamonly", 0, "", "media that only ever stream",
            (a, i) => SelfTest.StreamOnly()),
        new("--bankmax", 0, "", "the largest .bnk files", (a, i) => SelfTest.BankMax()),
    ];

    /// <summary>
    /// Run whichever verb the command line names.
    /// </summary>
    /// <returns>
    /// False when no verb was named, which means the caller should carry on and show a
    /// window. True when one ran -- or when one was named but could not run, because
    /// saying so and stopping is the useful answer, not opening the app.
    /// </returns>
    public static bool Dispatch(string[] args, out int exit)
    {
        exit = 0;
        if (args is null || args.Length == 0) return false;

        if (args.Any(a => a is "--help" or "-h" or "-?" or "/?"))
        {
            PrintHelp();
            return true;
        }

        foreach (var verb in All)
        {
            var at = Array.FindIndex(args, verb.Matches);
            if (at < 0) continue;

            // Counted from the verb, not from the end of the line: a flag before it
            // must not look like one of its arguments.
            if (args.Length - at - 1 < verb.Needs)
            {
                Console.Error.WriteLine($"{verb.Name} needs {verb.Needs} argument(s).");
                Console.Error.WriteLine($"  usage: {verb.Name} {verb.Usage}");
                exit = 2;
                return true;
            }

            try { exit = verb.Run(args, at); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{verb.Name} failed: {ex.Message}");
                exit = 1;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// The assembly that holds Window in each of the two UI frameworks this repository
    /// has ever shipped a window with. Named rather than matched on a prefix, so a
    /// reference to something that merely ships in the same package family as one of
    /// them does not read as a window.
    /// </summary>
    private static readonly string[] WindowingAssemblies =
        ["Avalonia.Controls", "PresentationFramework"];

    /// <summary>
    /// Whether the host that started this process can put a window on screen.
    ///
    /// Asked of the host rather than passed in by it. There are three hosts -- the
    /// headless XzoundCli, the Avalonia window, and the WPF window that is still in the
    /// tree -- and their entry points are in three separate projects, so a parameter on
    /// PrintHelp or a flag set at startup would have to be added to all three and would
    /// be wrong for whichever one was missed. The entry assembly's reference list answers
    /// it without any of them saying anything, and it is the same property the assembly
    /// split already guarantees: XzoundCli references XzoundLogic and nothing that draws,
    /// which is the entire reason that project exists. It also survives the way these
    /// binaries actually ship, self-contained and single-file, where Assembly.Location
    /// comes back empty but the reference table does not.
    /// </summary>
    private static bool HostCanOpenAWindow =>
        Assembly.GetEntryAssembly()?.GetReferencedAssemblies()
                .Any(a => WindowingAssemblies.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
        ?? false;

    public static void PrintHelp()
    {
        Console.WriteLine("XzoundWave — a sound toolkit for Marvel Rivals.");
        // The headless host prints this list when it is run with no arguments at all, so
        // an unconditional "run with no arguments to open the window" was refuted by the
        // output it was sitting in -- and that host contains no windowing code to obey it.
        Console.WriteLine(HostCanOpenAWindow
            ? "Run with no arguments to open the window."
            : "Run with no arguments for this list.");
        Console.WriteLine();
        var width = All.Max(v => v.Name.Length + v.Usage.Length) + 3;
        foreach (var v in All)
        {
            var left = $"{v.Name} {v.Usage}".TrimEnd();
            Console.WriteLine($"  {left.PadRight(width)}{v.What}");
        }
    }
}
