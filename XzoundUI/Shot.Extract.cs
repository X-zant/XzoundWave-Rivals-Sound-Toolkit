using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace MRAudioKit.UI;

public static partial class Shot
{
    /// <summary>
    /// XzoundWave --shot-extract &lt;dir&gt;: right-click › Extract wem(s)… and Extract as WAV… on Emma
    /// Frost's banks. Each file is what Play plays for its row, named {media id}-{event}; a voice
    /// line comes out whole, not as the bank's prefetch stub; the WAVs are WAV.
    ///
    /// Writes into &lt;dir&gt;, and the window writes its settings as it closes, so run it under a
    /// settings folder of its own (on Linux, HOME pointed at a scratch folder).
    /// </summary>
    public static int TakeExtractRows(string dir)
    {
        try
        {
            AppBuilder.Configure<App>().UseBundledFonts().UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            Directory.CreateDirectory(dir);
            var main = new MainWindow { Width = 1440, Height = 840 };
            main.Show();
            main.LoadGameSync();
            void Pump(Task t = null)
            {
                while (t is { IsCompleted: false }) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
                for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            }
            Pump();
            var failures = 0;
            void Check(string what, bool ok, string detail = "")
            {
                Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}{(detail.Length > 0 ? "   " + detail : "")}");
                if (!ok) failures++;
            }
            var grid = main.FindControl<DataGrid>("Grid_");
            void Select(IEnumerable<SoundRow> rows)
            {
                grid.SelectedItems.Clear();
                foreach (var r in rows) grid.SelectedItems.Add(r);
                Pump();
            }

            main.ShowSkinSync("1053001", "sfx");
            Pump();
            var sfx = main.SoundsForShot.Rows.Where(r => !r.Streamed && r.EventName is not null).GroupBy(r => r.MediaId).Select(g => g.First()).Take(3).ToList();
            Check("three of Emma's SFX rows to extract", sfx.Count == 3, $"{sfx.Count}");
            Select(sfx);
            var wems = Path.Combine(dir, "wem");
            Pump(main.ExtractRowsForShot(wems, asWav: false));
            var written = Directory.Exists(wems) ? Directory.GetFiles(wems, "*.wem") : [];
            Check("one wem per row, named {media id}-{event}", written.Length == 3 &&
                  sfx.All(r => written.Any(f => Path.GetFileName(f).StartsWith($"{r.MediaId}-", StringComparison.Ordinal))),
                  string.Join(", ", written.Select(Path.GetFileName)));
            Check("each is exactly what Play plays for its row", sfx.All(r =>
                      written.FirstOrDefault(f => Path.GetFileName(f).StartsWith($"{r.MediaId}-", StringComparison.Ordinal)) is { } f &&
                      File.ReadAllBytes(f).AsSpan().SequenceEqual(main.PlayBytesForShot(r.MediaId, original: false))),
                  main.Status.Text);

            var wavs = Path.Combine(dir, "wav");
            Pump(main.ExtractRowsForShot(wavs, asWav: true));
            var decoded = Directory.Exists(wavs) ? Directory.GetFiles(wavs, "*.wav") : [];
            Check("Extract as WAV writes WAV files", decoded.Length == 3 &&
                  decoded.All(f => File.ReadAllBytes(f) is { Length: > 44 } b && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'),
                  main.Status.Text);

            main.ShowSkinSync("1053001", "vo");
            Pump();
            // A voice line: its bank holds only the opening (a prefetch stub) and its whole audio is
            // a loose file. Play plays the whole of it, and so must the extract.
            var line = main.SoundsForShot.Rows.FirstOrDefault(r => r.Bank.StartsWith("bnk_vo", StringComparison.OrdinalIgnoreCase) &&
                                                                   r.EventName?.StartsWith("vo_", StringComparison.Ordinal) == true);
            Check("an Emma voice line", line is not null, line is null ? "" : $"{line.EventName}, {line.Bytes:N0} B in the bank, from {line.Origin}");
            if (line is not null)
            {
                Select([line]);
                var vo = Path.Combine(dir, "vo");
                Pump(main.ExtractRowsForShot(vo, asWav: false));
                var file = Directory.Exists(vo) ? Directory.GetFiles(vo, "*.wem").SingleOrDefault() : null;
                var full = main.PlayBytesForShot(line.MediaId, original: false);
                Check("it comes out as Play plays it, the whole line", file is not null && full is { Length: > 0 } &&
                      File.ReadAllBytes(file).AsSpan().SequenceEqual(full) && (line.Bytes <= 0 || full.Length >= line.Bytes),
                      $"{(file is null ? 0 : new FileInfo(file).Length):N0} B written vs {line.Bytes:N0} B in the bank; {main.Status.Text}");
            }

            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not run the extract check: " + ex);
            return 1;
        }
    }
}
