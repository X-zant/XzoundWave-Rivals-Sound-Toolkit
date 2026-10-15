using Avalonia;
using Avalonia.Media;
#if BUNDLED_FONTS
using Avalonia.Media.Fonts;
#endif

namespace MRAudioKit.UI;

/// <summary>
/// The fonts the window brings with it, everywhere but Windows.
///
/// The layout was drawn against Segoe UI, and on Windows that is what it gets. On Linux
/// Avalonia takes whatever fontconfig calls sans-serif, which on a great many machines is
/// DejaVu Sans: wide enough that every toolbar wrapped onto a second row. Inter is set as
/// the default family instead, so the window is the same width on every distribution.
///
/// Chinese is the other half. The Category column is the game's own Chinese, and so are
/// the subtitles in Chinese(CN); a host with no font that can draw the script showed a
/// column of empty boxes, which is why the column used to fall back to English there.
/// Noto Sans SC is registered as the fallback for any character Inter does not have, so
/// it is used for exactly the text that needs it and nothing else.
///
/// One method for every AppBuilder in the program, because the render harness builds its
/// own: a fix that reached only the real window would be one the renders could not show.
/// </summary>
public static class Fonts
{
    /// <summary>The family name inside NotoSansSC-Regular.otf.</summary>
    public const string ChineseFamily = "Noto Sans SC";

    public static AppBuilder UseBundledFonts(this AppBuilder builder)
    {
#if BUNDLED_FONTS
        // A build with no runtime identifier carries the fonts on Windows too, so that one
        // development build runs either way; Windows itself still gets its own.
        if (OperatingSystem.IsWindows()) return builder;

        return builder
            .WithInterFont()
            .ConfigureFonts(fonts => fonts.AddFontCollection(
                new EmbeddedFontCollection(new Uri("fonts:XzoundWave"), new Uri("avares://XzoundWave/Assets/Fonts"))))
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "fonts:Inter#Inter",
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily("fonts:XzoundWave#" + ChineseFamily) }],
            });
#else
        return builder;
#endif
    }
}
