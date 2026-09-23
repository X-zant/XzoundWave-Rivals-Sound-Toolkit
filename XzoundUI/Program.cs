using Avalonia;

namespace MRAudioKit.UI;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The verbs come first and never need a window, which is the whole point of
        // the split -- the same table runs from the command-line host.
        if (args.Length > 0 && Verbs.Dispatch(args, out var exit)) return exit;

        // Render the window to a file instead of a screen. This is how the layout gets
        // looked at on a machine with no display, and how a test can assert about it.
        if (args.Any(a => a.Equals("--uitest", StringComparison.OrdinalIgnoreCase)))
            return UiSelfTest.Run();

        var shot = Array.FindIndex(args, a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));
        if (shot >= 0)
            return Shot.Take(args.Length > shot + 1 ? args[shot + 1] : "window.png",
                             withGame: args.Any(a => a.Equals("--with-game", StringComparison.OrdinalIgnoreCase)),
                             skin: Array.FindIndex(args, a => a.Equals("--skin", StringComparison.OrdinalIgnoreCase)) is var k
                                   && k >= 0 && k + 1 < args.Length ? args[k + 1] : "1029001");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
