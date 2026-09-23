using System.Windows;

namespace MRAudioKit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A windowed application gets no console, so every verb would print into
        // nothing when started from Explorer or cmd. Attach to the caller's console if
        // there is one; --doctor also shows its output when there is not.
        if (e.Args.Any(a => a.StartsWith("--", StringComparison.Ordinal)))
            ConsoleBridge.Attach();

        // How the diagnostics reach someone who has no console. A windowed build has
        // nowhere to print, so it answers with a dialog; a command-line host leaves
        // this alone and the text goes to the console.
        EnvCheck.Tell = (title, body, fatal) => MessageBox.Show(
            body, title, MessageBoxButton.OK,
            fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);

        // Two verbs that only mean something here: they assert against this app's own
        // XAML -- its control templates and its context menu -- so they live with the
        // thing they test rather than in the shared table.
        if (Array.FindIndex(e.Args, a => a.Equals("--themetest", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            Shutdown(MenuSelfTest.RunTheme());
            return;
        }

        if (Array.FindIndex(e.Args, a => a.Equals("--menutest", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            Shutdown(MenuSelfTest.Run());
            return;
        }

        // Everything else. None of it needs a message pump, which is why the same
        // table can be run from a host that has no windows at all.
        if (Verbs.Dispatch(e.Args, out var exit))
        {
            Shutdown(exit);
            return;
        }

        // Say what is wrong before the window appears, rather than letting it surface
        // later as an import that quietly fails.
        if (!EnvCheck.WarnIfNeeded()) { Shutdown(1); return; }

        // A parse failure deep in a soundbank should surface as a message, not a
        // silent process exit -- modders run this without a console attached.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "XzoundWave",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
