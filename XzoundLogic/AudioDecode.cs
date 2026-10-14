using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NAudio.Wave;

using Xzound;

namespace MRAudioKit;

/// <summary>
/// Turns whatever audio file someone drags in into the 16-bit PCM the encoder wants.
///
/// wav and Ogg Vorbis are read inside this process, by NAudio's own wav reader and by
/// NVorbis. Everything else goes to the bundled vgmstream, which is the decoder the
/// preview and the volume tool already run and which carries the ffmpeg codec libraries
/// for mp3, AAC, ALAC, WMA and FLAC.
///
/// The split is measured, not stylistic. A wav or an ogg read in process comes out
/// byte-identical on Windows and on Linux, so a wem built from one is the wem it has
/// always been on either host; handing those two to a subprocess would change every
/// such wem and tie the result to whichever vgmstream revision a platform's payload
/// happens to carry. The formats above had no in-process reader at all: Media Foundation
/// decoded them on Windows and the portable build refused them outright, so the same
/// mp3 imported on Linux produced nothing rather than producing something slightly
/// different. One decoder for both platforms is what ends that, and it is the reason
/// Media Foundation is not kept as a Windows-only preference: a per-format preference
/// would make the two platforms disagree permanently instead of once.
///
/// Media Foundation is therefore gone from this file. The only format it read that
/// vgmstream will not open is G.711 wav, and NAudio.Core carries the mu-law and A-law
/// tables, so that case is decoded here in process on every platform and nothing is
/// left for a fallback to do.
///
/// Everything comes out as interleaved 16-bit little-endian PCM at the source's own
/// sample rate. Rate is never changed: resampling would be a quality loss the author
/// did not ask for, and XzoundCore encodes any rate.
/// </summary>
public static class AudioDecode
{
    /// <summary>
    /// The formats read without leaving this process, and what reads them. One table
    /// rather than a switch, because <see cref="ToPcm"/> and <see cref="ReadInProcess"/>
    /// have to agree about which route a file takes: the self-test prints the routing,
    /// and a printed table that is not the table used is worse than none.
    /// </summary>
    private static readonly Dictionary<string, Func<string, WwiseWem.WavData>> InProcess =
        new(StringComparer.Ordinal)
        {
            [".wav"] = FromWav,
            [".ogg"] = FromVorbis,
        };

    /// <summary>Already a wem, so it is accepted and never decoded.</summary>
    private const string Wem = ".wem";

    /// <summary>
    /// The flag that tells vgmstream to render what is IN the file rather than what a
    /// player would do with it. Without it a declared loop point is obeyed: the loop is
    /// played through a second time and a ten second fade is appended, so the 44.333 s
    /// of music in media 972121045 decode to 77.937 s of audio nobody authored.
    ///
    /// Named here once because four call sites hand this decoder a file and all four
    /// want the file's own contents -- the import, the preview, the volume tool and the
    /// duration shown beside them. Repeating a loop is the bank's business and not the
    /// decoder's: how many times a sound plays through is authored in the hierarchy,
    /// which is why <see cref="InGameChain"/> loops the decoded audio itself as many
    /// times as the bank asks. Audio that had already been looped once by the decoder
    /// would be looped again on top of that.
    /// </summary>
    public const string NoLoop = "-i";

    /// <summary>
    /// What is offered when vgmstream cannot be asked what it reads. These five were
    /// accepted before the decoder was asked anything, and a machine where the unpack
    /// failed should get an error naming the decoder rather than a file dialog that has
    /// quietly stopped showing mp3s.
    /// </summary>
    private static readonly string[] Always =
        [".mp3", ".flac", ".m4a", ".aac", ".wma"];

    /// <summary>
    /// Extensions we can turn into a wem, lowercase, with the dot. Composed once per
    /// decoder rather than per call, because a drag of a folder asks
    /// <see cref="CanDecode"/> this once per file.
    /// </summary>
    public static string[] Extensions
    {
        get
        {
            var declared = Declared();
            lock (Gate)
            {
                if (_accepted is not null && ReferenceEquals(_acceptedFrom, declared))
                    return _accepted;
                var rest = Always.Concat(declared).Distinct(StringComparer.Ordinal)
                                 .Where(e => !InProcess.ContainsKey(e) && e != Wem)
                                 .OrderBy(e => e, StringComparer.Ordinal);
                _acceptedFrom = declared;
                return _accepted = [.. InProcess.Keys, .. rest, Wem];
            }
        }
    }

    private static string[] _accepted;
    private static string[] _acceptedFrom;

    /// <summary>An open-file filter listing everything we accept.</summary>
    public static string DialogFilter =>
        "Audio|" + string.Join(";", Extensions.Select(e => "*" + e)) +
        "|Wwise media|*.wem|All files|*.*";

    public static bool CanDecode(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>True when the file is already a wem and needs no conversion.</summary>
    public static bool IsWem(string path) =>
        Path.GetExtension(path).Equals(Wem, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this file is read here or by vgmstream.</summary>
    public static bool ReadInProcess(string path) =>
        InProcess.ContainsKey(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Decode to 16-bit PCM. Throws with a message worth showing the user -- these
    /// are files they chose, so "what was wrong with it" matters more than a stack.
    /// </summary>
    public static WwiseWem.WavData ToPcm(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            var data = InProcess.TryGetValue(ext, out var reader)
                ? reader(path)
                : FromVgmstream(path);
            if (data.Pcm.Length == 0)
                throw new InvalidDataException("decoded to no audio at all");
            return Downmix(data);
        }
        // A message that already names its own remedy is passed through whole. The
        // decoder being absent is the case that matters: what to do about it has
        // nothing to do with which format was dropped, so borrowing the per-format
        // advice below would send the user to convert a file that was never the problem.
        catch (InvalidDataException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidDataException(Describe(ext, ex), ex);
        }
    }

    /// <summary>
    /// What went wrong, and something the user can actually do about it.
    ///
    /// Only wav and ogg are read in this process, so "convert it to wav" is sound advice
    /// for every other format and is the one remedy that needs neither vgmstream nor a
    /// codec. It is exactly wrong for a wav, which is what the previous wording told the
    /// owner of an ADPCM or mu-law file to do.
    /// </summary>
    private static string Describe(string ext, Exception ex) => ext switch
    {
        ".ogg" => $"not readable as Ogg Vorbis ({ex.Message})",
        ".wav" =>
            $"could not decode this wav ({ex.Message}). It is not one of the plain PCM, " +
            "float or G.711 layouts read here, and vgmstream could not open it either. " +
            "Re-exporting it as 16-bit PCM wav will work.",
        _ =>
            $"could not decode this {ext.TrimStart('.')} ({ex.Message}). " +
            "Converting it to wav or ogg first will work, since those two are read " +
            "without vgmstream.",
    };

    // ---- readers -----------------------------------------------------------

    /// <summary>
    /// A plain wav, including float, 8/24/32-bit and G.711, without a subprocess.
    /// A codec NAudio's own reader does not implement falls through to vgmstream, which
    /// reads MS-ADPCM, IMA-ADPCM and mp3-in-wav. That fallback used to be Media
    /// Foundation, which meant a wav the managed reader rejected failed on Linux with a
    /// message telling the user to convert their wav into a wav.
    ///
    /// A file that is not a WAVE at all does not get that fallback, and the check is the
    /// container rather than anything file-specific. vgmstream is a game-audio tool and
    /// carries headerless readers chosen by extension, so 4 KB of arbitrary bytes named
    /// .wav comes back as a fraction of a second of Nintendo DTK ADPCM instead of
    /// failing. Noise is a worse answer than the reader's own complaint, while a real
    /// WAVE it rejected is a codec it does not implement, which is exactly what the
    /// fallback is for.
    /// </summary>
    private static WwiseWem.WavData FromWav(string path)
    {
        try
        {
            using var r = new WaveFileReader(path);
            return Read(r);
        }
        catch (Exception ex)
        {
            if (!IsRiffWave(path))
                throw new InvalidDataException(
                    $"this is not a wav: it has no RIFF WAVE header ({ex.Message})");
            return FromVgmstream(path);
        }
    }

    private static bool IsRiffWave(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[12];
            return f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                   && head[..4].SequenceEqual("RIFF"u8)
                   && head[8..].SequenceEqual("WAVE"u8);
        }
        catch { return false; }
    }

    /// <summary>
    /// Everything that is not wav or ogg, decoded by the bundled vgmstream.
    ///
    /// <see cref="NoLoop"/> is what every caller of this decoder passes, for the reason
    /// given there. The flag that is this route's own is -p, which puts the wav on
    /// stdout: it was verified byte-identical to what -o writes, so an import needs no
    /// scratch file and no temp directory to clean up.
    /// </summary>
    private static WwiseWem.WavData FromVgmstream(string path)
    {
        var exe = VgmstreamPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new InvalidDataException(
                "vgmstream is not available. It reads every format except plain wav and " +
                "Ogg Vorbis, including the wav layouts the reader here cannot open. " +
                (DecoderProblem ?? "It normally unpacks itself from the exe.") +
                " Settings > Tools shows what it is using and can point at your own copy.");

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(NoLoop);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(path);

        using var wav = new MemoryStream();
        string stderr;
        int exit;
        using (var proc = Process.Start(psi))
        {
            // Both pipes are drained at once. Reading one to the end first deadlocks as
            // soon as the other fills, and the audio on stdout is orders of magnitude
            // larger than the banner on stderr.
            var errors = proc.StandardError.ReadToEndAsync();
            proc.StandardOutput.BaseStream.CopyTo(wav);
            proc.WaitForExit();
            stderr = errors.GetAwaiter().GetResult();
            exit = proc.ExitCode;
        }

        if (wav.Length == 0)
        {
            // A decoder that will not start at all is a different problem from one that
            // rejected this particular file, and the two are told apart the same way the
            // preview tells them apart.
            var launch = AudioPreview.LaunchProblem(exe);
            if (launch is not null)
                throw new InvalidDataException("vgmstream could not run: " + launch);
            throw new InvalidOperationException(
                $"vgmstream read nothing from it (exit {exit})" +
                (string.IsNullOrWhiteSpace(stderr) ? "" : ": " + stderr.Trim()));
        }

        wav.Position = 0;
        using var r = new WaveFileReader(wav);
        return Read(r);
    }

    private static WwiseWem.WavData FromVorbis(string path)
    {
        using var v = new NVorbis.VorbisReader(path);
        var ch = v.Channels;
        var floats = new float[ch * 4096];
        using var ms = new MemoryStream();
        int n;
        while ((n = v.ReadSamples(floats, 0, floats.Length)) > 0)
            WriteSamples(ms, floats.AsSpan(0, n));
        return new WwiseWem.WavData(v.SampleRate, (short)ch, 16, ms.ToArray());
    }

    /// <summary>
    /// Pull 16-bit PCM out of any NAudio stream. Float and higher-bit sources are
    /// converted through the sample provider so the clamp and rounding are NAudio's
    /// rather than ours.
    /// </summary>
    private static WwiseWem.WavData Read(WaveStream stream)
    {
        var fmt = stream.WaveFormat;
        if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
            return new WwiseWem.WavData(fmt.SampleRate, (short)fmt.Channels, 16,
                                        AllOf(stream, fmt));

        // The one thing vgmstream refuses to open, so it is decoded here or nowhere.
        // NAudio's sample provider does not cover G.711 either, which is why this sits
        // ahead of it rather than inside it: the exception it throws is what used to
        // send a mu-law wav to Media Foundation.
        if (fmt.Encoding is WaveFormatEncoding.MuLaw or WaveFormatEncoding.ALaw)
            return FromG711(stream, fmt);

        var sp = stream.ToSampleProvider();
        var buf = new float[fmt.Channels * 4096];
        using var ms = new MemoryStream();
        int n;
        while ((n = sp.Read(buf)) > 0)
            WriteSamples(ms, buf.AsSpan(0, n));
        return new WwiseWem.WavData(fmt.SampleRate, (short)fmt.Channels, 16, ms.ToArray());
    }

    /// <summary>
    /// Companded telephone audio, one byte per sample. The tables are NAudio.Core's, so
    /// this works in the portable build as well, and the interleaving needs no thought
    /// because a frame is one byte per channel.
    /// </summary>
    private static WwiseWem.WavData FromG711(WaveStream stream, WaveFormat fmt)
    {
        var bytes = AllOf(stream, fmt);
        var count = bytes.Length;

        var pcm = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var s = fmt.Encoding == WaveFormatEncoding.MuLaw
                ? NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(bytes[i])
                : NAudio.Codecs.ALawDecoder.ALawToLinearSample(bytes[i]);
            pcm[i * 2] = (byte)s;
            pcm[i * 2 + 1] = (byte)(s >> 8);
        }
        return new WwiseWem.WavData(fmt.SampleRate, (short)fmt.Channels, 16, pcm);
    }

    /// <summary>
    /// Every byte of a wav's data, read in whole frames.
    ///
    /// Stream.CopyTo asks for 81920 bytes at a time and NAudio's reader refuses a length
    /// that is not a multiple of the block align, so a 5.1 wav, whose frame is twelve
    /// bytes, threw here and was decoded by the fallback instead of being read and folded
    /// to stereo. Six channels is the ordinary shape of imported music, and nothing about
    /// the failure said which of the two was reading the file.
    /// </summary>
    private static byte[] AllOf(WaveStream stream, WaveFormat fmt)
    {
        var block = Math.Max(1, fmt.BlockAlign);
        var buf = new byte[block * 4096];
        using var raw = new MemoryStream();
        int n;
        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
            raw.Write(buf, 0, n);
        return raw.ToArray();
    }

    private static void WriteSamples(Stream to, ReadOnlySpan<float> samples)
    {
        Span<byte> two = stackalloc byte[2];
        foreach (var f in samples)
        {
            var s = (int)MathF.Round(Math.Clamp(f, -1f, 1f) * 32767f);
            two[0] = (byte)s;
            two[1] = (byte)(s >> 8);
            to.Write(two);
        }
    }

    // ---- which vgmstream, and what it says it reads -------------------------

    private static readonly object Gate = new();
    private static string _decoder;
    private static bool _decoderAsked;
    private static string _decoderProblem;

    /// <summary>
    /// The vgmstream to decode with, resolved the way <see cref="Settings.AutoDetect"/>
    /// resolves it: a copy the user pointed at wins, then one beside the exe, then the
    /// bundled payload. Settable, so a host that lets the user change it mid-session
    /// assigns it here as it already does on <see cref="AudioPreview"/>; left alone it
    /// resolves itself, because <see cref="ToPcm"/> is called from three hosts and a new
    /// parameter would have to be threaded through all of them.
    /// </summary>
    public static string VgmstreamPath
    {
        get
        {
            lock (Gate)
            {
                if (_decoderAsked && (_decoder is null || File.Exists(_decoder)))
                    return _decoder;
                _decoderAsked = true;
                _decoder = Resolve(out _decoderProblem);
                return _decoder;
            }
        }
        set
        {
            lock (Gate)
            {
                _decoderAsked = true;
                _decoder = value;
                _decoderProblem = null;
            }
        }
    }

    /// <summary>Why there is no decoder, when there is none.</summary>
    private static string DecoderProblem
    {
        get { _ = VgmstreamPath; lock (Gate) return _decoderProblem; }
    }

    private static string Resolve(out string problem)
    {
        problem = null;
        try
        {
            var chosen = Settings.Load().VgmstreamPath;
            if (!string.IsNullOrWhiteSpace(chosen) && File.Exists(chosen)) return chosen;

            var beside = Path.Combine(AppContext.BaseDirectory, AppPaths.Exe("vgmstream-cli"));
            if (File.Exists(beside)) return beside;

            return Bundled.EnsureVgmstream(out problem);
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    private static string _declaredBy;
    private static string[] _declared = [];
    private static string _revision;

    /// <summary>
    /// Which revision of vgmstream is doing the decoding, or null when there is none.
    ///
    /// Worth reporting rather than assuming: the payloads for the two platforms are
    /// separate downloads, and two revisions decode the same mp3 to the same length and
    /// alignment but to within two least significant bits of each other rather than
    /// identically, which is inaudible and is still a different wem. Anything claiming
    /// two hosts produce the same bytes has to be able to show they ran the same decoder.
    /// </summary>
    public static string DecoderRevision { get { _ = Declared(); lock (Gate) return _revision; } }

    /// <summary>
    /// Every decoder that could have produced a decode from here, named together, in the
    /// form something that STORES the result of one keys itself on.
    ///
    /// Three libraries turn a file into PCM in this class and a change to any of them
    /// changes the bytes: vgmstream for everything that is not wav or ogg, NAudio's wav
    /// reader, and NVorbis. They are named as one identity rather than one per format
    /// because <see cref="FromWav"/> hands a wav layout NAudio will not open to vgmstream
    /// instead, so the extension does not say which of them ran, and an identity that
    /// guessed from the extension would be wrong for exactly the files that took the
    /// fallback. Naming all three costs a re-encode when any one of them moves, which is
    /// the cheap direction to be wrong in.
    ///
    /// Never null, and it says "none" rather than leaving the term out, because a host
    /// where the unpack failed still imports wav and ogg and those must not be written
    /// under the key a host with a decoder would read them back from.
    /// </summary>
    public static string DecoderIdentity =>
        $"vgmstream/{DecoderRevision ?? "none"} " +
        $"naudio/{typeof(WaveFileReader).Assembly.GetName().Version} " +
        $"nvorbis/{typeof(NVorbis.VorbisReader).Assembly.GetName().Version}";

    /// <summary>
    /// What vgmstream itself says it reads, asked rather than listed. Its -V output
    /// carries a "common" group that is exactly the everyday container formats, so opus,
    /// aiff, mp2, ac3 and mp4 are accepted because the decoder declares them and not
    /// because anyone remembered to add them.
    ///
    /// bin and dat are in that group and are kept. They are what a great many games name
    /// their audio, which is why vgmstream claims them, and the honest meaning of this
    /// list is "we will try", with a failure that says what happened.
    /// </summary>
    private static string[] Declared()
    {
        var exe = VgmstreamPath;
        lock (Gate)
        {
            if (_declaredBy == exe) return _declared;
            _declaredBy = exe;
            _declared = [];
            _revision = null;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return _declared;

            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-V");
                string json;
                using (var proc = Process.Start(psi))
                {
                    var errors = proc.StandardError.ReadToEndAsync();
                    json = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    _ = errors.GetAwaiter().GetResult();
                }

                // Not checked against the exit code: vgmstream prints this and then
                // exits non-zero for want of an input file, so the JSON is the only
                // thing worth believing.
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var v))
                    _revision = v.GetString();
                if (doc.RootElement.TryGetProperty("extensions", out var exts) &&
                    exts.TryGetProperty("common", out var common))
                    _declared = [.. common.EnumerateArray()
                                          .Select(e => "." + e.GetString())
                                          .Where(e => e.Length > 1)];
            }
            catch { /* an older or stranger build says nothing, and the floor covers it */ }
            return _declared;
        }
    }

    // ---- channels ----------------------------------------------------------

    /// <summary>
    /// The game's banks are mono or stereo, and the encoder writes a channel config
    /// for those two. A 5.1 source is folded to stereo rather than rejected -- the
    /// author dragged in music, and a refusal helps nobody.
    /// </summary>
    private static WwiseWem.WavData Downmix(WwiseWem.WavData d)
    {
        if (d.Channels <= 2) return d;

        var frames = d.Pcm.Length / 2 / d.Channels;
        var outPcm = new byte[frames * 2 * 2];
        for (var f = 0; f < frames; f++)
        {
            int l = 0, r = 0;
            for (var c = 0; c < d.Channels; c++)
            {
                var s = BitConverter.ToInt16(d.Pcm, (f * d.Channels + c) * 2);
                if (c % 2 == 0) l += s; else r += s;
            }
            var half = (d.Channels + 1) / 2;
            var other = d.Channels / 2;
            Write16(outPcm, f * 4, l / Math.Max(1, half));
            Write16(outPcm, f * 4 + 2, r / Math.Max(1, other));
        }
        return new WwiseWem.WavData(d.SampleRate, 2, 16, outPcm);

        static void Write16(byte[] b, int at, int v)
        {
            v = Math.Clamp(v, short.MinValue, short.MaxValue);
            b[at] = (byte)v;
            b[at + 1] = (byte)(v >> 8);
        }
    }
}
