#if WINDOWS
using NAudio.Wave;

namespace MRAudioKit;

/// <summary>
/// The Windows output, which is what the app has always used.
///
/// NAudio rather than WPF's MediaPlayer: MediaPlayer is a front end for Windows Media
/// Player, so on an edition without Media Features -- an N build, or one where they
/// have been turned off -- it opens the file, reports nothing, and plays silence. That
/// is indistinguishable from a broken decoder, and it is what a tester hit on a machine
/// where every other check came back clean.
/// </summary>
public sealed class WaveOutAudio : IAudioOutput
{
    private WaveOut _out;
    private bool _stoppedOnPurpose;

    public event Action Finished;
    public event Action<string> Failed;

    public void Play(ISampleProvider source)
    {
        Release();
        try
        {
            _out = new WaveOut();
            _out.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null) Failed?.Invoke(e.Exception.Message);
                // Stopping on purpose is not the end of a clip, and must not advance
                // an autoplay run to the next one.
                if (!_stoppedOnPurpose) Finished?.Invoke();
            };
            _out.Init(source);
            _stoppedOnPurpose = false;
            _out.Play();
        }
        catch (Exception ex)
        {
            Release();
            Failed?.Invoke(ex.Message);
            // A clip that cannot play must still advance, or one bad file stalls a run.
            Finished?.Invoke();
        }
    }

    /// <summary>
    /// Whether sound is coming out right now. Polling this is how a headless check can
    /// watch a clip through: PlaybackStopped is posted to the synchronization context
    /// that created the player, so a caller blocking that thread never sees the event.
    /// </summary>
    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;

    public void Stop()
    {
        _stoppedOnPurpose = true;
        try { _out?.Stop(); } catch { }
    }

    public string Problem()
    {
        try
        {
            return WaveOut.DeviceCount > 0 ? null : "Windows reports no audio output device.";
        }
        catch (Exception ex) { return "could not query audio output: " + ex.Message; }
    }

    public string Summary()
    {
        try
        {
            var n = WaveOut.DeviceCount;
            if (n == 0) return "none";
            return $"{n} device(s), default: {WaveOut.GetCapabilities(0).ProductName}";
        }
        catch (Exception ex) { return "unknown — " + ex.Message; }
    }

    private void Release()
    {
        _stoppedOnPurpose = true;
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        _out = null;
    }

    public void Dispose() => Release();
}
#endif
