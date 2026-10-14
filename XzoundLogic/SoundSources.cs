namespace MRAudioKit;

/// <summary>
/// Why a row has no audio file behind it. Two kinds of Sound have none: one whose source is a
/// generator built into Wwise rather than a recording (3,904 of the game's 480,089 Sounds:
/// silence, controller vibration, an impact or wind synthesizer, live input), and a streamed
/// one whose loose file the game does not ship (80, all in map ambience banks). Both used to
/// answer Play with "no bytes found", which reads as a fault in the tool.
/// </summary>
public static class SoundSources
{
    /// <summary>A row's source is a generator, not a recording: its codec names a Wwise plugin.</summary>
    public static bool IsGenerator(SoundRow row) =>
        row.Codec is { } c && c.StartsWith("Ak", StringComparison.Ordinal);

    /// <summary>What a generator source makes, in words.</summary>
    public static string GeneratorName(string codec) => codec switch
    {
        "AkFxSrcSilenceSource" => "silence",
        "AkMotionSourceSource" => "controller vibration",
        "AkImpacterSource" => "an impact synthesizer (it builds impacts from samples the bank holds)",
        "AkSoundSeedWind" or "AkSoundSeedWoosh" => "a wind synthesizer",
        "AkAudioInputSource" => "live audio input",
        "AkSineSource" or "AkToneSource" or "AkFxSrcSineSource" or "AkFxSrcToneSource" => "a test tone",
        _ => $"a generator built into Wwise ({codec})",
    };

    /// <summary>
    /// Why this row has no audio to play or extract, or null when it should have some.
    /// <paramref name="shipped"/>: the game has the medium, embedded or loose.
    /// </summary>
    public static string NoAudioReason(SoundRow row, bool shipped)
    {
        if (IsGenerator(row))
            return $"this sound plays {GeneratorName(row.Codec)}, not a recording -- there is no audio file to play or replace.";
        if (!shipped && row.Streamed)
            return "the game streams this sound from a file it does not ship, so it is silent in a match. " +
                   "A file staged on it is added at the path the game looks in.";
        return null;
    }
}
