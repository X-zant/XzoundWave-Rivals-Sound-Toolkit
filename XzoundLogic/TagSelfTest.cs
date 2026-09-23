using System.IO;

namespace MRAudioKit;

/// <summary>
/// Checks the shared tag vocabulary without a window: the codes survive a save and a
/// reload, they follow a sound whose media id a game update changed, and the filter
/// matches whole tags only.
/// </summary>
public static class TagSelfTest
{
    /// <summary>XzoundWave.exe --tags</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        W($"vocabulary: {SoundTags.All.Count} tags");
        foreach (var g in SoundTags.All.GroupBy(t => t.Group))
            W($"  {g.Key}: {string.Join(" ", g.Select(t => "[" + t.Code + "]"))}");

        // ---- cleaning ------------------------------------------------------
        W("");
        W("cleaning");
        Check("no duplicates, case folded",
            SoundTags.Flat(["ULT", "ult", "[Ult]"]) == "ULT",
            SoundTags.Flat(["ULT", "ult", "[Ult]"]));
        Check("vocabulary order, not entry order",
            SoundTags.Flat(["LOOP", "MUSIC", "ULT"]) == "ULT MUSIC LOOP",
            SoundTags.Flat(["LOOP", "MUSIC", "ULT"]));
        // A project from a newer build must not lose tags this build cannot explain.
        Check("unknown codes kept, after the known ones",
            SoundTags.Flat(["ZZFUTURE", "ULT"]) == "ULT ZZFUTURE",
            SoundTags.Flat(["ZZFUTURE", "ULT"]));
        Check("blank entries dropped", SoundTags.Flat(["", "  ", "ULT"]) == "ULT");
        Check("display form", SoundTags.Format(["ult", "music"]) == "[ULT][MUSIC]",
            SoundTags.Format(["ult", "music"]));
        Check("parse round trip",
            SoundTags.Flat(SoundTags.Parse("[ULT][MUSIC] LONG, 3D")) == "ULT MUSIC LONG 3D",
            SoundTags.Flat(SoundTags.Parse("[ULT][MUSIC] LONG, 3D")));

        // ---- filtering -----------------------------------------------------
        W("");
        W("filtering");
        Check("matches a whole tag", SoundTags.Matches(["ULT", "MUSIC"], "ult"));
        Check("brackets in the query are ignored", SoundTags.Matches(["ULT"], "[ULT]"));
        Check("does not match a substring", !SoundTags.Matches(["ULT"], "ul"));
        Check("does not match a longer word containing it", !SoundTags.Matches(["ULT"], "result"));
        Check("empty query matches nothing", !SoundTags.Matches(["ULT"], "   "));

        // ---- a project keeps them -----------------------------------------
        W("");
        W("project round trip");
        var dir = Path.Combine(Path.GetTempPath(), "xzw-tagtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tags" + Project.Extension);

        var project = Project.New();
        var row = Row(4242, "vo_1029001_ult_01_play", "bnk_vo_1029001");
        row.Tags = SoundTags.Canonical(["music", "ULT", "LONG"]);
        row.Notes = "fades in";
        project.SetNote(row);
        project.Save(file);

        var reloaded = Project.Load(file);
        var note = reloaded.FindNote(4242);
        Check("note survives", note is not null);
        Check("tags survive", note is not null && SoundTags.Flat(note.Tags) == "ULT MUSIC LONG",
            note is null ? "no note" : SoundTags.Flat(note.Tags));
        Check("free text is untouched", note?.Notes == "fades in");

        // Tags only, no other content, must still be worth saving.
        var bare = Project.New();
        var tagOnly = Row(7, "vo_x_play", "bnk_x");
        tagOnly.Tags = ["MVP"];
        bare.SetNote(tagOnly);
        Check("a tag alone is not an empty note", bare.Notes.Count == 1 && !bare.Notes[0].IsEmpty);

        // ---- they follow the sound across a game update --------------------
        W("");
        W("relink after a media id change");
        var afterPatch = new List<SoundRow> { Row(99999, "vo_1029001_ult_01_play", "bnk_vo_1029001") };
        var relinked = reloaded.ApplyNotes(afterPatch);
        Check("one note relinked", relinked == 1, $"relinked {relinked}");
        Check("tags came with it", SoundTags.Flat(afterPatch[0].Tags) == "ULT MUSIC LONG",
            SoundTags.Flat(afterPatch[0].Tags));
        Check("row can be asked about one tag", afterPatch[0].HasTag("music"));
        Check("label renders", afterPatch[0].TagLabel == "[ULT][MUSIC][LONG]", afterPatch[0].TagLabel);

        // A note that was relinked has to save against the NEW id, or the next
        // reload loses it again.
        reloaded.Save();
        Check("relinked id is what gets written", Project.Load(file).FindNote(99999) is not null);

        // The extension changed from .mrak to .xzw. A project written before that must
        // still open, or people lose work for a rename.
        W("");
        W("project file extension");
        Check("new projects are saved as .xzw", Project.Extension == ".xzw", Project.Extension);
        Check("the old extension is still known", Project.LegacyExtension == ".mrak");
        Check("the Open filter offers both",
            Project.OpenFilter.Contains("*.xzw") && Project.OpenFilter.Contains("*.mrak"));

        var legacy = Path.Combine(dir, "old" + Project.LegacyExtension);
        var carry = Project.New();
        var oldRow = Row(555, "vo_old_play", "bnk_old");
        oldRow.Tags = ["MVP"];
        oldRow.Notes = "written before the rename";
        carry.SetNote(oldRow);
        carry.Save(legacy);

        var opened = Project.Load(legacy);
        Check("a .mrak project still loads", opened.FindNote(555) is not null);
        Check("its tags and notes are intact",
            SoundTags.Flat(opened.FindNote(555)?.Tags) == "MVP"
            && opened.FindNote(555)?.Notes == "written before the rename");

        // Saving it somewhere new writes the current extension; the old file is left be.
        var migrated = Path.Combine(dir, "old" + Project.Extension);
        opened.Save(migrated);
        Check("it can be saved on as .xzw",
            File.Exists(migrated) && Project.Load(migrated).FindNote(555) is not null);
        Check("the original .mrak is left alone", File.Exists(legacy));

        try { Directory.Delete(dir, true); } catch { }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    private static SoundRow Row(uint id, string ev, string bank) => new()
    {
        Kind = "VO", Bank = bank, EventName = ev, MediaId = id, Codec = "VORBIS",
    };
}
