using System.Windows;

namespace MRAudioKit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless verification path -- see SelfTest.
        if (Array.FindIndex(e.Args, a => a.Equals("--bankmax", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            Shutdown(SelfTest.BankMax());
            return;
        }

        var tts = Array.FindIndex(e.Args, a => a.Equals("--tts", StringComparison.OrdinalIgnoreCase));
        if (tts >= 0 && tts + 3 < e.Args.Length)
        {
            Shutdown(SelfTest.TtsTest(e.Args[tts + 1], int.Parse(e.Args[tts + 2]), int.Parse(e.Args[tts + 3])));
            return;
        }

        var tb = Array.FindIndex(e.Args, a => a.Equals("--testbank", StringComparison.OrdinalIgnoreCase));
        if (tb >= 0 && tb + 3 < e.Args.Length)
        {
            Shutdown(SelfTest.TestBank(e.Args[tb + 1], e.Args[tb + 2], e.Args[tb + 3],
                                       tb + 4 < e.Args.Length ? e.Args[tb + 4] : null));
            return;
        }

        var bd = Array.FindIndex(e.Args, a => a.Equals("--build", StringComparison.OrdinalIgnoreCase));
        if (bd >= 0 && bd + 3 < e.Args.Length)
        {
            Shutdown(SelfTest.BuildMod(e.Args[bd + 1], e.Args[bd + 2], e.Args[bd + 3]));
            return;
        }

        var m = Array.FindIndex(e.Args, a => a.Equals("--media", StringComparison.OrdinalIgnoreCase));
        if (m >= 0 && m + 1 < e.Args.Length)
        {
            var rep = Array.FindIndex(e.Args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
            var bank = Array.FindIndex(e.Args, a => a.Equals("--bank", StringComparison.OrdinalIgnoreCase));
            var ids = e.Args.Skip(m + 1).TakeWhile(a => !a.StartsWith("--")).ToArray();
            Shutdown(SelfTest.Media(ids,
                bank >= 0 && bank + 1 < e.Args.Length ? e.Args[bank + 1] : null,
                rep >= 0 && rep + 1 < e.Args.Length ? e.Args[rep + 1] : null));
            return;
        }

        var i = Array.FindIndex(e.Args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < e.Args.Length)
        {
            var code = SelfTest.Run(e.Args[i + 1], i + 2 < e.Args.Length ? e.Args[i + 2] : null);
            Shutdown(code);
            return;
        }

        // A parse failure deep in a soundbank should surface as a message, not a
        // silent process exit -- modders run this without a console attached.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "MRAudioKit",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
