using NAudio.Wave;

namespace MRAudioKit;

/// <summary>
/// Somewhere for sound to go.
///
/// Everything above this line is portable: vgmstream decodes, NAudio's sample-provider
/// layer applies what the .bnk does to a sound, and both are plain managed code. Only
/// the last step — handing samples to a device — is a platform's own business, so this
/// is the whole of the audio boundary rather than one seam among several.
///
/// Deliberately small. The app plays one short clip at a time and needs to know when it
/// ended by itself, because that is what lets autoplay move on without polling.
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Start this stream. Replaces anything already playing.</summary>
    void Play(ISampleProvider source);

    /// <summary>Stop on purpose, which must NOT raise <see cref="Finished"/>.</summary>
    void Stop();

    bool IsPlaying { get; }

    /// <summary>A clip reached its end by itself. The signal autoplay advances on.</summary>
    event Action Finished;

    /// <summary>Playback failed, as opposed to the decode failing.</summary>
    event Action<string> Failed;

    /// <summary>Null when sound can come out, or why it cannot.</summary>
    string Problem();

    /// <summary>What the devices are, for the diagnostics report.</summary>
    string Summary();
}

/// <summary>
/// Which output this build has.
///
/// The Windows build keeps NAudio's WaveOut, which is packaged only for a windows
/// target framework. Everywhere else there are two, tried in order, because the better
/// one needs a library the machine may not have and the other needs nothing at all:
/// that is what makes a release that installs nothing able to make a sound on a stock
/// desktop rather than only on a prepared one.
/// </summary>
public static class AudioOut
{
    /// <summary>
    /// Set by a host that has its own output. Left alone, the platform's default is
    /// used -- so nothing has to be wired up for the common case.
    /// </summary>
    public static Func<IAudioOutput> Factory { get; set; }

    public static IAudioOutput Create()
    {
        if (Factory is not null) return Factory();
#if WINDOWS
        // Windows keeps WaveOut. PortAudio exists here to give the other platforms a
        // voice at all, and swapping a working backend for a newer one on the platform
        // that never needed it would be a change with a downside and no upside -- it
        // would also make the Windows build depend on shipping a native library it
        // currently does without.
        return new WaveOutAudio();
#else
        // Everywhere else, in order of how well they do the job. PortAudio is the better
        // output -- one library in front of the host's own API, with no process between
        // this and the device -- so it wins whenever the machine has it. A desktop that
        // has no libportaudio still ships a command that plays a sound, and piping to
        // that needs nothing installed, which is the whole point of the release being
        // one file. NoAudio is last and is not a failure: decoding, building and
        // measuring all work without a device.
        //
        // Its reason names both attempts, because "PortAudio is missing" on its own
        // sends the reader to install something that would not have been needed if the
        // second attempt had found a player either.
        return PortAudioOutput.TryCreate()
            ?? (IAudioOutput)PipeAudioOutput.TryCreate()
            ?? new NoAudio { Reason = Nowhere() };
#endif
    }

#if !WINDOWS
    /// <summary>
    /// Why nothing can be heard, answered for the machine this is actually running on
    /// rather than for the one the build was made for.
    ///
    /// This build has no Windows output compiled into it, so on Windows the honest
    /// answer is that the reader is holding the wrong one of the two, not that their
    /// machine is missing three Linux sound servers. Naming PipeWire, PulseAudio and
    /// ALSA to somebody on Windows sends them off to install something that could never
    /// have helped, and the tree carries several files of this name, so being handed the
    /// portable one is an easy and quiet mistake to make.
    /// </summary>
    private static string Nowhere() =>
        OperatingSystem.IsWindows()
            ? "this is the portable build, which carries no Windows audio output. " +
              "The Windows build of the window is the one beside it, under " +
              "net10.0-windows, and it plays through the system's own device."
            : $"{PortAudioOutput.LoadProblem} {PipeAudioOutput.LoadProblem}";
#endif
}

/// <summary>
/// No device, stated plainly. A clip "plays" instantly and reports finished, so an
/// autoplay run walks the list rather than stalling on the first sound, and the
/// diagnostics say why nothing was heard.
/// </summary>
public sealed class NoAudio : IAudioOutput
{
    public void Play(ISampleProvider source) => Finished?.Invoke();
    public void Stop() { }
    public bool IsPlaying => false;
    public event Action Finished;

    /// <summary>Never raised: nothing can go wrong on the way to nowhere.</summary>
    public event Action<string> Failed { add { } remove { } }

    /// <summary>
    /// Why there is no sound. Given by whoever fell back to this, because the reason is
    /// theirs to know: "no output backend in this build" was true when nothing else
    /// existed, and became a lie the moment a backend shipped that the machine simply
    /// does not have the library for. Silence with the wrong explanation is worse than
    /// silence, because it sends the reader to fix the wrong thing.
    /// </summary>
    public string Reason { get; init; }

    // No dash on either branch. The first is one clause and does not need one; the second
    // is joined to a Reason that is two backends' own explanations and already ends in a
    // full stop, so what follows it has to be a sentence rather than an aside.
    public string Problem() => Reason is null
        ? "this build has no audio output, so decoding and building work but playback does not."
        : Reason.TrimEnd().TrimEnd('.') + ". Decoding and building work, playback does not.";

    public string Summary() => Reason is null
        ? "none (no output backend in this build)" : "none (" + Reason + ")";
    public void Dispose() => Finished = null;
}
