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
/// target framework; the portable build has nothing yet and says so rather than
/// pretending. Both are temporary: one cross-platform output replaces the pair, and
/// then this whole file collapses to a constructor call.
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
#if WINDOWS
        return Factory is not null ? Factory() : new WaveOutAudio();
#else
        return Factory is not null ? Factory() : new NoAudio();
#endif
    }
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
    public event Action<string> Failed;

    public string Problem() =>
        "this build has no audio output — decoding and building work, playback does not.";

    public string Summary() => "none (no output backend in this build)";
    public void Dispose() { Failed = null; Finished = null; }
}
