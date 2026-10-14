using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MRAudioKit;

/// <summary>
/// Sound through the player the desktop already has.
///
/// The release is one file with nothing to install, which rules out a backend that
/// needs a library the machine does not carry. PortAudio is the better output and is
/// used wherever it is there, but a stock Ubuntu has no libportaudio, and answering a
/// silent app with "install libportaudio2" is exactly the promise being broken. What a
/// desktop that can make a sound at all does have is a command that plays one, so this
/// drives that instead of linking anything: raw PCM down a pipe into pw-play, paplay or
/// aplay, whichever is there.
///
/// It adds no package reference and no native dependency, and nothing above this class
/// knows which backend it got.
/// </summary>
public sealed class PipeAudioOutput : IAudioOutput
{
    /// <summary>
    /// Roughly a tenth of a second at 44.1 kHz stereo. The size does not set how long a
    /// <see cref="Stop"/> takes to be noticed, unlike the PortAudio output where the
    /// feeding thread can only look between writes: killing the player closes the read
    /// end of the pipe, which throws a write that is already blocked in it.
    /// </summary>
    private const int BytesPerWrite = 16384;

    /// <summary>
    /// How long a player is given to finish after being told the clip is over. It is
    /// waiting for what the player has already buffered, which is well under a second,
    /// and one that has not exited by now is not going to. The clip still counts as
    /// finished when the wait runs out, because the audio was all handed over.
    /// </summary>
    private const int DrainMs = 5000;

    /// <summary>
    /// A command that plays raw PCM read from standard input, and how to ask it to.
    ///
    /// Raw rather than a WAV header on the pipe, which was the other way to do this.
    /// Only aplay would be simplified by a header, since it reads WAVE unless told
    /// otherwise; pw-play and paplay hand a headed stream to libsndfile, which wants to
    /// seek in what it is reading and a pipe cannot be seeked. All three document a raw
    /// mode and the flags that go with it, so raw is the one shape all three agree on.
    /// A header would also have to state a length before the first sample is read, and
    /// a chain whose providers can stop at their own boundaries makes that a guess.
    /// </summary>
    internal sealed record Player(string Command, string What, Func<int, int, string[]> Arguments)
    {
        /// <summary>The file the search resolved this command to.</summary>
        public string Path { get; init; }

        /// <summary>What the diagnostics call it, as in "pw-play (PipeWire)".</summary>
        public override string ToString() => $"{Command} ({What})";
    }

    /// <summary>
    /// The players worth trying, best first.
    ///
    /// PipeWire is what a current desktop runs and pw-play speaks to it directly.
    /// paplay speaks the PulseAudio protocol, which PipeWire also serves, so it works on
    /// both and is an older machine's answer. aplay is ALSA itself, which is what is
    /// left when no sound server is running at all.
    ///
    /// The three spell one format three ways. The samples are 16-bit little-endian
    /// PCM, which is what <see cref="SampleToWaveProvider16"/> produces and what every
    /// one of them takes without argument; pw-play's "s16" is the machine's own byte
    /// order, and that is little-endian on both architectures this is built for.
    /// </summary>
    internal static readonly IReadOnlyList<Player> Candidates =
    [
        new("pw-play", "PipeWire", (rate, channels) =>
            ["--raw", "--format=s16", $"--rate={N(rate)}", $"--channels={N(channels)}", "-"]),

        // No file argument: paplay reads standard input when it is given none, and that
        // spelling has meant the same thing for longer than the "-" one has.
        new("paplay", "PulseAudio", (rate, channels) =>
            ["--raw", "--format=s16le", $"--rate={N(rate)}", $"--channels={N(channels)}"]),

        // -q because aplay otherwise announces every clip on stderr, and stderr is kept
        // here to explain a failure.
        new("aplay", "ALSA", (rate, channels) =>
            ["-q", "-t", "raw", "-f", "S16_LE", "-c", N(channels), "-r", N(rate), "-"]),
    ];

    /// <summary>
    /// A number as a command line spells it, which is not as the reader's locale does.
    /// A machine set to a locale with its own digits would otherwise hand the player a
    /// sample rate it cannot parse, and the failure would look like a broken device.
    /// </summary>
    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static readonly Lazy<Player> Search =
        new(() => Find(SearchPath), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Player _player;
    private volatile Run _run;
    private Thread _pump;
    private int _disposed;

    public event Action Finished;
    public event Action<string> Failed;

    private PipeAudioOutput(Player player) => _player = player;

    /// <summary>
    /// An output backed by whichever player this machine has, or null when it has none.
    /// Null is the signal to fall back and is not an error.
    /// </summary>
    public static PipeAudioOutput TryCreate() =>
        Search.Value is { } player ? new PipeAudioOutput(player) : null;

    /// <summary>Why there is no player here, or null when there is one.</summary>
    public static string LoadProblem =>
        Search.Value is null
            ? "No system player to pipe sound to: none of " +
              string.Join(", ", Candidates.Select(c => c.ToString())) + " is on PATH."
            : null;

    /// <summary>Which player was found, or null. For the diagnostics and the self test.</summary>
    internal static Player Found => Search.Value;

    /// <summary>Where a command is looked for, in the order the host would look.</summary>
    internal static string SearchPath => Environment.GetEnvironmentVariable("PATH");

    /// <summary>Which player this instance drives.</summary>
    internal Player Driving => _player;

    /// <summary>
    /// An output driving one named player, which is how the self test reaches the whole
    /// of this file on a machine with neither a player nor a sound card: a stand-in
    /// that only swallows bytes exercises every path here except the sound itself.
    /// </summary>
    internal static PipeAudioOutput Using(Player player) => new(player);

    /// <summary>
    /// The best candidate present on the given search path, or null.
    ///
    /// The path is passed in rather than read here so that the ordering rule, which is
    /// the part with a decision in it, can be checked against directories a test built
    /// rather than against whatever the machine running the test happens to have.
    /// </summary>
    internal static Player Find(string searchPath)
    {
        // Candidate first, directory second: a better player anywhere on the path beats
        // a worse one earlier on it, because the order of this list is a judgement about
        // the players and the order of PATH is a judgement about nothing in particular.
        foreach (var candidate in Candidates)
            if (OnPath(candidate.Command, searchPath) is { } full)
                return candidate with { Path = full };
        return null;
    }

    /// <summary>Where a command is, or null when it is nowhere on the path.</summary>
    internal static string OnPath(string command, string searchPath)
    {
        foreach (var dir in (searchPath ?? "").Split(Path.PathSeparator,
                                                     StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            // A PATH can hold anything, including entries with characters no path may
            // have, and one bad entry must not stop the search at the entry before it.
            try { full = Path.Combine(dir.Trim(), AppPaths.Exe(command)); }
            catch (ArgumentException) { continue; }

            if (!File.Exists(full)) continue;
            if (OperatingSystem.IsWindows()) return full;

            // A name on the path that nobody may run is not a player. This is the same
            // rule a shell follows, and skipping it would pick a file that then fails
            // to start, which reads as the machine having no sound.
            try
            {
                const UnixFileMode Runnable = UnixFileMode.UserExecute
                                            | UnixFileMode.GroupExecute
                                            | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(full) & Runnable) == 0) continue;
            }
            catch
            {
                // The file is there and its mode could not be read. Letting it through
                // means the worst case is a start that fails and says why, where
                // dropping it means silence with nothing to read.
            }
            return full;
        }
        return null;
    }

    /// <summary>
    /// One clip's worth of state, owned by the thread pumping it rather than by the
    /// player.
    ///
    /// It has to be per clip because <see cref="Release"/> gives up waiting after two
    /// seconds: a pump wedged in a write outlives the clip that started it, and shared
    /// fields would let it answer for the clip playing now, clearing "playing" for a
    /// clip that is still going or raising <see cref="Finished"/> for one nobody is
    /// listening to, which steps an autoplay run on by two rows.
    /// </summary>
    private sealed class Run
    {
        /// <summary>
        /// Held across a kill and across letting go of the process, because those are
        /// the two things different threads do to it and one must not free what the
        /// other is about to use.
        /// </summary>
        private readonly object _gate = new();
        private readonly StringBuilder _said = new();
        private Process _proc;

        public Run(Process proc) => _proc = proc;

        public volatile bool Playing;

        /// <summary>
        /// Set by <see cref="PipeAudioOutput.Stop"/> and by starting something else,
        /// before the player is killed, so a pump that sees the pipe break is
        /// guaranteed to see why it broke. The whole reason Finished exists is to
        /// advance an autoplay run, so a stop that was asked for must not raise it.
        /// </summary>
        public volatile bool OnPurpose;

        public Stream Input => _proc.StandardInput.BaseStream;

        /// <summary>
        /// What the player said, kept because it is the only account of why a clip
        /// stopped. Bounded: a player that is unhappy about every buffer would
        /// otherwise grow this without end for as long as the clip lasts.
        /// </summary>
        public void Say(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_gate)
            {
                if (_said.Length > 2000) return;
                if (_said.Length > 0) _said.Append(' ');
                _said.Append(line.Trim());
            }
        }

        public string Said()
        {
            lock (_gate) return _said.Length > 0 ? _said.ToString() : null;
        }

        /// <summary>Tell the player the clip is over, which is what closing the pipe means.</summary>
        public void CloseInput()
        {
            lock (_gate)
            {
                try { _proc?.StandardInput.Close(); } catch { }
            }
        }

        /// <summary>Its exit code, or null when it has not finished in time.</summary>
        public int? WaitFor(int ms)
        {
            // Deliberately outside the gate: this blocks for as long as the player takes
            // and Stop has to be able to kill it while it does.
            var proc = _proc;
            try { return proc is not null && proc.WaitForExit(ms) ? proc.ExitCode : null; }
            catch { return null; }
        }

        public void Kill()
        {
            lock (_gate)
            {
                if (_proc is not { } proc) return;
                try { if (proc.HasExited) return; } catch { return; }

                // The tree, because a distribution is free to ship any of these three as
                // a small script in front of the real program, and killing only the
                // script would leave the thing making the sound holding the device.
                try { proc.Kill(entireProcessTree: true); }
                catch { try { proc.Kill(); } catch { } }
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                try { _proc?.Dispose(); } catch { }
                _proc = null;
            }
        }
    }

    public bool IsPlaying => _run is { Playing: true };

    public void Play(ISampleProvider source)
    {
        Release();

        if (source is null) { Finished?.Invoke(); return; }

        Run run = null;
        try
        {
            // Sixteen bit because it is what all three players take without argument,
            // and because it is what the Windows output does to the same chain: this is
            // the converter WaveOut uses, clipping included, so a sound the bank pushed
            // past full scale is heard the same way on both platforms instead of
            // wrapping round on one of them.
            var pcm = new SampleToWaveProvider16(source);

            var psi = new ProcessStartInfo(_player.Path ?? _player.Command)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // The list form rather than one string: a path with a space in it is a
            // normal thing to have and quoting it by hand is how that goes wrong.
            foreach (var arg in _player.Arguments(source.WaveFormat.SampleRate,
                                                  source.WaveFormat.Channels))
                psi.ArgumentList.Add(arg);

            var proc = Process.Start(psi);
            run = new Run(proc) { Playing = true };

            // Both of the player's own pipes are drained. One that has something to say
            // and is never read fills its pipe, blocks, and stops reading ours, which
            // arrives here as a clip that stalls halfway rather than as an opinion.
            proc.OutputDataReceived += (_, _) => { };
            proc.ErrorDataReceived += (_, e) => run.Say(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            _run = run;
            _pump = new Thread(() => Pump(run, pcm))
            {
                IsBackground = true,
                Name = "XzoundWave audio",
            };
            _pump.Start();
        }
        catch (Exception ex)
        {
            // A player that was started and then abandoned before the pump took it on
            // would go on playing with nothing left that could stop it.
            run?.Kill();
            run?.Close();
            _run = null;

            Failed?.Invoke($"{_player.Command} could not start: {ex.Message}");
            // A clip that cannot play must still advance, or one bad file stalls a run.
            Finished?.Invoke();
        }
    }

    /// <summary>
    /// Hand the clip to the player a buffer at a time, then see it out.
    ///
    /// This thread owns the process for its whole life. Every other thread only sets a
    /// flag and kills, so there is no lock here to deadlock on and no call that another
    /// thread can make into the middle of a write.
    /// </summary>
    private void Pump(Run run, IWaveProvider source)
    {
        string failure = null;

        // Whether the player went away on its own, which is a different ending from the
        // clip running out and is the only one that might need explaining.
        var gone = false;

        try
        {
            var buffer = new byte[BytesPerWrite];
            while (true)
            {
                var n = source.Read(buffer);
                // Zero is the end of the clip. A short read is not: several of the
                // providers in the chain stop at their own boundaries.
                if (n <= 0) break;

                try { run.Input.Write(buffer, 0, n); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Writing to a pipe whose reader has gone is an error rather than a
                    // message, and killing the player is exactly how Stop works here, so
                    // this is the ordinary path out of a clip that was stopped. Only the
                    // code below knows whether anyone asked for it.
                    gone = true;
                    break;
                }
            }

            if (!run.OnPurpose)
            {
                // Closing the write end is how a player is told the clip is over: it
                // makes the last of the sound out of what it has buffered and exits. So
                // waiting for that exit waits for the sound to end rather than for the
                // last byte to have been handed over, which is a fraction of a second
                // earlier and would step an autoplay run on over the tail of a clip.
                if (!gone) run.CloseInput();
                var code = run.WaitFor(DrainMs);

                // Exit zero is a player that did its job, however it got here: one that
                // closed the pipe and then exited cleanly has still played everything it
                // was given, and calling that a failure would put a red line under a
                // clip that was heard in full.
                if (code is int c && c != 0)
                    failure = $"{_player.Command} stopped: " + (run.Said() ?? $"exit code {c}.");
                else if (gone && code is null)
                    failure = $"{_player.Command} stopped: " +
                              (run.Said() ?? "it closed the pipe without saying why.");
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            // Nothing outlives its clip. A player still draining when the next one
            // starts would be heard over the top of it.
            run.Kill();
            run.Close();
            run.Playing = false;
        }

        // Raised last, and nothing on this thread runs afterwards, because a handler is
        // free to dispose this object or start the next clip from inside it.
        try
        {
            if (failure is not null) Failed?.Invoke(failure);
            // Read here rather than earlier: a stop during the drain is still a stop
            // that was asked for, and it must not advance the run either.
            if (!run.OnPurpose) Finished?.Invoke();
        }
        catch
        {
            // An exception escaping a background thread ends the process, and a handler
            // that throws is the caller's bug, not a reason to take the app down.
        }
    }

    public void Stop()
    {
        // In this order, and OnPurpose volatile: the pump learns the pipe has gone by
        // failing to write to it, so the reason has to be readable before the thing that
        // causes it. A pump that sees the break then always sees why.
        if (_run is { } run) { run.OnPurpose = true; run.Kill(); }
    }

    public string Problem()
    {
        if (LoadProblem is { } why) return why;

        // Whether a sound server is running, and whether it has a device, is what the
        // player finds out for itself. It says so and exits when it cannot, which
        // arrives as Failed in its own words, and that is better than a guess made here
        // in advance out of nothing.
        if (_player.Path is { } path && !File.Exists(path))
            return $"{_player.Command} was on PATH and is not there now ({path}).";
        return null;
    }

    public string Summary() =>
        $"{_player}, raw 16-bit PCM on a pipe" +
        (_player.Path is null ? "" : $" ({_player.Path})");

    /// <summary>Stop, and wait for the pump to let go of the player.</summary>
    private void Release()
    {
        Stop();
        var pump = _pump;
        _pump = null;

        // Disowned here rather than when the pump ends, so that a pump which does not
        // end still stops being this player's answer to IsPlaying and Finished.
        _run = null;
        if (pump is null || !pump.IsAlive) return;

        // Not when we ARE that thread: Finished is raised from it, and a handler that
        // starts the next clip or disposes the player would be waiting on itself.
        if (pump == Thread.CurrentThread) return;

        // Bounded, because a player that has stopped reading must not take the window
        // with it. The pump is a background thread and kills its own process, so a wait
        // that times out leaves nothing behind that is still holding a device.
        pump.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        // Disposing twice is ordinary here: AudioPreview disposes its output and the
        // window disposes AudioPreview.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Release();
        Finished = null;
        Failed = null;
    }
}
