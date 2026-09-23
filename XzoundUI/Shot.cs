using System.IO;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MRAudioKit.UI;

/// <summary>
/// Render the window to a file instead of a screen.
///
/// A layout claim is a visual claim, and the usual way to check one is to open the app
/// and look — which needs a screen, needs the person to be at it, and proves nothing
/// afterwards. Avalonia can run with no windowing system at all and hand back the
/// frame it would have drawn, so the layout can be looked at on any machine and, more
/// usefully, asserted about by a test.
///
/// This is also the honest way to show work: a screenshot of what was actually built,
/// rather than a description of it.
/// </summary>
public static class Shot
{
    /// <summary>XzoundWave --shot &lt;file.png&gt; [width] [height]</summary>
    public static int Take(string path, int width = 1440, int height = 840)
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();

            var window = new MainWindow { Width = width, Height = height };
            window.Show();

            // Let layout settle before asking for the pixels: a frame captured during
            // the first pass shows controls at their unmeasured size, which reads as a
            // broken layout when nothing is wrong.
            Dispatcher.UIThread.RunJobs();

            var frame = window.CaptureRenderedFrame()
                        ?? throw new InvalidOperationException("no frame was rendered");

            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var file = File.Create(full)) frame.Save(file);

            Console.WriteLine($"{width}x{height} -> {full}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not render the window: " + ex);
            return 1;
        }
    }
}
