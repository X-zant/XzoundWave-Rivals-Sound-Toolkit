using System.Windows;

namespace MRAudioKit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless verification path -- see SelfTest.
        var w2w = Array.FindIndex(e.Args, a => a.Equals("--wav2wem", StringComparison.OrdinalIgnoreCase));
        if (w2w >= 0 && w2w + 2 < e.Args.Length)
        {
            Shutdown(SelfTest.WavToWem(e.Args[w2w + 1], e.Args[w2w + 2]));
            return;
        }

        var vt = Array.FindIndex(e.Args, a => a.Equals("--vorbistest", StringComparison.OrdinalIgnoreCase));
        if (vt >= 0 && vt + 2 < e.Args.Length)
        {
            Shutdown(SelfTest.VorbisTest(e.Args[vt + 1], e.Args[vt + 2]));
            return;
        }

        var srt = Array.FindIndex(e.Args, a => a.Equals("--setuproundtrip", StringComparison.OrdinalIgnoreCase));
        if (srt >= 0 && srt + 1 < e.Args.Length)
        {
            Shutdown(SelfTest.SetupRoundTrip(e.Args[srt + 1]));
            return;
        }

        var dm = Array.FindIndex(e.Args, a => a.Equals("--dumpmedia", StringComparison.OrdinalIgnoreCase));
        if (dm >= 0 && dm + 2 < e.Args.Length)
        {
            Shutdown(SelfTest.DumpMedia(e.Args[dm + 1], e.Args[dm + 2]));
            return;
        }

        var md = Array.FindIndex(e.Args, a => a.Equals("--mountdiag", StringComparison.OrdinalIgnoreCase));
        if (md >= 0 && md + 1 < e.Args.Length)
        {
            Shutdown(MountDiag.Run(e.Args[md + 1]));
            return;
        }

        var ob = Array.FindIndex(e.Args, a => a.Equals("--openbank", StringComparison.OrdinalIgnoreCase));
        if (ob >= 0 && ob + 1 < e.Args.Length)
        {
            Shutdown(SelfTest.OpenBank(e.Args[ob + 1],
                ob + 2 < e.Args.Length ? int.Parse(e.Args[ob + 2]) : 8));
            return;
        }

        var tr = Array.FindIndex(e.Args, a => a.Equals("--translate", StringComparison.OrdinalIgnoreCase));
        if (tr >= 0 && tr + 1 < e.Args.Length)
        {
            Shutdown(SelfTest.TranslateCategories(e.Args[tr + 1],
                e.Args.Any(a => a.Equals("--fetch", StringComparison.OrdinalIgnoreCase))));
            return;
        }

        if (Array.FindIndex(e.Args, a => a.Equals("--doctor", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            Shutdown(EnvCheck.Run());
            return;
        }

        if (Array.FindIndex(e.Args, a =>
                a.Equals("--licences", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--licenses", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            Shutdown(Bundled.PrintLicences());
            return;
        }

        var mp = Array.FindIndex(e.Args, a => a.Equals("--mediapath", StringComparison.OrdinalIgnoreCase));
        if (mp >= 0)
        {
            Shutdown(MediaPathSelfTest.Run(
                mp + 1 < e.Args.Length && int.TryParse(e.Args[mp + 1], out var n) ? n : 400));
            return;
        }

        if (Array.FindIndex(e.Args, a => a.Equals("--rowfilters", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            Shutdown(RowFilterSelfTest.Run());
            return;
        }

        if (Array.FindIndex(e.Args, a => a.Equals("--menutest", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            Shutdown(MenuSelfTest.Run());
            return;
        }

        if (Array.FindIndex(e.Args, a => a.Equals("--tags", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            Shutdown(TagSelfTest.Run());
            return;
        }

        if (Array.FindIndex(e.Args, a => a.Equals("--banks", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            Shutdown(SelfTest.Banks());
            return;
        }

        var mg = Array.FindIndex(e.Args, a => a.Equals("--merge", StringComparison.OrdinalIgnoreCase));
        if (mg >= 0 && mg + 3 < e.Args.Length)
        {
            Shutdown(SelfTest.Merge(e.Args[mg + 1], e.Args[mg + 2], e.Args[mg + 3]));
            return;
        }

        var bt = Array.FindIndex(e.Args, a => a.Equals("--bulktest", StringComparison.OrdinalIgnoreCase));
        if (bt >= 0 && bt + 4 < e.Args.Length)
        {
            Shutdown(BulkSelfTest.Run(e.Args[bt + 1], e.Args[bt + 2], e.Args[bt + 3], e.Args[bt + 4]));
            return;
        }

        if (Array.FindIndex(e.Args, a => a.Equals("--medialayout", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            Shutdown(MediaLayout.Run());
            return;
        }

        var sp = Array.FindIndex(e.Args, a => a.Equals("--subprobe", StringComparison.OrdinalIgnoreCase));
        if (sp >= 0 && sp + 1 < e.Args.Length)
        {
            Shutdown(SubtitleProbe.Run(e.Args[sp + 1]));
            return;
        }

        var cp = Array.FindIndex(e.Args, a => a.Equals("--catprobe", StringComparison.OrdinalIgnoreCase));
        if (cp >= 0 && cp + 1 < e.Args.Length)
        {
            Shutdown(CategoryProbe.Run(e.Args[cp + 1]));
            return;
        }

        var vo = Array.FindIndex(e.Args, a => a.Equals("--volume", StringComparison.OrdinalIgnoreCase));
        if (vo >= 0 && vo + 3 < e.Args.Length)
        {
            Shutdown(SelfTest.Volume(e.Args[vo + 1], e.Args[vo + 2], e.Args[vo + 3],
                vo + 4 < e.Args.Length ? e.Args[vo + 4] : null));
            return;
        }

        var st = Array.FindIndex(e.Args, a => a.Equals("--seltest", StringComparison.OrdinalIgnoreCase));
        if (st >= 0 && st + 2 < e.Args.Length)
        {
            Shutdown(SelectionSelfTest.Run(e.Args[st + 1], e.Args[st + 2],
                st + 3 < e.Args.Length ? int.Parse(e.Args[st + 3]) : 6));
            return;
        }

        var im = Array.FindIndex(e.Args, a => a.Equals("--import", StringComparison.OrdinalIgnoreCase));
        if (im >= 0 && im + 2 < e.Args.Length)
        {
            Shutdown(SelfTest.Import(e.Args[im + 1], e.Args[im + 2]));
            return;
        }

        var rc = Array.FindIndex(e.Args, a => a.Equals("--recompress", StringComparison.OrdinalIgnoreCase));
        if (rc >= 0 && rc + 2 < e.Args.Length)
        {
            Shutdown(SelfTest.Recompress(e.Args[rc + 1], e.Args[rc + 2]));
            return;
        }

        var es = Array.FindIndex(e.Args, a => a.Equals("--extstats", StringComparison.OrdinalIgnoreCase));
        if (es >= 0)
        {
            Shutdown(ExtStats.Run(es + 1 < e.Args.Length ? e.Args[es + 1] : null));
            return;
        }

        if (Array.FindIndex(e.Args, a => a.Equals("--streamonly", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            Shutdown(SelfTest.StreamOnly());
            return;
        }

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
            Shutdown(SelfTest.BuildMod(e.Args[bd + 1], e.Args[bd + 2], e.Args[bd + 3],
                e.Args.Any(a => a.Equals("--prefetch", StringComparison.OrdinalIgnoreCase))));
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
