namespace MRAudioKit;

/// <summary>
/// The properties a .bnk applies on top of a sound, checked against the game.
///
/// Two things can go wrong silently here and both did while this was being written.
/// The property ids are version specific, so a table from the wrong Wwise labels real
/// values with the wrong names -- an initial delay read as "TransitionTime". And each
/// value is four bytes that are a float or a u32 depending on the property, so reading
/// the wrong view turns an attenuation id into a number near zero.
///
/// Neither shows up as an error. The numbers simply mean something else, which is why
/// this checks named properties against sounds whose values are known.
/// </summary>
public static class AppliedPropsTest
{
    /// <summary>XzoundWave.exe --applied</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        SkinSounds Read(string skinId)
        {
            var ch = session.Characters.FirstOrDefault(c => c.CharId == skinId[..4]);
            var skin = ch?.Skins.FirstOrDefault(s => s.SkinId == skinId);
            return skin is null ? null : SoundIndex.Build(session, skin, skinId[..4], _ => { });
        }

        // ---- the table is the right one for these banks ------------------------
        W("property table");
        var moon = Read("1030001");
        if (moon is null) { W("no skin 1030001"); return 1; }

        var read = moon.Rows.Count(r => r.Applied is not null);
        Check("every row carries what the .bnk applies to it", read == moon.Rows.Count,
              $"{read} of {moon.Rows.Count}");

        // Moon Knight's ult music: one sound, volume trimmed on the container it sits
        // in. -5 dB is the value in the bank; if the table ever shifts, this name will
        // move off it and the check fails rather than quietly renaming the number.
        var music = moon.Rows.FirstOrDefault(r => r.EventName == "sfx_1030001_q_music_play");
        Check("the ult music event is there", music is not null);
        if (music is not null)
        {
            var vol = music.Applied.Props.FirstOrDefault(p => p.Name == "Volume");
            Check("its volume is trimmed by the .bnk, not the file",
                  vol is not null && Math.Abs(vol.Float - -5f) < 0.001f,
                  vol is null ? "no Volume property" : $"{vol.Text} on the {vol.Owner}");
            Check("and that shows in the column", music.AppliedLabel.Contains("dB"),
                  music.AppliedLabel);
            Check("it plays once rather than looping", music.Applied.LoopCount == 1,
                  $"loop count {music.Applied.LoopCount}");
        }

        // ---- a value that is an id, not a number -------------------------------
        W("");
        W("values that are ids rather than numbers");
        var luna = Read("1031001");
        if (luna is null) { W("no skin 1031001"); return 1; }

        var ids = luna.Rows.Where(r => r.Applied is not null)
                      .SelectMany(r => r.Applied.Props)
                      .Where(p => p.Unit == WwiseProps.Unit.ObjectId)
                      .ToList();
        Check("some properties are object references", ids.Count > 0, $"{ids.Count} found");
        Check("they are shown as ids", ids.All(p => p.Text.StartsWith('#')),
              ids.FirstOrDefault(p => !p.Text.StartsWith('#'))?.Text ?? "");

        // The point of the ObjectId unit. A Wwise id is a 32-bit hash, so reading it as
        // a float gives whatever those bits happen to mean -- a denormal near zero when
        // the high bits are clear, an astronomical number when they are not. Only some
        // land in those ranges, which is what makes the mistake survivable: the rest
        // come out as ordinary-looking numbers and read as real values.
        var real = ids.Where(p => p.Raw > 1000).ToList();
        var nonsense = real.Count(p => Math.Abs(p.Float) < 1e-10 || Math.Abs(p.Float) > 1e10);
        Check("set references read as ids", real.Count > 0, $"{real.Count} set");
        Check("...where reading the float view would have been visibly wrong",
              nonsense > 0,
              real.Count == 0 ? "none set"
              : $"{nonsense} of {real.Count} land outside any plausible range, " +
                $"e.g. #{real[0].Raw} is {real[0].Float:G4} as a float");

        // ---- delays are seconds, and plausible ---------------------------------
        W("");
        W("delays");
        var lunaMusic = luna.Rows.Where(r => r.EventName == "sfx_1031001_q_music_play").ToList();
        Check("the ult music fires more than one sound", lunaMusic.Count > 1,
              $"{lunaMusic.Count} sound(s)");

        var delays = lunaMusic.SelectMany(r => r.Applied.Props)
                              .Where(p => p.Name == "Initial delay")
                              .Select(p => p.Float).ToList();
        Check("its layers are staggered by an initial delay", delays.Count > 0,
              string.Join(", ", delays.Select(d => $"{d:0.###}s")));
        // A tenth of a second staggers layers; a tenth of a MILLIsecond, or ten
        // seconds, would mean the value is being read in the wrong unit.
        Check("those delays are in a range a human would author",
              delays.All(d => d > 0.001f && d < 5f),
              string.Join(", ", delays.Select(d => $"{d:0.###}s")));

        // ---- the chain, not just the sound -------------------------------------
        W("");
        W("inherited from containers");
        var inherited = moon.Rows.Where(r => r.Applied is not null)
                            .SelectMany(r => r.Applied.Props)
                            .Count(p => p.Owner != "sound");
        Check("properties are picked up from the containers above a sound",
              inherited > 0, $"{inherited} inherited");

        var chain = moon.Rows.FirstOrDefault(r => r.Applied is not null &&
                                                  r.Applied.Props.Any(p => p.Owner != "sound"));
        if (chain is not null)
            W($"    e.g. {chain.Display}: {string.Join(" | ",
                chain.Applied.Props.Select(p => $"{p} ({p.Owner})"))}");

        // ---- nothing is invented ----------------------------------------------
        W("");
        W("silence where there is nothing to say");
        var untouched = moon.Rows.FirstOrDefault(r => r.Applied is { Props.Count: 0 });
        Check("a sound the .bnk leaves alone says so plainly",
              untouched is null || untouched.AppliedLabel.Length == 0,
              untouched is null ? "(every sound here has something set)" : untouched.AppliedDetail);

        var named = moon.Rows.SelectMany(r => r.Applied?.Props ?? [])
                        .Count(p => p.Name.StartsWith("Property "));
        Check("no property is left as a bare number", named == 0,
              named == 0 ? "" : $"{named} unnamed — the table does not cover this bank version");

        // ---- a level at the floor ---------------------------------------------
        W("");
        W("levels at the floor");

        // -96 dB is Wwise's minimum. The first guess was that these are driven up by a
        // game parameter -- setting a level to its floor and letting a curve raise it
        // is ordinary practice. In these banks that is not what is happening: nothing
        // points at them, and they really are switched off. The check is here because
        // the two cases look identical in the data and mean opposite things.
        var floored = new[] { moon, luna }
            .SelectMany(s2 => s2.Rows)
            .Where(r => r.Applied is not null)
            .SelectMany(r => r.Applied.Props.Select(p => (Row: r, Prop: p)))
            .Where(x => x.Prop.Unit == WwiseProps.Unit.Db && x.Prop.Float <= -90)
            .ToList();
        W($"    {floored.Count} propert(y/ies) at -90 dB or below");

        var silenced = new[] { moon, luna }.SelectMany(s2 => s2.Rows)
            .Where(r => r.Applied is { Silenced: true }).ToList();
        Check("a sound the bank turns fully down is reported as silent",
              silenced.Count > 0, $"{silenced.Count} row(s)");

        foreach (var g in silenced.GroupBy(r => r.EventName ?? "(unnamed)")
                                  .OrderByDescending(g2 => g2.Count()).Take(6))
            W($"      {g.Count(),3}x  {g.Key}   {g.First().Applied.StaticDb:0.#} dB");

        var one = silenced.FirstOrDefault(r => r.EventName is not null);
        if (one is not null)
        {
            Check("the column says SILENT rather than a number that reads as a typo",
                  one.AppliedLabel.Contains("SILENT"), one.AppliedLabel);
            Check("the tooltip says nothing raises it again",
                  one.AppliedDetail.Contains("nothing raises it"), "");
            Check("...and that replacing the audio would be wasted work",
                  one.AppliedDetail.Contains("changes nothing"), "");
        }

        // The other reading of a floor value, kept apart from it. A driven level is
        // left out of the figure entirely rather than counted as silence.
        var drivenDb = new[] { moon, luna }.SelectMany(s2 => s2.Rows)
            .Where(r => r.Applied is not null)
            .SelectMany(r => r.Applied.DrivenProps)
            .Count(p => p.Unit == WwiseProps.Unit.Db);
        W($"    {drivenDb} level(s) are driven by a game parameter instead");

        var curves = new[] { moon, luna }.SelectMany(s2 => s2.Rows)
            .Where(r => r.Applied is not null)
            .SelectMany(r => r.Applied.Live).Distinct().ToList();
        Check("runtime curves name what they point at", curves.Count > 0,
              string.Join(", ", curves.Take(4)));
        // The RTPC table is a DIFFERENT enumeration from the property one -- they
        // agree to BusVolume and then diverge, so a curve on id 6 is an initial delay
        // and a property on id 6 is a make-up gain.
        Check("...using the RTPC table, not the property one",
              curves.All(c => !c.StartsWith("parameter ")),
              curves.FirstOrDefault(c => c.StartsWith("parameter ")) ?? "");

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
