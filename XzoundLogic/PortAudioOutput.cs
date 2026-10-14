using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Pa = MRAudioKit.PortAudioNative;

namespace MRAudioKit;

/// <summary>
/// Sound on every platform, through PortAudio.
///
/// This is the output that makes the portable build audible: WaveOut is packaged only
/// for a windows target framework, so a net10.0 build has no way to reach it. Every
/// window head now builds for the windows moniker as well and takes WaveOut there, so
/// what is left to this class is Linux and macOS, and a net10.0 host on Windows.
/// PortAudio is MIT licensed and is one small C library in front of WASAPI, ALSA, JACK
/// and CoreAudio, so the same file covers all three platforms.
///
/// The library is not a NuGet dependency and is not required: when it cannot be loaded
/// <see cref="TryCreate"/> returns null and the app falls back, rather than failing to
/// start. Nothing above this class knows which backend it got.
/// </summary>
public sealed class PortAudioOutput : IAudioOutput
{
    /// <summary>
    /// How much is handed over at a time. This is also how long <see cref="Stop"/> can
    /// take to be noticed, because the feeding thread only looks between writes, so it
    /// is kept to roughly twenty milliseconds of audio rather than sized for throughput.
    /// </summary>
    private const int FramesPerWrite = 1024;

    private static readonly Lazy<string> Startup = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>How many feeding threads are inside the library right now.</summary>
    private static int _busy;

    /// <summary>
    /// One clip's worth of state, owned by the thread feeding it rather than by the
    /// player.
    ///
    /// It has to be per clip because <see cref="Release"/> gives up waiting after two
    /// seconds: a thread wedged in the device outlives the clip that started it, and
    /// shared flags would let it answer for the clip playing now, reporting the wrong
    /// thing through <see cref="IsPlaying"/> or raising <see cref="Finished"/> for a
    /// clip nobody is listening to and advancing an autoplay run by two.
    /// </summary>
    private sealed class Run
    {
        public volatile bool Playing;
        public volatile bool Stop;

        /// <summary>
        /// Set by <see cref="PortAudioOutput.Stop"/> and by starting something else. The
        /// whole reason <see cref="Finished"/> exists is to advance an autoplay run, so
        /// a stop the user asked for must not raise it.
        /// </summary>
        public volatile bool OnPurpose;
    }

    private volatile Run _run;
    private Thread _feed;
    private int _disposed;

    public event Action Finished;
    public event Action<string> Failed;

    private PortAudioOutput() { }

    /// <summary>
    /// An output backed by PortAudio, or null when this machine has no usable copy of
    /// the library. Null is the signal to fall back: it is not an error, and a build
    /// with no PortAudio anywhere still runs.
    /// </summary>
    public static PortAudioOutput TryCreate() => LoadProblem is null ? new PortAudioOutput() : null;

    /// <summary>Why PortAudio is unusable here, or null when it is usable.</summary>
    public static string LoadProblem => Startup.Value;

    /// <summary>
    /// Load and initialise once per process. Pa_Initialize walks every host API and
    /// enumerates devices, which is far too much to repeat for each preview, and
    /// AudioOut builds a fresh output for every clip and for every diagnostics line.
    /// </summary>
    private static string Start()
    {
        int err;
        try
        {
            err = Pa.Initialize();
        }
        catch (DllNotFoundException)
        {
            return OperatingSystem.IsWindows()
                ? "PortAudio is not on this machine (portaudio.dll). Put it beside the exe."
                : OperatingSystem.IsMacOS()
                ? "PortAudio is not on this machine (libportaudio.2.dylib). brew install portaudio."
                : "PortAudio is not on this machine (libportaudio.so.2). " +
                  "Install libportaudio2, or portaudio from your distribution.";
        }
        catch (BadImageFormatException)
        {
            return "the PortAudio library found is built for a different architecture.";
        }
        catch (EntryPointNotFoundException ex)
        {
            // A file by the right name that is not the right library, which is a
            // different thing to fix from one that is missing.
            return "the PortAudio library found is not PortAudio: " + ex.Message;
        }
        catch (Exception ex)
        {
            return "PortAudio could not be loaded: " + ex.Message;
        }

        if (err != Pa.NoError) return "PortAudio could not start: " + Pa.ErrorText(err);

        // Matched with the Pa_Initialize above. Skipping it leaks the host API's own
        // threads, which on ALSA keeps a process alive after the window has closed.
        //
        // Not while a thread is still inside the library, though: Pa_Terminate frees
        // streams other threads are blocked in, and a quit during playback would come
        // apart inside native code where nothing managed can catch it. A leak at the
        // moment of exit costs nothing by comparison.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (Volatile.Read(ref _busy) == 0) Pa.Terminate(); } catch { }
        };
        return null;
    }

    public bool IsPlaying => _run is { Playing: true };

    public void Play(ISampleProvider source)
    {
        Release();

        if (source is null) { Finished?.Invoke(); return; }

        IntPtr stream;
        ISampleProvider chain;
        int channels;
        string why;
        try
        {
            if (!Open(source, out stream, out chain, out channels, out why))
            {
                Failed?.Invoke(why);
                // A clip that cannot play must still advance, or one bad file stalls a run.
                Finished?.Invoke();
                return;
            }

            var err = Pa.StartStream(stream);
            if (err != Pa.NoError)
            {
                try { Pa.CloseStream(stream); } catch { }
                Failed?.Invoke("PortAudio could not start the stream: " + Pa.ErrorText(err));
                Finished?.Invoke();
                return;
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
            Finished?.Invoke();
            return;
        }

        var run = new Run { Playing = true };
        _run = run;
        _feed = new Thread(() => Feed(run, stream, chain, channels))
        {
            IsBackground = true,
            Name = "XzoundWave audio",
        };
        _feed.Start();
    }

    /// <summary>
    /// Open the default device for what this clip actually is, and if that is refused,
    /// for something near it that the device will take.
    ///
    /// Both retries are for real hardware rather than theoretical: a device exposed as
    /// stereo only turns a mono voice line into paInvalidChannelCount, and a device in
    /// exclusive mode at 48 kHz refuses a 44.1 kHz clip outright. Both come back as a
    /// failed open, which would otherwise read as "PortAudio does not work here".
    /// </summary>
    private static bool Open(ISampleProvider source, out IntPtr stream, out ISampleProvider chain,
                             out int channels, out string why)
    {
        stream = IntPtr.Zero;
        chain = source;
        channels = source.WaveFormat.Channels;
        why = null;

        var rate = source.WaveFormat.SampleRate;
        var deviceRate = Pa.TryDefaultOutput(out var info, out _) ? (int)info.DefaultSampleRate : 0;

        var tries = new List<(Func<ISampleProvider> Make, int Channels, int Rate)>
        {
            (() => source, channels, rate),
        };
        if (channels == 1)
            tries.Add((() => new MonoToStereoSampleProvider(source), 2, rate));
        if (deviceRate > 0 && deviceRate != rate)
        {
            tries.Add((() => new WdlResamplingSampleProvider(source, deviceRate), channels, deviceRate));
            if (channels == 1)
                tries.Add((() => new MonoToStereoSampleProvider(
                                     new WdlResamplingSampleProvider(source, deviceRate)), 2, deviceRate));
        }

        var last = Pa.NoError;
        foreach (var attempt in tries)
        {
            // Building the chain only once an open has succeeded keeps the source
            // untouched: a failed open reads no samples, so the next attempt starts
            // from the beginning of the clip.
            var err = Pa.OpenDefaultStream(out stream, 0, attempt.Channels, Pa.Float32,
                                           attempt.Rate, Pa.FramesUnspecified,
                                           IntPtr.Zero, IntPtr.Zero);
            if (err == Pa.NoError)
            {
                // A provider's constructor can throw, and by here the device is already
                // held. Nothing else knows the handle yet, so this is the only place it
                // can be given back.
                try { chain = attempt.Make(); }
                catch
                {
                    try { Pa.CloseStream(stream); } catch { }
                    stream = IntPtr.Zero;
                    throw;
                }
                channels = attempt.Channels;
                return true;
            }
            last = err;
            stream = IntPtr.Zero;
        }

        why = $"PortAudio would not open an output at {rate} Hz, {channels} channel(s): " +
              Pa.ErrorText(last);
        return false;
    }

    /// <summary>
    /// Read from the chain and hand it over until the clip ends or a stop is asked for.
    ///
    /// This thread owns the stream handle for its whole life. PortAudio's blocking calls
    /// are not safe to use from two threads at once, so stopping is a flag this thread
    /// reads rather than a call another thread makes, and there is no lock to deadlock on.
    /// </summary>
    private void Feed(Run run, IntPtr stream, ISampleProvider source, int channels)
    {
        string failure = null;
        Interlocked.Increment(ref _busy);
        try
        {
            var buffer = new float[FramesPerWrite * channels];
            var filled = 0;
            var ended = false;

            while (!run.Stop && !ended)
            {
                while (filled < buffer.Length)
                {
                    var n = source.Read(buffer.AsSpan(filled));
                    // Zero is the end of the clip. A short read is not: several of the
                    // providers in the chain stop at their own boundaries.
                    if (n <= 0) { ended = true; break; }
                    filled += n;
                }

                var frames = filled / channels;
                if (frames > 0)
                {
                    var err = Pa.WriteStream(stream, buffer, (nuint)frames);
                    if (err != Pa.NoError && err != Pa.OutputUnderflowed)
                    {
                        failure = "PortAudio stopped playing: " + Pa.ErrorText(err);
                        break;
                    }

                    // Whole frames only, so anything left over leads the next write.
                    var rest = filled - frames * channels;
                    if (rest > 0) Array.Copy(buffer, frames * channels, buffer, 0, rest);
                    filled = rest;
                }
                else if (!ended)
                {
                    break;      // nothing read and not at the end: no way forward
                }
            }

            // Pa_StopStream waits for what has been buffered to be heard; Pa_AbortStream
            // throws it away. That is the difference between a clip ending and someone
            // pressing Stop, and using the wrong one clips the last buffer off every sound.
            try
            {
                if (run.OnPurpose) Pa.AbortStream(stream); else Pa.StopStream(stream);
            }
            catch { }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            try { Pa.CloseStream(stream); } catch { }
            run.Playing = false;
            Interlocked.Decrement(ref _busy);
        }

        // Raised last, and nothing on this thread runs afterwards, because a handler is
        // free to dispose this object or start the next clip from inside it.
        try
        {
            if (failure is not null) Failed?.Invoke(failure);
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
        // In this order, and both volatile: the feeding thread tests Stop first, so a
        // thread that sees the stop is guaranteed to see why it was asked for, and can
        // never abort a clip while believing it ended by itself.
        if (_run is { } run) { run.OnPurpose = true; run.Stop = true; }
    }

    public string Problem()
    {
        if (LoadProblem is { } why) return why;
        try
        {
            var count = Pa.GetDeviceCount();
            if (count < 0) return "PortAudio could not list devices: " + Pa.ErrorText(count);
            if (count == 0) return "PortAudio found no audio devices.";
            if (Pa.GetDefaultOutputDevice() == Pa.NoDevice)
                return "PortAudio found no default output device.";
            return null;
        }
        catch (Exception ex) { return "could not query audio output: " + ex.Message; }
    }

    public string Summary()
    {
        if (LoadProblem is { } why) return "none (" + why + ")";
        try
        {
            var count = Pa.GetDeviceCount();
            if (count <= 0) return "none";
            if (!Pa.TryDefaultOutput(out var info, out var name)) return $"{count} device(s), no default output";

            // Which host API it went through is worth a word: it is the difference
            // between ALSA talking to the card and ALSA talking to a stopped PulseAudio,
            // and the two sound identical from here.
            var host = Pa.HostApiName(info.HostApi);
            var via = string.IsNullOrEmpty(host) ? "" : $" via {host}";
            var version = Pa.VersionText() ?? "PortAudio";
            return $"{version}: {count} device(s), default: {name ?? "unnamed"}{via}";
        }
        catch (Exception ex) { return "unknown, " + ex.Message; }
    }

    /// <summary>Stop and let go of the device, waiting for the feeding thread to finish.</summary>
    private void Release()
    {
        Stop();
        var feed = _feed;
        _feed = null;

        // Disowned here rather than when the thread ends, so that a thread which does
        // not end still stops being this player's answer to IsPlaying and Finished.
        _run = null;
        if (feed is null || !feed.IsAlive) return;

        // Not when we ARE that thread: Finished is raised from it, and a handler that
        // starts the next clip or disposes the player would be waiting on itself.
        if (feed == Thread.CurrentThread) return;

        // Bounded, because a host API wedged inside Pa_WriteStream must not take the
        // window with it. The thread is a background one and closes its own stream, so
        // a wait that times out costs nothing that is still holding the device.
        feed.Join(TimeSpan.FromSeconds(2));
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
