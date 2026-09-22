using System.IO;

namespace MRAudioKit;

/// <summary>
/// A folder of named wems is a complete instruction on its own.
///
/// Building used to demand a skin be selected first, which is backwards when the
/// author has already said what they want: a file called <c>{mediaId}-note.wem</c>
/// names the sound, and the media id names the .bnk. Worse, a set of staged files can
/// span characters, which no single skin selection can express — so requiring one
/// actively got in the way of the case it was supposed to help.
/// </summary>
public static class NoSkinBuildTest
{
    /// <summary>XzoundWave.exe --noskinbuild &lt;skinIdA&gt; &lt;skinIdB&gt;</summary>
    public static int Run(string skinA, string skinB)
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
        session.IndexBanks(_ => { });

        // One sound from each of two different characters, to prove a build can span
        // them without any single skin being "the" selection.
        var preview0 = new AudioPreview { VgmstreamPath = settings.VgmstreamPath };
        var picks = new List<(SoundRow Row, byte[] Bytes)>();
        foreach (var id in new[] { skinA, skinB })
        {
            var ch = session.Characters.FirstOrDefault(c => c.CharId == id[..4]);
            var skin = ch?.Skins.FirstOrDefault(s => s.SkinId == id);
            if (skin is null) { W($"no such skin: {id}"); return 1; }
            var rows = SoundIndex.Build(session, skin, id[..4], _ => { });

            // Needs to be a sound that is IN a .bnk and whose bytes we can actually get.
            (SoundRow, byte[]) found = default;
            foreach (var r in rows.Rows.Where(r => session.BanksForMedia(r.MediaId).Count > 0))
            {
                var b = preview0.Bytes(session, rows, r);
                if (b is { Length: > 0 }) { found = (r, b); break; }
            }
            if (found.Item1 is null) { W($"no usable sound in {skin.Label}"); return 1; }
            picks.Add(found);
            W($"{skin.Label,-28} {found.Item1.MediaId,-12} {found.Item1.Display}");
        }

        var work = Path.Combine(Path.GetTempPath(), "xzw-noskin-" + Guid.NewGuid().ToString("N")[..8]);
        var staged = Path.Combine(work, "staged");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(staged);

        try
        {
            // Stage a real sound's own bytes against each id: content does not matter
            // here, only that the build reaches the right .bnk without being told.
            var pending = new List<PendingWem>();
            foreach (var (row, bytes) in picks)
            {
                var file = Path.Combine(staged, $"{row.MediaId}-from-test.wem");
                File.WriteAllBytes(file, bytes);
                var p = PendingWem.FromFile(file, out var why);
                if (p is null) { W("  stage failed: " + why); continue; }
                pending.Add(p);
            }
            Check("both sounds staged from filenames alone", pending.Count == picks.Count,
                  $"{pending.Count} of {picks.Count}");
            if (pending.Count == 0) return 1;

            // The resolution the window now relies on: id -> .bnk, with no skin involved.
            var bankPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pending)
                foreach (var b in session.BanksForMedia(p.MediaId)) bankPaths.Add(b);
            Check("their .bnk files resolve from the ids", bankPaths.Count > 0,
                  $"{bankPaths.Count} .bnk(s)");

            var spansBoth = picks.Count == 2 &&
                            !session.BanksForMedia(picks[0].Row.MediaId)
                                    .Intersect(session.BanksForMedia(picks[1].Row.MediaId)).Any();
            Check("the two sounds live in different .bnk files", spansBoth,
                  spansBoth ? "a single skin could not cover both" : "they share a .bnk");

            // skin: null is the whole point.
            var built = ModBuilder.Build(session, null, pending, outDir, bankPaths);
            Check("it builds with no skin selected", built.Banks > 0, $"{built.Banks} .bnk(s) written");
            Check("the log does not pretend to know a skin",
                  built.Log.Contains("staged files", StringComparison.Ordinal),
                  built.Log.Split('\n').FirstOrDefault()?.Trim());

            var written = Directory.GetFiles(outDir, "*.bnk", SearchOption.AllDirectories);
            Check("every resolved .bnk was written", written.Length == bankPaths.Count,
                  $"{written.Length} of {bankPaths.Count}");

            // And the staged bytes really landed, rather than the build merely running.
            var landed = 0;
            foreach (var path in written)
            {
                var bytes = File.ReadAllBytes(path);
                var sec = BnkBuilder.ReadSections(bytes);
                var didx = sec.FirstOrDefault(s => s.Tag == "DIDX");
                var data = sec.FirstOrDefault(s => s.Tag == "DATA");
                if (didx is null || data is null) continue;
                foreach (var e in BnkBuilder.ReadDidx(didx.Body))
                {
                    var p = pending.FirstOrDefault(x => x.MediaId == e.Id);
                    if (p is null) continue;
                    var inBank = data.Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();
                    var mine = p.Payload ?? File.ReadAllBytes(p.FullPath);
                    if (inBank.AsSpan().SequenceEqual(mine)) landed++;
                }
            }
            Check("the staged audio is what ended up in the .bnk", landed >= pending.Count,
                  $"{landed} of {pending.Count}");
        }
        finally { try { Directory.Delete(work, true); } catch { } }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
