using System.IO;

namespace MRAudioKit;

/// <summary>
/// A muted sound must come out silent in every build, not only a test one.
///
/// Muting started as a test-build idea: quiet the number you have already identified so
/// the sounds underneath it can be heard. But silencing a line you do not want is a mod
/// in its own right, and it used to be ignored by the ordinary build entirely -- the
/// row said MUTED, the .bnk said otherwise.
/// </summary>
public static class MuteSelfTest
{
    /// <summary>XzoundWave.exe --mutetest &lt;skinId&gt;</summary>
    public static int Run(string skinId)
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

        var charId = skinId[..4];
        var skin = session.Characters.First(c => c.CharId == charId).Skins.First(s => s.SkinId == skinId);
        var sounds = SoundIndex.Build(session, skin, charId, _ => { });

        // Pick sounds that really live in a bank, since that is what gets rewritten.
        var targets = sounds.Rows
            .Where(r => session.BanksForMedia(r.MediaId).Count > 0)
            .GroupBy(r => r.MediaId).Select(g => g.First())
            .Take(4).ToList();
        if (targets.Count == 0) { W("no embedded media to test with"); return 1; }

        var muted = targets.Select(r => r.MediaId).ToHashSet();
        var bankPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in muted) foreach (var b in session.BanksForMedia(id)) bankPaths.Add(b);

        W($"{skin.Label}: muting {muted.Count} sound(s) across {bankPaths.Count} .bnk(s)");
        foreach (var r in targets) W($"    {r.MediaId,-12} {r.Display}");
        W("");

        var outDir = Path.Combine(Path.GetTempPath(), "xzw-mutetest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // ---- a plain build, nothing staged, only mutes ------------------
            var built = ModBuilder.Build(session, skin, [], outDir, bankPaths, false, muted);
            Check("the build reports them muted", built.Muted == muted.Count,
                  $"{built.Muted} of {muted.Count}");
            Check("it wrote a .bnk", built.Banks > 0, $"{built.Banks} written");

            var written = Directory.GetFiles(outDir, "*.bnk", SearchOption.AllDirectories);
            var checkedIds = 0;
            foreach (var path in written)
            {
                var bytes = File.ReadAllBytes(path);
                var sections = BnkBuilder.ReadSections(bytes);
                var didx = sections.FirstOrDefault(s => s.Tag == "DIDX");
                var data = sections.FirstOrDefault(s => s.Tag == "DATA");
                if (didx is null || data is null) continue;

                foreach (var e in BnkBuilder.ReadDidx(didx.Body))
                {
                    if (!muted.Contains(e.Id)) continue;
                    var wem = data.Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();
                    checkedIds++;

                    // Silence is not "a small file": it is a file whose packets are
                    // trivially small. uMaxPacketSize is the honest measure -- 1 for
                    // silence against 139-151 for speech.
                    var quiet = IsSilent(wem, out var maxPacket);
                    Check($"{e.Id} is silent in {Path.GetFileName(path)}", quiet,
                          $"uMaxPacketSize {maxPacket}");
                }
            }
            Check("every muted id was found in a written .bnk", checkedIds >= muted.Count,
                  $"{checkedIds} checked");

            // ---- a staged file beats a mute ---------------------------------
            W("");
            W("a staged replacement overrides a mute");
            var donor = sounds.Rows.First(r => r.MediaId == targets[0].MediaId);
            var bytesFor = new AudioPreview { VgmstreamPath = settings.VgmstreamPath }
                .Bytes(session, sounds, donor);
            if (bytesFor is { Length: > 0 })
            {
                var staged = Path.Combine(outDir, "staged");
                Directory.CreateDirectory(staged);
                var file = Path.Combine(staged, $"{donor.MediaId}-kept.wem");
                File.WriteAllBytes(file, bytesFor);
                var pending = PendingWem.FromFile(file, out _);

                var outDir2 = outDir + "-staged";
                var b2 = ModBuilder.Build(session, skin, [pending], outDir2, bankPaths, false, muted);
                var found = false;
                foreach (var path in Directory.GetFiles(outDir2, "*.bnk", SearchOption.AllDirectories))
                {
                    var bytes = File.ReadAllBytes(path);
                    var sec = BnkBuilder.ReadSections(bytes);
                    var didx = sec.FirstOrDefault(s => s.Tag == "DIDX");
                    var data = sec.FirstOrDefault(s => s.Tag == "DATA");
                    if (didx is null || data is null) continue;
                    foreach (var e in BnkBuilder.ReadDidx(didx.Body))
                        if (e.Id == donor.MediaId)
                        {
                            var wem = data.Body.AsSpan((int)e.Offset, (int)e.Size).ToArray();
                            found = true;
                            Check("the staged audio is there, not silence",
                                  !IsSilent(wem, out var mp), $"uMaxPacketSize {mp}");
                        }
                }
                Check("the staged id was found", found);
                Check("the others are still muted", b2.Muted == muted.Count - 1,
                      $"{b2.Muted} muted, expected {muted.Count - 1}");
                try { Directory.Delete(outDir2, true); } catch { }
            }
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// Whether a Vorbis wem carries no audible audio. Reading uMaxPacketSize rather
    /// than comparing byte counts: a 0.05 s clip is small without being silent, which
    /// is exactly how a size-based check gets this wrong.
    /// </summary>
    private static bool IsSilent(byte[] wem, out int maxPacket)
    {
        maxPacket = -1;
        try
        {
            var i = 12;
            while (i + 8 <= wem.Length)
            {
                var tag = System.Text.Encoding.ASCII.GetString(wem, i, 4);
                var size = BitConverter.ToInt32(wem, i + 4);
                if (tag == "fmt ")
                {
                    if (BitConverter.ToUInt16(wem, i + 8) != 0xFFFF) return false;  // not Vorbis
                    maxPacket = BitConverter.ToUInt16(wem, i + 8 + 18 + 0x1e);
                    return maxPacket <= 4;
                }
                i += 8 + size + (size & 1);
            }
        }
        catch { }
        return false;
    }
}
