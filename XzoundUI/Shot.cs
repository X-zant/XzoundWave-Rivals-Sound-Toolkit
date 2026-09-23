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
    public static int Take(string path, int width = 1440, int height = 840,
                           bool withGame = false, string skin = "1029001")
    {
        try
        {
            AppBuilder.Configure<App>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                      .SetupWithoutStarting();

            var window = new MainWindow { Width = width, Height = height };
            window.Show();

            // Real rows, not a sketch. A layout that only looks right with placeholder
            // text is a layout that has not been checked: the widths that matter are
            // the ones a 40-character event name and a Chinese category actually take.
            if (withGame)
            {
                // Synchronous on purpose: this thread is the UI thread and is about to
                // block on the result, so anything awaited would queue behind the wait.
                window.LoadGameSync();
                Dispatcher.UIThread.RunJobs();

                // What a loaded mod looks like, using the counts from the pak the
                // mockup was drawn with, so the two can be put side by side.
                window.PretendModded(new Dictionary<string, int>
                {
                    ["bnk_vo_1029001"] = 279, ["bnk_vo_1030001"] = 29,
                    ["bnk_vo_1024001"] = 73,  ["bnk_vo_1034001"] = 247,
                    ["bnk_vo_1016001"] = 62,  ["bnk_vo_1050001"] = 53,
                }, expandModded: true);
                window.ShowSkinSync(skin, "vo");
            }

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
