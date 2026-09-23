using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace MRAudioKit;

/// <summary>
/// wem -> wav -> speakers. vgmstream does the decoding; this only decides WHICH
/// bytes to hand it, which is the part that is easy to get wrong.
/// </summary>
public sealed class AudioPreview : IDisposable
{
    /// <summary>
    /// Where the samples go. The decode and everything the .bnk applies on top of it
    /// are portable; only this last step belongs to a platform, so it is the one thing
    /// behind an interface.
    /// </summary>
    private IAudioOutput _out;
    private WaveFileReader _reader;

    /// <summary>Raised when a clip finishes on its own, which is what lets one
    /// sound lead into the next without polling or guessing at durations.</summary>
    public event Action Finished;

    /// <summary>Raised when playback itself fails, rather than the decode.</summary>
    public event Action<string> PlaybackFailed;
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "XzoundWave");
    public string VgmstreamPath { get; set; }

    public AudioPreview() => Directory.CreateDirectory(_cache);

    /// <summary>
    /// The loose streamed file when one exists, otherwise the bank's copy.
    /// Getting this order wrong makes every voice line a 0.03 s click, because the
    /// bank embeds only a prefetch stub of streamed voice.
    /// </summary>
    /// <summary>
    /// The full streamed file when <paramref name="held"/> is only its opening
    /// fragment, or null when the bytes in hand are the whole sound.
    /// </summary>
    private static (string Path, byte[] Full)? Prefetch(GameSession session, uint mediaId, byte[] held)
    {
        var loose = session?.LooseMedia(mediaId);
        // A stub is shorter than what it is a stub OF, so a file no larger than the
        // bytes in hand cannot be one and is not worth reading.
        if (loose is null || loose.Size <= held.Length) return null;
        try
        {
            var full = loose.Read();
            if (full is not { Length: > 0 } || full.Length <= held.Length) return null;
            return full.AsSpan(0, held.Length).SequenceEqual(held) ? (loose.Path, full) : null;
        }
        catch { return null; }
    }

    public byte[] Bytes(GameSession session, SkinSounds sounds, SoundRow row,
                        bool ignoreStaged = false)
    {
        // A file staged for this sound is what will ship, so it is what "play this row"
        // means. The game's own audio is still reachable -- Play the original, on the
        // right-click menu -- because comparing the two is most of the job.
        if (!ignoreStaged && row.ReplacementPath is not null && File.Exists(row.ReplacementPath))
        {
            try
            {
                var staged = File.ReadAllBytes(row.ReplacementPath);
                if (staged.Length > 0)
                {
                    row.Origin = row.ReplacementPath + "  (staged)";
                    return staged;
                }
            }
            catch { /* fall through to the game's copy rather than failing the preview */ }
        }

        // Bytes we were handed win: a bank opened from a mod reuses the shipped media
        // ids, so asking the game first would play the vanilla sound instead.
        if (sounds.Raw.TryGetValue(row.MediaId, out var held) && held is { Length: > 0 })
        {
            // Unless they are a PREFETCH STUB. Most voice lines stream from Media/ and
            // the bank embeds only the opening fragment, so a mod that left a line
            // alone hands us a few hundred bytes -- a fraction of a second and a hard
            // cut, which is what the vanilla path avoids by reading loose first.
            //
            // A stub is a byte-prefix of the loose file, and audio a mod actually
            // replaced is not, so the bytes themselves say which this is. No flag to
            // set, and it stays right for a .bnk with no shipped copy to diff against.
            if (Prefetch(session, row.MediaId, held) is { } streamed)
            {
                row.Origin = streamed.Path + "  (bank holds a prefetch stub)";
                row.Bytes = streamed.Full.Length;
                return streamed.Full;
            }

            row.Origin = "opened .bnk";
            row.Bytes = held.Length;
            return held;
        }

        var loose = session.LooseMedia(row.MediaId);
        if (loose is not null)
        {
            try
            {
                var d = loose.Read();
                if (d is { Length: > 0 }) { row.Origin = loose.Path; row.Bytes = d.Length; return d; }
            }
            catch { }
        }
        if (sounds.Embedded.TryGetValue(row.MediaId, out var def))
        {
            var d = def.GetData();
            if (d is { Length: > 0 })
            {
                row.Origin = ".bnk (embedded)";
                row.Bytes = d.Length;
                return d;
            }
        }
        return null;
    }

    public string WriteWem(byte[] data, uint mediaId)
    {
        var p = Path.Combine(_cache, $"{mediaId}.wem");
        File.WriteAllBytes(p, data);
        return p;
    }

    /// <summary>Decode to wav, returning the path, or null with a reason.</summary>
    public string Decode(byte[] data, uint mediaId, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(VgmstreamPath) || !File.Exists(VgmstreamPath))
        {
            error = "vgmstream is not available. It normally unpacks itself from the exe; " +
                    "Settings > Tools shows what it is using and can point at your own copy.";
            return null;
        }
        var wem = WriteWem(data, mediaId);
        var wav = Path.Combine(_cache, $"{mediaId}.wav");
        try
        {
            Run($"-o \"{wav}\" \"{wem}\"", out var stderr, out var exit);
            if (File.Exists(wav)) return wav;

            // Say which kind of failure it was: a decoder that will not start at all
            // is a different problem from one that rejected this particular sound.
            var launch = LaunchProblem(VgmstreamPath);
            error = launch is not null
                ? "vgmstream could not run — " + launch
                : $"vgmstream produced no output (exit {exit})." +
                  (string.IsNullOrWhiteSpace(stderr) ? "" : " " + stderr.Trim());
            return null;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>Sample rate / channels / duration, without decoding the whole file.</summary>
    public double? Probe(byte[] data, uint mediaId)
    {
        if (string.IsNullOrWhiteSpace(VgmstreamPath) || !File.Exists(VgmstreamPath)) return null;
        try
        {
            var meta = Run($"-m \"{WriteWem(data, mediaId)}\"");
            foreach (var line in meta.Split('\n'))
            {
                var i = line.IndexOf("play duration:", StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;
                var open = line.IndexOf('(');
                var sp = line.IndexOf(" seconds", StringComparison.OrdinalIgnoreCase);
                if (open < 0 || sp < 0) continue;
                var span = line[(open + 1)..sp];
                var parts = span.Split(':');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], out var mm) &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var ss))
                    return mm * 60 + ss;
            }
        }
        catch { }
        return null;
    }

    private string Run(string args) => Run(args, out _, out _);

    /// <summary>
    /// Keep what vgmstream said. Throwing stderr away turned "it could not start at
    /// all" and "it did not like that file" into the same blank failure, which is the
    /// hardest kind to diagnose from someone else's machine.
    /// </summary>
    private string Run(string args, out string stderr, out int exitCode)
    {
        var psi = new ProcessStartInfo(VgmstreamPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        var o = p.StandardOutput.ReadToEnd();
        stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        exitCode = p.ExitCode;
        return o;
    }

    /// <summary>
    /// Can vgmstream actually start on this machine? Existing on disk is not the same
    /// thing: it is a native binary, so a missing Visual C++ runtime or an antivirus
    /// quarantine stops it dead with nothing written to the log.
    /// </summary>
    public static string LaunchProblem(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return "no path set.";
        if (!File.Exists(exePath)) return $"not on disk: {exePath}";
        try
        {
            var psi = new ProcessStartInfo(exePath, "-h")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            // It prints its banner whatever the arguments, so seeing the name is the
            // proof that the process started and its DLLs loaded.
            if (text.Contains("vgmstream", StringComparison.OrdinalIgnoreCase)) return null;
            return Explain(p.ExitCode);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return $"Windows refused to start it ({ex.NativeErrorCode}): {ex.Message}";
        }
        catch (Exception ex) { return "could not start it: " + ex.Message; }
    }

    /// <summary>
    /// Turn a process exit code into something a person can act on. These two account
    /// for nearly every "it just does not play" report: vgmstream is a native binary,
    /// and it fails before printing anything when a dependency is missing.
    /// </summary>
    private static string Explain(int exitCode) => (uint)exitCode switch
    {
        0xC0000135 =>
            "a DLL it needs is missing (STATUS_DLL_NOT_FOUND). Most often that is the " +
            "Microsoft Visual C++ Redistributable (x64), from " +
            "https://aka.ms/vs/17/release/vc_redist.x64.exe — or antivirus has removed " +
            "one of the codec DLLs next to it.",
        0xC000007B =>
            "it is the wrong architecture for this machine (STATUS_INVALID_IMAGE_FORMAT).",
        0xC0000142 =>
            "a DLL it needs failed to initialise (STATUS_DLL_INIT_FAILED).",
        _ => $"it started but said nothing recognisable (exit {exitCode}, 0x{(uint)exitCode:X8})."
    };

    public void Play(string wavPath) => Play(wavPath, null, out _);

    /// <param name="applied">
    /// What the .bnk does to this sound. Passed in, the preview is what the game
    /// plays; left out, it is what the file contains. Both are worth hearing, which is
    /// why it is a switch rather than a decision made here.
    /// </param>
    /// <param name="described">What was applied, for the status line.</param>
    public void Play(string wavPath, WwiseProps.Applied applied, out string described)
    {
        described = "";
        Release();
        try
        {
            _reader = new WaveFileReader(wavPath);
            _out = AudioOut.Create();
            _out.Failed += msg => PlaybackFailed?.Invoke(msg);
            _out.Finished += () => Finished?.Invoke();
            _out.Play(InGameChain.Build(_reader, applied, out described));
        }
        catch (Exception ex)
        {
            Release();
            PlaybackFailed?.Invoke(ex.Message);
            // A clip that cannot play must still advance, or one bad file stalls a run.
            Finished?.Invoke();
        }
    }

    /// <summary>
    /// Whether sound is coming out right now. Polling this is how a headless check can
    /// watch a clip through: the finished event is posted to the synchronization
    /// context that created the player, so a caller blocking that thread never sees it.
    /// </summary>
    public bool IsPlaying => _out?.IsPlaying == true;

    public void Stop()
    {
        try { _out?.Stop(); } catch { }
    }

    private void Release()
    {
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _out = null;
        _reader = null;
    }

    /// <summary>
    /// Is there anywhere for sound to go? Reported by the diagnostics, because "no
    /// output device" and "the decoder is broken" look identical from the outside.
    /// </summary>
    public static string OutputProblem()
    {
        try { using var probe = AudioOut.Create(); return probe.Problem(); }
        catch (Exception ex) { return "could not query audio output: " + ex.Message; }
    }

    public static string OutputSummary()
    {
        try { using var probe = AudioOut.Create(); return probe.Summary(); }
        catch (Exception ex) { return "unknown — " + ex.Message; }
    }

    public void Dispose() => Release();
}
