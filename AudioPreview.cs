using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace MRAudioKit;

/// <summary>
/// wem -> wav -> speakers. vgmstream does the decoding; this only decides WHICH
/// bytes to hand it, which is the part that is easy to get wrong.
/// </summary>
public sealed class AudioPreview : IDisposable
{
    private readonly MediaPlayer _player = new();

    /// <summary>Raised when a clip finishes on its own, which is what lets one
    /// sound lead into the next without polling or guessing at durations.</summary>
    public event Action Finished;
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "MRAudioKit");
    public string VgmstreamPath { get; set; }

    public AudioPreview()
    {
        Directory.CreateDirectory(_cache);
        // A clip that fails to open must advance too, or one bad file stalls the run.
        _player.MediaEnded += (_, _) => Finished?.Invoke();
        _player.MediaFailed += (_, _) => Finished?.Invoke();
    }

    /// <summary>
    /// The loose streamed file when one exists, otherwise the bank's copy.
    /// Getting this order wrong makes every voice line a 0.03 s click, because the
    /// bank embeds only a prefetch stub of streamed voice.
    /// </summary>
    public byte[] Bytes(GameSession session, SkinSounds sounds, SoundRow row)
    {
        // Bytes we were handed win: a bank opened from a mod reuses the shipped media
        // ids, so asking the game first would play the vanilla sound instead.
        if (sounds.Raw.TryGetValue(row.MediaId, out var held) && held is { Length: > 0 })
        {
            row.Origin = "opened bank";
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
                row.Origin = "bank (embedded)";
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
            error = "vgmstream-cli.exe not set — point Settings at it to enable playback.";
            return null;
        }
        var wem = WriteWem(data, mediaId);
        var wav = Path.Combine(_cache, $"{mediaId}.wav");
        try
        {
            Run($"-o \"{wav}\" \"{wem}\"");
            if (!File.Exists(wav)) { error = "vgmstream produced no output."; return null; }
            return wav;
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

    private string Run(string args)
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
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    public void Play(string wavPath)
    {
        _player.Stop();
        _player.Open(new Uri(wavPath));
        _player.Play();
    }

    public void Stop() => _player.Stop();

    public void Dispose() => _player.Close();
}
