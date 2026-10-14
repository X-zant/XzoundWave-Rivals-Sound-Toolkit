using System.Globalization;
using System.IO;

namespace MRAudioKit;

/// <summary>
/// The places where the machine's locale must not reach.
///
/// A status line saying "0,9 MB" on a German machine is right, and is left alone. What
/// is not right is the same comma reaching a file: a legend column, a folder name, a
/// project's stored level. Those are read by another machine, or by a script, or by the
/// same person next week on a different host, and a number that changes shape between
/// locales is a file that only loads where it was written. The same goes for letters --
/// a key, a tag code or a file extension compared through the machine's collation gets
/// a different answer in Turkish, where upper-case "i" is not "I".
///
/// None of this shows up on the machine it was written on, which is why the test forces
/// a locale that disagrees rather than reporting whatever this host happens to use. The
/// first check in each section proves the forced locale actually took, so a pass here
/// can never mean "nothing was tested".
/// </summary>
public static class CultureSelfTest
{
    /// <summary>XzoundWave.exe --culture</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        W($"this machine: {CultureInfo.CurrentCulture.Name} " +
          $"(decimal separator '{CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator}')");

        // The Linux build is published with globalization switched off, because a
        // self-contained app cannot rely on ICU being installed and bundling it costs
        // 30 MB. On such a build no locale but the invariant one can be selected, so
        // this test cannot force the disagreement it is built to look for.
        //
        // It says so rather than passing. A pass would be a lie about work that did not
        // happen, and this file exists precisely because a green test that tested
        // nothing is worse than no test. Nothing is at risk here either way: the bugs
        // it guards against need a locale that this build cannot be put into, and the
        // Windows build, which can, runs the real thing.
        if (!CanForceALocale())
        {
            W("");
            W("globalization is off in this build, so no locale can be forced.");
            W("NOT APPLICABLE HERE - run this on a build with globalization enabled.");
            return 0;
        }

        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            W("");
            W("under a locale that writes 0,85");
            Check("the locale really is in force", (0.85).ToString("0.0#") == "0,85",
                  (0.85).ToString("0.0#"));

            // ---- a number that becomes a folder name ---------------------------
            Check("an export folder is named the same everywhere",
                  VolumeTool.Label(0.85) == "Vol-0.85", VolumeTool.Label(0.85));
            Check("...at every level", VolumeTool.Label(1.0) == "Vol-1.0"
                                       && VolumeTool.Label(4.0) == "Vol-4.0",
                  $"{VolumeTool.Label(1.0)}, {VolumeTool.Label(4.0)}");

            // ---- numbers that become columns in a comma-separated file ---------
            var testCsv = TestBankBuilder.LegendCsv(
                [new TestBankBuilder.Row(7, 1000585642, "bnk_vo_1029001", "ev", "cat", "sub", "", 3.71)]);
            Check("the numbered legend writes a length a reader can parse",
                  testCsv.Contains("\"3.71\"") && !testCsv.Contains("3,71"),
                  Cell(testCsv));

            var silentCsv = SilentBankBuilder.LegendCsv(
                [new SilentBankBuilder.Row(1000585642, "bnk_vo_1029001", "ev", "cat", "sub", 3.71)]);
            Check("...and so does the silent legend",
                  silentCsv.Contains("\"3.71\"") && !silentCsv.Contains("3,71"),
                  Cell(silentCsv));

            var volumeCsv = VolumeBankBuilder.LegendCsv(
                [new VolumeBankBuilder.Row(1000585642, "bnk_vo_1029001", "ev", 0.85, 12.5)]);
            Check("...and the volume legend's gain and clipping",
                  volumeCsv.Contains("\"0.85\"") && volumeCsv.Contains("\"12.50\"")
                  && !volumeCsv.Contains("0,85"),
                  Cell(volumeCsv));

            // ---- numbers that become a path inside a pak ------------------------
            Check("a loose media path is spelled the same everywhere",
                  MediaLayout.PathFor(1000585642) == MediaLayout.Root + "10/1000585642.wem",
                  MediaLayout.PathFor(1000585642));

            // ---- a number read back out of a name the depot chose ---------------
            Check("a depot filename still yields its build number",
                  UsmapFetch.BuildOf("5.3.2-3870120+++depot_marvel+S10.0_release-Marvel.usmap") == 3870120,
                  $"{UsmapFetch.BuildOf("5.3.2-3870120+++depot_marvel+S10.0_release-Marvel.usmap")}");

            // ---- a project written here and opened elsewhere --------------------
            W("");
            W("a project file written under that locale");
            fails += ProjectRoundTrip(Check);
        }
        finally { CultureInfo.CurrentCulture = was; }

        try
        {
            // German is not a hard enough test on its own. The folder name used to be
            // built in the machine's own culture and have its comma swapped back to a
            // point afterwards, which survives here and nowhere else: some forty
            // cultures write the decimal separator as U+066B, which that swap never
            // looked for, and an export landed in a folder whose name no other machine
            // in the same project would ever spell.
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");

            W("");
            W("under a locale whose decimal separator is neither . nor ,");
            // Reported as a code point, because a console that cannot render the
            // character would otherwise print the failure as "0?85 is not 0?85".
            Check("the locale really is in force", (0.85).ToString("0.0#") == "0٫85",
                  $"U+{(int)CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0]:X4}");
            Check("an export folder is still named for the level",
                  VolumeTool.Label(0.85) == "Vol-0.85", VolumeTool.Label(0.85));

            var csv = VolumeBankBuilder.LegendCsv(
                [new VolumeBankBuilder.Row(1000585642, "bnk_vo_1029001", "ev", 0.85, 12.5)]);
            Check("...and the legend still states the gain plainly",
                  csv.Contains("\"0.85\""), Cell(csv));
        }
        finally { CultureInfo.CurrentCulture = was; }

        try
        {
            // Turkish is the case that breaks identifiers rather than numbers: its
            // upper-case "i" is "I with a dot", so a key, a tag or an extension folded
            // through the machine's own rules stops matching the one it was stored as.
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            W("");
            W("under a locale where upper-case i is not I");
            Check("the locale really is in force", "i".ToUpper() == "İ", "i".ToUpper());

            Check("a tag typed in lower case is still the vocabulary's own code",
                  string.Join(" ", SoundTags.Canonical(["intro", "ui"])) == "INTRO UI",
                  string.Join(" ", SoundTags.Canonical(["intro", "ui"])));
            Check("...read back out of text the same way",
                  SoundTags.Flat(SoundTags.Parse("[intro][ui]")) == "INTRO UI",
                  SoundTags.Flat(SoundTags.Parse("[intro][ui]")));

            // A hidden row class is stored in settings.json by key, so the lookup has
            // to survive whatever case it was written in.
            Check("a stored row-class key still finds its class",
                  RowClasses.Find("NONVORBIS")?.Key == "nonvorbis",
                  RowClasses.Find("NONVORBIS")?.Key ?? "(not found)");

            Check("a file extension is recognised whatever its case",
                  AudioDecode.IsWem("CLIP.WEM") && AudioDecode.CanDecode("CLIP.WEM"),
                  Path.GetExtension("CLIP.WEM"));

            // The platform's rule, not the locale's: AppPaths decides whether case
            // distinguishes two files, and Turkish casing must not get a say.
            var a = Path.Combine(Path.GetTempPath(), "clip.wem");
            var b = Path.Combine(Path.GetTempPath(), "CLIP.WEM");
            var windows = OperatingSystem.IsWindows();
            Check($"case {(windows ? "does not" : "does")} still distinguish two paths here",
                  AppPaths.SamePath(a, b) == windows, $"SamePath = {AppPaths.SamePath(a, b)}");
        }
        finally { CultureInfo.CurrentCulture = was; }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// A project saved under a comma locale and opened anywhere else.
    ///
    /// System.Text.Json writes and reads numbers to the JSON grammar rather than to the
    /// machine's locale, so this has never been broken -- which is exactly why it is
    /// worth stating. Without it, the next person to read these fixes has no way to
    /// tell "verified invariant" from "not looked at", and the cost of guessing wrong
    /// is rewriting a serializer that was already correct.
    /// </summary>
    private static int ProjectRoundTrip(Action<string, bool, string> check)
    {
        var fails = 0;
        var dir = Path.Combine(Path.GetTempPath(), "xzw-culture-" + Guid.NewGuid().ToString("N")[..8]);
        var home = Path.Combine(dir, "mod");
        Directory.CreateDirectory(home);
        try
        {
            var p = Project.New();
            p.BulkVolume = 0.85;
            p.Notes.Add(new NoteRecord { MediaId = 1000585642, Volume = 1.25 });
            var path = Path.Combine(home, "test" + Project.Extension);
            p.Save(path);

            var raw = File.ReadAllText(path);
            var ok = raw.Contains("0.85") && raw.Contains("1.25") && !raw.Contains("0,85");
            check("the levels are written to the JSON grammar, not the locale", ok,
                  raw.Contains("0,85") ? "found 0,85 in the file" : "0.85 and 1.25");
            if (!ok) fails++;

            var reopened = Project.Load(path);
            var same = reopened.BulkVolume == 0.85 && reopened.Notes[0].Volume == 1.25;
            check("...and read back as the same numbers", same,
                  $"{reopened.BulkVolume.ToString(CultureInfo.InvariantCulture)}, " +
                  $"{reopened.Notes[0].Volume?.ToString(CultureInfo.InvariantCulture)}");
            if (!same) fails++;

            // The staged-path rule: inside the project folder it is stored relative,
            // outside it stays absolute. The outside case is decided by a ".." that a
            // collation is free to have an opinion about.
            var inside = p.ToStoredPath(Path.Combine(home, "audio", "x.wem"));
            var storedIn = inside == "audio/x.wem";
            check("a staged file under the project is stored relative", storedIn, inside);
            if (!storedIn) fails++;

            var outsidePath = Path.Combine(dir, "elsewhere.wem");
            var outside = p.ToStoredPath(outsidePath);
            var storedOut = outside == outsidePath;
            check("...and one above it stays absolute", storedOut, outside);
            if (!storedOut) fails++;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        return fails;
    }

    /// <summary>The first data row of a legend, for a failure worth reading.</summary>
    private static string Cell(string csv) =>
        csv.Split('\n').Skip(1).FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(no rows)";

    /// <summary>
    /// Whether a locale other than the invariant one can be selected at all. Asked by
    /// trying, because that is the only honest question: the answer depends on how the
    /// binary was published, not on anything the code can read about itself.
    /// </summary>
    private static bool CanForceALocale()
    {
        try { return new CultureInfo("de-DE").NumberFormat.NumberDecimalSeparator == ","; }
        catch (CultureNotFoundException) { return false; }
    }

}
