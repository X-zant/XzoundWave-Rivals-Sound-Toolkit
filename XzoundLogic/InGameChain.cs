using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MRAudioKit;

/// <summary>
/// Plays a sound the way the .bnk plays it, rather than the way the file sits on disk.
///
/// The audio is only half of what you hear in game. The bank trims the volume, shifts
/// the pitch, waits before starting and decides whether it repeats, and none of that is
/// in the file -- so a replacement previewed raw can sound nothing like the thing it
/// replaces. This applies the parts that can be applied honestly.
///
/// What it does NOT do, and says so rather than pretending: the low- and high-pass
/// filters (a Wwise-specific curve, not a plain cutoff), attenuation (needs a listener
/// position and a distance), HDR ducking (depends on everything else playing) and
/// anything driven by a game parameter at runtime. Those are listed in the column's
/// tooltip so the difference is visible instead of silently missing.
/// </summary>
public static class InGameChain
{
    /// <summary>
    /// A sound that repeats for ever would hold the player open and stall an autoplay
    /// run, so an endless loop is previewed as a few passes.
    /// </summary>
    public const int MaxLoops = 4;

    /// <param name="applied">What the bank does. Null or empty plays the file as it is.</param>
    /// <param name="described">
    /// What was actually applied, for the status line. Written even when nothing was,
    /// because "nothing" is the interesting answer for a sound someone expected the
    /// game to change.
    /// </param>
    public static ISampleProvider Build(WaveStream source, WwiseProps.Applied applied,
                                        out string described)
    {
        described = "";
        if (applied is null) return source.ToSampleProvider();

        var bits = new List<string>();

        // Volume and make-up gain add up in dB down the chain, the way Wwise sums
        // them, and the sum is what becomes a gain factor. A level a game parameter
        // drives is left out: the bank stores where it starts, not what is heard, and
        // applying a starting value of -96 dB would play silence for a sound the game
        // turns up.
        var db = applied.Props
            .Where(p => !p.Driven && p.Unit == WwiseProps.Unit.Db && p.Id is 0x00 or 0x06)
            .Sum(p => p.Float);

        // Pitch is in cents and Wwise applies it by resampling, so it moves the speed
        // with it -- the same trade a tape machine makes. Playback speed is the same
        // operation stated as a multiplier, so the two multiply together.
        var cents = applied.Props.Where(p => p.Unit == WwiseProps.Unit.Cents).Sum(p => p.Float);
        var rate = (float)Math.Pow(2, cents / 1200.0);
        foreach (var p in applied.Props.Where(p => p.Unit == WwiseProps.Unit.Multiplier))
            if (p.Float > 0.01f) rate *= p.Float;

        var delay = applied.Props.Where(p => p.Id is 0x0F or 0x3B).Sum(p => p.Float)
                    + applied.ActionDelay;

        var loops = applied.LoopCount switch
        {
            0 => MaxLoops,          // authored to repeat until stopped
            > 1 => applied.LoopCount.Value,
            _ => 1,
        };

        ISampleProvider chain = loops > 1
            ? new LoopingProvider(source, loops)
            : source.ToSampleProvider();

        if (Math.Abs(rate - 1) > 0.001f)
        {
            chain = new RateShift(chain, rate);
            bits.Add(cents != 0 ? $"{cents:+0.#;-0.#} cents" : $"{rate:0.##}x speed");
        }

        if (Math.Abs(db) > 0.01f)
        {
            chain = new VolumeSampleProvider(chain) { Volume = (float)Math.Pow(10, db / 20.0) };
            bits.Add($"{db:+0.#;-0.#} dB");
        }

        if (delay > 0.0005f)
        {
            chain = new OffsetSampleProvider(chain) { DelayBy = TimeSpan.FromSeconds(delay) };
            bits.Add($"{delay:0.###}s delay");
        }

        if (loops > 1)
            bits.Add(applied.LoopCount == 0 ? $"loops ({MaxLoops} shown)" : $"{loops}x");

        described = applied.Silenced
            ? $"SILENT — the .bnk turns this down by {-applied.StaticDb:0.#} dB and nothing " +
              "raises it, so the game plays nothing here"
            : bits.Count == 0 ? "the .bnk changes nothing you can hear"
            : string.Join(", ", bits);
        return chain;
    }

    /// <summary>
    /// What the preview cannot reproduce, named so its absence is not mistaken for
    /// the game leaving the sound alone.
    /// </summary>
    public static string Unapplied(WwiseProps.Applied applied)
    {
        if (applied is null) return "";
        var missing = new List<string>();

        if (applied.Props.Any(p => p.Id is 0x03 or 0x04)) missing.Add("filters");
        if (applied.Props.Any(p => p.Unit == WwiseProps.Unit.ObjectId && p.Id == 0x46))
            missing.Add("distance attenuation");
        if (applied.Props.Any(p => p.Id is >= 0x1B and <= 0x21)) missing.Add("HDR ducking");
        if (applied.Rtpc > 0) missing.Add($"{applied.Rtpc} runtime curve(s)");
        foreach (var p in applied.DrivenProps.Where(p => p.Unit == WwiseProps.Unit.Db))
            missing.Add($"{p.Name}, which the game drives");
        if (applied.StateGroups > 0) missing.Add("state changes");

        return missing.Count == 0 ? "" : string.Join(", ", missing);
    }

    /// <summary>
    /// Report a different sample rate than the source really has, which is exactly how
    /// a resampling pitch shift works: the samples are unchanged and the clock moves.
    /// Wwise's Pitch does the same thing, so speed and pitch move together here as
    /// they do in game.
    /// </summary>
    private sealed class RateShift(ISampleProvider source, float rate) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(
            Math.Clamp((int)(source.WaveFormat.SampleRate * rate), 4000, 192000),
            source.WaveFormat.Channels);

        public int Read(Span<float> buffer) => source.Read(buffer);
    }

    /// <summary>
    /// Plays the stream through several times. Rewinding the reader is what makes a
    /// loop, so this owns the seek rather than the caller.
    /// </summary>
    private sealed class LoopingProvider : ISampleProvider
    {
        private readonly WaveStream _stream;
        private readonly ISampleProvider _source;
        private readonly int _times;
        private int _played;

        public LoopingProvider(WaveStream stream, int times)
        {
            _stream = stream;
            _source = stream.ToSampleProvider();
            _times = times;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            var got = 0;
            while (got < buffer.Length)
            {
                var n = _source.Read(buffer[got..]);
                if (n > 0) { got += n; continue; }

                if (++_played >= _times) break;
                try { _stream.Position = 0; }
                catch { break; }          // a stream that will not rewind ends here
            }
            return got;
        }
    }
}
