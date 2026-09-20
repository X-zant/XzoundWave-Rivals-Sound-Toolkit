namespace MRAudioKit;

/// <summary>
/// Checks that turning a kind of row off actually removes those rows and nothing else,
/// and that the setting behaves sensibly when it has never been chosen or names a kind
/// this build does not know.
/// </summary>
public static class RowFilterSelfTest
{
    /// <summary>XzoundWave.exe --rowfilters</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        // One row of each kind, plus a plain one that must survive everything.
        var plain = Row(1, "vo_plain_play", sub: "A line.", tags: ["ULT"]);
        var rows = new List<SoundRow>
        {
            plain,
            Row(2, null,             sub: "x", tags: ["ULT"]),                     // unnamed
            Row(3, "vo_muted_play",  sub: "x", tags: ["ULT"], muted: true),        // muted
            Row(4, "vo_staged_play", sub: "x", tags: ["ULT"], staged: true),       // staged
            Row(5, "vo_mod_play",    sub: "x", tags: ["ULT"], modTag: "MODDED"),   // modded
            Row(6, "vo_new_play",    sub: "x", tags: ["ULT"], modTag: "NEW"),      // modded
            Row(7, "vo_same_play",   sub: "x", tags: ["ULT"], modTag: ""),         // unchanged
            Row(8, "vo_str_play",    sub: "x", tags: ["ULT"], streamed: true),     // streamed
            Row(9, "sfx_pcm_play",   sub: "x", tags: ["ULT"], codec: "PCM"),       // non-Vorbis
            Row(10, "vo_nosub_play", sub: "",  tags: ["ULT"]),                     // no subtitle
            Row(11, "vo_untag_play", sub: "x", tags: []),                          // untagged
        };
        W($"{rows.Count} rows, one of each kind");

        W("");
        W("each kind removes only its own rows");
        foreach (var cls in RowClasses.All)
        {
            var expected = rows.Count(r => cls.Matches(r));
            var left = RowClasses.Apply(rows, [cls.Key]).ToList();
            var ok = left.Count == rows.Count - expected && !left.Any(r => cls.Matches(r));
            Check($"{cls.Key,-10} hides {expected}", ok, $"{left.Count} left");
        }

        W("");
        W("combinations and edge cases");

        // Every row is either tagged or untagged, so those two are left out here: they
        // are a complementary pair and between them they account for everything.
        var stateKinds = RowClasses.All.Select(c => c.Key)
            .Where(k => k is not ("tagged" or "untagged")).ToList();
        var survivors = RowClasses.Apply(rows, stateKinds).ToList();
        var shouldSurvive = rows.Where(r => !stateKinds.Any(k => RowClasses.Find(k).Matches(r))).ToList();
        Check("hiding every state kind leaves exactly the rows in no state",
            survivors.SequenceEqual(shouldSurvive) && survivors.Contains(plain),
            $"{survivors.Count} left, expected {shouldSurvive.Count}");

        Check("nothing hidden leaves everything",
            RowClasses.Apply(rows, []).Count() == rows.Count);
        Check("a null list is not a filter",
            RowClasses.Apply(rows, null).Count() == rows.Count);
        // A settings file from a newer build must not crash this one.
        Check("an unknown kind is ignored, not thrown on",
            RowClasses.Apply(rows, ["not-a-kind"]).Count() == rows.Count);
        Check("tagged + untagged together leave nothing",
            !RowClasses.Apply(rows, ["tagged", "untagged"]).Any());

        // This is the distinction that decides whether normal browsing works at all.
        Check("hiding untouched mod entries spares the game's own rows",
            RowClasses.Apply(rows, ["unchanged"]).Contains(plain));
        Check("...and does remove the one the diff marked untouched",
            !RowClasses.Apply(rows, ["unchanged"]).Any(r => r.MediaId == 7));

        W("");
        W("settings");
        var s = new Settings();
        Check("never chosen means the default set, not empty",
            (s.HiddenRowClasses ??= [.. RowClasses.HiddenByDefault]) is ["unnamed"],
            string.Join(",", s.HiddenRowClasses));
        Check("every default names a real kind",
            RowClasses.HiddenByDefault.All(k => RowClasses.Find(k) is not null));
        Check("keys are unique",
            RowClasses.All.Select(c => c.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            == RowClasses.All.Count);

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    private static SoundRow Row(uint id, string ev, string sub, List<string> tags,
                                bool muted = false, bool staged = false, string modTag = null,
                                bool streamed = false, string codec = "VORBIS") => new()
    {
        Kind = "VO", Bank = "bnk_test", EventName = ev, MediaId = id, Codec = codec,
        Streamed = streamed, Subtitle = sub, Muted = muted, ModTag = modTag,
        Tags = tags, ReplacementPath = staged ? @"C:\x.wem" : null,
    };
}
