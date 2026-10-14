using System.Buffers.Binary;
using System.IO;

using Xzound;

namespace MRAudioKit;

/// <summary>
/// Spoken numbers built out of about thirty recorded words, so a numbered test bank
/// means the same thing on every platform.
///
/// The old path spoke each number through Windows SAPI, which exists nowhere else, so
/// the whole feature was Windows only. The wording is what has to be kept, not the
/// speech engine: a number said as a number carries its own magnitude, where digit by
/// digit does not. Sounds overlap in game, and a clipped or half heard "one zero two"
/// reads equally well as "one zero" or "zero two", while "one hundred forty two" cannot
/// be mistaken for "forty two". So the words stay and only the recording moves: the
/// thirty words are recorded once, and every number is composed from them.
///
/// Thirty clips cover every number with no upper bound, because the vocabulary is
/// positional. Clips are recorded on Windows by <see cref="RecordWordClips"/> and then
/// checked in; at runtime they are read from an embedded resource, or from a folder.
/// </summary>
public static class NumberSpeech
{
    // Indexed by value: Ones[15] is the word for fifteen. The teens are irregular in
    // English and are listed rather than composed, which is why this runs to twenty.
    private static readonly string[] Ones =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
        "seventeen", "eighteen", "nineteen",
    ];

    // Indexed by the tens digit, so Tens[4] is forty. Below two there is no such word.
    private static readonly string[] Tens =
    [
        null, null, "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety",
    ];

    private const string Hundred = "hundred";
    private const string Thousand = "thousand";

    /// <summary>Every word a composed number can use, and so every clip needed.</summary>
    public static readonly IReadOnlyList<string> Vocabulary =
        [.. Ones, .. Tens.Where(t => t is not null), Hundred, Thousand];

    /// <summary>Silence between words. Long enough to hear a seam, short enough that a
    /// four word number still fits inside a sound that may be cut short.</summary>
    public const int GapMs = 60;

    /// <summary>Silence after the last word, so the final consonant is not flush against
    /// the end of the clip where playback stops.</summary>
    public const int LeadOutMs = 60;

    /// <summary>Kept either side of a word when trimming, so a soft onset survives.</summary>
    public const int MarginMs = 15;

    // A recorded clip's "silence" is room tone and dither, not zeroes. This is roughly
    // -42 dBFS, below anything the voice produces and above what the pad contains.
    private const int SilenceLevel = 256;

    /// <summary>Where recorded clips live when they are not embedded.</summary>
    public static string DefaultClipDir => Path.Combine(Settings.AppDataDir, "numberwords");

    /// <summary>The embedded name of one word's clip. See the note on RecordWordClips
    /// for the csproj line that puts them there.</summary>
    public static string ResourceName(string word) => $"XzoundWave.numbers.{word}.wav";

    // ---- numbers into words -------------------------------------------------

    /// <summary>
    /// The words for a number, most significant first: 142 gives one, hundred, forty, two.
    /// </summary>
    public static List<string> ToWords(int n)
    {
        if (n < 0)
            throw new ArgumentOutOfRangeException(nameof(n),
                "spoken numbers name entries in a bank, which are never negative");

        // Zero is the one value with no group to speak, so it is said outright.
        if (n == 0) return [Ones[0]];

        var words = new List<string>();
        Groups(words, n);
        return words;
    }

    private static void Groups(List<string> into, long n)
    {
        // The vocabulary stops at thousand, so larger scales repeat it: a million is
        // "one thousand thousand". Odd to read, but it still carries magnitude, which
        // is the whole reason for speaking words instead of digits. Nothing realistic
        // reaches it; a bank with a million entries does not exist.
        if (n >= 1000)
        {
            Groups(into, n / 1000);
            into.Add(Thousand);
            n %= 1000;
        }
        if (n > 0) UnderThousand(into, (int)n);
    }

    private static void UnderThousand(List<string> into, int n)
    {
        if (n >= 100)
        {
            into.Add(Ones[n / 100]);
            into.Add(Hundred);
            n %= 100;
        }
        if (n == 0) return;
        if (n < 20) { into.Add(Ones[n]); return; }

        into.Add(Tens[n / 10]);
        if (n % 10 != 0) into.Add(Ones[n % 10]);
    }

    // ---- finding the recorded clips -----------------------------------------

    /// <summary>A place clips might be, and how to get one from it.</summary>
    private sealed record ClipSource(string Name, Func<string, bool> Has, Func<string, byte[]> Read);

    /// <summary>
    /// The clips that were found, where they came from, and which are absent.
    /// </summary>
    public sealed record ClipBank(IReadOnlyDictionary<string, WwiseWem.WavData> Clips,
                                  string Source, IReadOnlyList<string> Missing)
    {
        public bool Complete => Missing.Count == 0;

        /// <summary>One line naming the source and everything still wanted.</summary>
        public string Describe() => Complete
            ? $"{Clips.Count} word clips from {Source}"
            : $"{Clips.Count} of {Vocabulary.Count} word clips from {Source}; missing: {Absent()}";

        // Nothing found at all is the ordinary first run, and printing the whole
        // vocabulary back at the reader buries the line that says where to look.
        private string Absent() => Missing.Count <= 6
            ? string.Join(", ", Missing)
            : string.Join(", ", Missing.Take(6)) + $" and {Missing.Count - 6} more";
    }

    private static IEnumerable<ClipSource> Sources(string folder)
    {
        yield return new ClipSource(
            "the embedded clips",
            // Asking whether a resource is there hands back an open stream over the
            // assembly image, so the probe has to close what it opened.
            w =>
            {
                using var s = typeof(NumberSpeech).Assembly.GetManifestResourceStream(ResourceName(w));
                return s is not null;
            },
            w =>
            {
                using var s = typeof(NumberSpeech).Assembly.GetManifestResourceStream(ResourceName(w));
                if (s is null) return null;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            });

        foreach (var dir in FolderCandidates(folder))
            yield return new ClipSource(
                dir,
                w => File.Exists(Path.Combine(dir, w + ".wav")),
                w =>
                {
                    var p = Path.Combine(dir, w + ".wav");
                    return File.Exists(p) ? File.ReadAllBytes(p) : null;
                });
    }

    private static IEnumerable<string> FolderCandidates(string folder)
    {
        var seen = new HashSet<string>(AppPaths.Comparer);
        foreach (var dir in new[] { folder, DefaultClipDir,
                                    Path.Combine(AppContext.BaseDirectory, "numberwords") })
            if (!string.IsNullOrWhiteSpace(dir) && seen.Add(dir))
                yield return dir;
    }

    /// <summary>Whether a whole set of clips can be found, without decoding any.</summary>
    public static bool IsAvailable =>
        Sources(null).Any(src => Vocabulary.All(w => Safe(() => src.Has(w))));

    /// <summary>
    /// Load the word clips. Embedded clips win; otherwise the first folder holding a
    /// complete set. With no complete set anywhere, the fullest attempt comes back so
    /// the caller can say what is missing rather than only that something is.
    /// </summary>
    public static ClipBank LoadClips(string folder = null)
    {
        ClipBank best = null;
        var looked = new List<string>();
        foreach (var src in Sources(folder))
        {
            looked.Add(src.Name);
            var bank = Read(src);
            if (bank.Complete) return bank;
            if (best is null || bank.Clips.Count > best.Clips.Count) best = bank;
        }

        // With nothing found anywhere, naming only the first place looked reads as
        // though the others were never tried, and the reader copies clips into a
        // folder the loader was already checking.
        if (best is null || best.Clips.Count == 0)
            return new ClipBank(new Dictionary<string, WwiseWem.WavData>(),
                                "nowhere; looked in " + string.Join("; ", looked), Vocabulary);
        return best;
    }

    private static ClipBank Read(ClipSource src)
    {
        var clips = new Dictionary<string, WwiseWem.WavData>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var word in Vocabulary)
        {
            WwiseWem.WavData wav = null;
            // A clip that will not parse is as useless as one that is not there, and
            // saying "missing" of both keeps the report to one list.
            try
            {
                var bytes = src.Read(word);
                if (bytes is not null) wav = WwiseWem.ReadWav(bytes);
            }
            catch { wav = null; }

            if (wav is null) missing.Add(word); else clips[word] = wav;
        }
        return new ClipBank(clips, src.Name, missing);
    }

    private static bool Safe(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }

    // ---- words into one clip ------------------------------------------------

    /// <summary>
    /// Join the clips for these words into one, with a gap between them.
    /// </summary>
    public static WwiseWem.WavData Compose(IReadOnlyList<string> words, ClipBank bank)
    {
        ArgumentNullException.ThrowIfNull(bank);
        if (words is null || words.Count == 0)
            throw new ArgumentException("no words to speak", nameof(words));

        var rate = 0;
        short channels = 0;
        var parts = new List<byte[]>(words.Count);

        foreach (var word in words)
        {
            if (!bank.Clips.TryGetValue(word, out var clip))
                throw new InvalidOperationException(
                    $"no clip for \"{word}\" ({bank.Describe()})");
            if (clip.Bits != 16)
                throw new NotSupportedException(
                    $"the clip for \"{word}\" is {clip.Bits}-bit; 16-bit only");

            // Concatenation is raw samples, so a clip in another format cannot simply
            // be appended. Mismatch means the set was recorded in two sittings.
            if (rate == 0) { rate = clip.SampleRate; channels = clip.Channels; }
            else if (clip.SampleRate != rate || clip.Channels != channels)
                throw new InvalidOperationException(
                    $"the clip for \"{word}\" is {clip.SampleRate} Hz {clip.Channels}ch, " +
                    $"but the set is {rate} Hz {channels}ch; record them all at once");

            parts.Add(Trim(clip));
        }

        var frame = channels * 2;
        var gap = rate * GapMs / 1000 * frame;
        var leadOut = rate * LeadOutMs / 1000 * frame;

        // The gaps and the lead out are silence and a fresh array is already zero, so
        // they are room left in it rather than anything written into it.
        var pcm = new byte[parts.Sum(p => p.Length) + gap * (parts.Count - 1) + leadOut];
        var at = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) at += gap;
            Buffer.BlockCopy(parts[i], 0, pcm, at, parts[i].Length);
            at += parts[i].Length;
        }
        return new WwiseWem.WavData(rate, channels, 16, pcm);
    }

    /// <summary>
    /// Drop the silence a recording carries at each end. Without this a four word number
    /// spends most of its length saying nothing, and the pauses swamp the words.
    /// </summary>
    private static byte[] Trim(WwiseWem.WavData w)
    {
        var frame = w.Channels * 2;
        var frames = w.Pcm.Length / frame;
        if (frames == 0) return [];

        var first = 0;
        while (first < frames && Quiet(w.Pcm, first * frame, frame)) first++;
        if (first == frames) return [];        // the whole clip is silence

        var last = frames - 1;
        while (last > first && Quiet(w.Pcm, last * frame, frame)) last--;

        var margin = w.SampleRate * MarginMs / 1000;
        first = Math.Max(0, first - margin);
        last = Math.Min(frames - 1, last + margin);

        var len = (last - first + 1) * frame;
        var cut = new byte[len];
        Buffer.BlockCopy(w.Pcm, first * frame, cut, 0, len);
        return cut;
    }

    private static bool Quiet(byte[] pcm, int at, int frameBytes)
    {
        for (var i = at; i < at + frameBytes; i += 2)
            if (Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i, 2))) > SilenceLevel)
                return false;
        return true;
    }

    // ---- the numbered clips a test bank is built from -----------------------

    /// <summary>
    /// Write {first}.wem to {last}.wem plus a silent clip, in the folder the numbered
    /// test bank builder expects. The same shape as Tts.GenerateNumbers, so a caller
    /// swaps one for the other and changes nothing else.
    /// </summary>
    /// <param name="rateBoost">
    /// Ignored. The clips are recorded, so speaking rate is settled when they are made,
    /// not here. It stays in the signature so callers written against the SAPI path
    /// keep compiling.
    /// </param>
    public static int GenerateNumbers(string outDir, int first, int last,
                                      Action<string> progress, int rateBoost = 2)
    {
        progress ??= _ => { };

        var bank = LoadClips();
        if (!bank.Complete)
            throw new InvalidOperationException(
                "the spoken word clips are not available: " + bank.Describe() +
                $". Record them once on Windows with --numberclips \"{DefaultClipDir}\".");

        return WriteRange(bank, outDir, first, last, progress);
    }

    private static int WriteRange(ClipBank bank, string outDir, int first, int last,
                                  Action<string> progress)
    {
        Directory.CreateDirectory(outDir);

        var made = 0;
        var rate = 0;
        short ch = 0;

        for (var n = first; n <= last; n++)
        {
            var wav = Compose(ToWords(n), bank);
            rate = wav.SampleRate; ch = wav.Channels;

            // Wwise Vorbis, no Wwise install and no account. PCM only if the encoder
            // fails on an input we have not anticipated.
            byte[] wem;
            try { wem = XzoundCore.Encode(wav); }
            catch { wem = WwiseWem.Write(wav); }
            File.WriteAllBytes(Path.Combine(outDir, $"{n}.wem"), wem);

            made++;
            if (made % 25 == 0) progress($"composing spoken numbers… {made}/{last - first + 1}");
        }

        // A silent clip of the same format, so entries past the end of the range are
        // audibly nothing rather than leftover vanilla audio.
        if (rate > 0)
        {
            var half = new byte[rate * ch * 2 / 2];
            var sw = new WwiseWem.WavData(rate, ch, 16, half);
            byte[] silentWem;
            try { silentWem = XzoundCore.Encode(sw); }
            catch { silentWem = WwiseWem.Write(sw); }
            File.WriteAllBytes(Path.Combine(outDir, "0-Silent.wem"), silentWem);
        }

        progress($"composed {made} spoken number(s) at {rate} Hz, {ch}ch as Vorbis into {outDir} " +
                 $"({bank.Describe()})");
        return made;
    }

    // ---- what a caller should use -------------------------------------------

    /// <summary>
    /// Whether spoken numbers can be produced at all, either way.
    /// </summary>
    public static bool CanSpeak => IsAvailable || Tts.IsAvailable;

    /// <summary>
    /// Spoken numbers, composed from clips where they exist and spoken by the Windows
    /// voice where they do not.
    ///
    /// The fallback is here so the feature keeps working on Windows before the clips
    /// are recorded, and it is the only thing left holding the SAPI path: once the
    /// clips ship, every platform takes the first branch and Tts can go.
    /// </summary>
    public static int Generate(string outDir, int first, int last,
                               Action<string> progress, int rateBoost = 2)
    {
        progress ??= _ => { };

        // The choice is made on the loaded set rather than on IsAvailable, which only
        // counts file names: a clip that is present but will not decode would otherwise
        // send this down the composed path and throw, on the one platform where the
        // voice was there to fall back on. Loading costs nothing extra, because the
        // composed path reads the same clips a line later.
        var bank = LoadClips();
        return bank.Complete
            ? WriteRange(bank, outDir, first, last, progress)
            : Tts.GenerateNumbers(outDir, first, last, progress, rateBoost);
    }

    // ---- recording the clips, once, on Windows ------------------------------

    /// <summary>
    /// Speak each word of the vocabulary into a .wav in <paramref name="outDir"/>.
    ///
    /// This is the only Windows only part left, and it runs once: the clips it writes
    /// are meant to be checked in and embedded, after which no platform needs a voice.
    /// </summary>
    public static int GenerateWordClips(string outDir, Action<string> progress, int rateBoost = 0)
    {
        progress ??= _ => { };

        // The voice is Windows' own. Recording is a one off that produces files for
        // everyone else, so a host without it is not broken, just not the one that does
        // the recording.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Recording the word clips needs the Windows speech voice. " +
                "They are recorded once and checked in; take them from the repo instead.");

        Directory.CreateDirectory(outDir);

        var made = 0;
        foreach (var word in Vocabulary)
        {
            Tts.SpeakToWav(word, Path.Combine(outDir, word + ".wav"), rateBoost);
            made++;
            progress($"recorded \"{word}\" ({made}/{Vocabulary.Count})");
        }

        progress($"recorded {made} word clip(s) into {outDir}");
        return made;
    }

    /// <summary>XzoundWave.exe --numberclips outDir</summary>
    public static int RecordWordClips(string outDir)
    {
        try
        {
            var made = GenerateWordClips(outDir, Console.WriteLine);
            Console.WriteLine();
            Console.WriteLine($"{made} clip(s) written. To make them work everywhere, either");
            Console.WriteLine($"  copy them to {DefaultClipDir}, or");
            Console.WriteLine("  add them to XzoundLogic.csproj as embedded resources named " +
                              ResourceName("<word>") + ".");
            return made == Vocabulary.Count ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // ---- self-test ----------------------------------------------------------

    /// <summary>XzoundWave.exe --numbers</summary>
    public static int SelfTest()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        W($"{Vocabulary.Count} words: {string.Join(" ", Vocabulary)}");

        W("");
        W("the vocabulary");
        Check("thirty words, and no more", Vocabulary.Count == 30, $"{Vocabulary.Count}");
        Check("no word appears twice",
              Vocabulary.Distinct(StringComparer.Ordinal).Count() == Vocabulary.Count);
        // Each word is also a file name and a resource name, so anything but plain
        // lower case letters would have to be escaped somewhere.
        Check("every word is plain lower case letters",
              Vocabulary.All(w => w.Length > 0 && w.All(c => c is >= 'a' and <= 'z')));

        W("");
        W("a number becomes the words a person would say");
        void Says(int n, params string[] expected)
        {
            var got = ToWords(n);
            Check($"{n,5} is \"{string.Join(" ", expected)}\"",
                  got.SequenceEqual(expected), $"got \"{string.Join(" ", got)}\"");
        }

        Says(0, "zero");
        Says(7, "seven");
        Says(15, "fifteen");
        Says(20, "twenty");
        Says(21, "twenty", "one");
        Says(99, "ninety", "nine");
        Says(100, "one", "hundred");
        Says(101, "one", "hundred", "one");
        Says(115, "one", "hundred", "fifteen");
        Says(250, "two", "hundred", "fifty");
        Says(999, "nine", "hundred", "ninety", "nine");
        Says(1000, "one", "thousand");
        Says(1024, "one", "thousand", "twenty", "four");
        Says(9999, "nine", "thousand", "nine", "hundred", "ninety", "nine");

        W("");
        W("past the range a test bank needs");
        Says(142, "one", "hundred", "forty", "two");
        Says(10000, "ten", "thousand");
        Says(100000, "one", "hundred", "thousand");
        Says(123456, "one", "hundred", "twenty", "three", "thousand",
                     "four", "hundred", "fifty", "six");
        // No million in the vocabulary, on purpose: repeating thousand still says how
        // big the number is, which digits would not.
        Says(1000000, "one", "thousand", "thousand");

        W("");
        W("every number a bank could hold");
        var all = new List<List<string>>(10000);
        for (var n = 0; n < 10000; n++) all.Add(ToWords(n));

        Check("each says something", all.All(w => w.Count > 0));
        Check("each uses only recorded words",
              all.All(w => w.All(Vocabulary.Contains)),
              string.Join(", ", all.SelectMany(w => w).Distinct()
                                   .Where(w => !Vocabulary.Contains(w)).Take(5)));
        // The point of the whole feature: hearing a clip must name one entry.
        Check("no two numbers say the same thing",
              all.Select(w => string.Join(" ", w)).Distinct(StringComparer.Ordinal).Count() == 10000);
        Check("at most six words, so a clip stays short",
              all.Max(w => w.Count) == 6, $"longest is {all.Max(w => w.Count)} words");

        Check("a negative index is refused", Throws(() => ToWords(-1)));

        W("");
        W("joining the clips");

        // Synthetic clips, so this check needs no recording. Every sample is loud, so
        // trimming has nothing to remove and the arithmetic is exact.
        const int rate = 22050;
        const int frames = 1000;
        var bank = new ClipBank(new Dictionary<string, WwiseWem.WavData>(StringComparer.Ordinal)
        {
            ["one"] = Loud(rate, 1, frames),
            ["two"] = Loud(rate, 1, frames),
            ["hundred"] = Loud(rate, 1, frames),
        }, "this test", []);

        var gap = rate * GapMs / 1000;
        var tail = rate * LeadOutMs / 1000;

        var two = Compose(["one", "two"], bank);
        Check("two words are one clip, with a gap and a lead out",
              two.Pcm.Length / 2 == frames * 2 + gap + tail,
              $"{two.Pcm.Length / 2} frames, expected {frames * 2 + gap + tail}");
        Check("...and keep the format of the clips they came from",
              two.SampleRate == rate && two.Channels == 1 && two.Bits == 16);

        var three = Compose(["one", "hundred", "two"], bank);
        Check("a third word adds one word and one gap",
              three.Pcm.Length - two.Pcm.Length == (frames + gap) * 2,
              $"{(three.Pcm.Length - two.Pcm.Length) / 2} frames");

        // The gap starts where the first word ends, which is exactly its own length
        // because nothing was trimmed from it.
        Check("the gap really is silent", IsSilent(two.Pcm, frames, gap));

        Check("a word with no clip is refused by name",
              Throws(() => Compose(["one", "ninety"], bank), "ninety"));
        Check("nothing to say is refused", Throws(() => Compose([], bank)));

        // Two sittings at different settings would otherwise be concatenated into
        // samples that play at the wrong speed from the seam onwards.
        var mixed = new ClipBank(new Dictionary<string, WwiseWem.WavData>(StringComparer.Ordinal)
        {
            ["one"] = Loud(rate, 1, frames),
            ["two"] = Loud(44100, 1, frames),
        }, "this test", []);
        Check("clips recorded in two formats are refused, not spliced",
              Throws(() => Compose(["one", "two"], mixed), "44100"));

        W("");
        W("trimming");
        var padded = Padded(rate, frames, padFrames: rate / 2);
        var trimmed = Compose(["pad"], new ClipBank(
            new Dictionary<string, WwiseWem.WavData>(StringComparer.Ordinal) { ["pad"] = padded },
            "this test", []));
        var margin = rate * MarginMs / 1000;
        Check("silence either end of a recording is dropped",
              trimmed.Pcm.Length / 2 == frames + margin * 2 + tail,
              $"{trimmed.Pcm.Length / 2} frames from {padded.Pcm.Length / 2}");

        W("");
        W("the recorded clips");
        var found = LoadClips();
        W("  --    " + found.Describe());
        if (!found.Complete)
            W($"  --    record them on Windows: XzoundWave.exe --numberclips \"{DefaultClipDir}\"");
        W("  --    clips are not needed for the checks above, and are not counted");

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    private static bool Throws(Action a, string mentioning = null)
    {
        try { a(); return false; }
        catch (Exception ex)
        {
            return mentioning is null
                || ex.Message.Contains(mentioning, StringComparison.Ordinal);
        }
    }

    private static WwiseWem.WavData Loud(int rate, short ch, int frames)
    {
        var pcm = new byte[frames * ch * 2];
        for (var i = 0; i < pcm.Length; i += 2)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i, 2), 12000);
        return new WwiseWem.WavData(rate, ch, 16, pcm);
    }

    private static WwiseWem.WavData Padded(int rate, int frames, int padFrames)
    {
        var loud = Loud(rate, 1, frames).Pcm;
        var pcm = new byte[(frames + padFrames * 2) * 2];
        Buffer.BlockCopy(loud, 0, pcm, padFrames * 2, loud.Length);
        return new WwiseWem.WavData(rate, 1, 16, pcm);
    }

    /// <summary>Mono 16-bit, which is all the synthetic clips above are.</summary>
    private static bool IsSilent(byte[] pcm, int atFrame, int frames)
    {
        for (var i = atFrame * 2; i < (atFrame + frames) * 2; i++)
            if (pcm[i] != 0) return false;
        return true;
    }
}
