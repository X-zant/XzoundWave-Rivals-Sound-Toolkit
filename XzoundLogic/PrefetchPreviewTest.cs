using System.IO;

namespace MRAudioKit;

/// <summary>
/// A voice line in an opened .bnk has to preview in full.
///
/// Most voice lines stream: the .bnk embeds a prefetch stub, a few hundred bytes of the
/// opening moment, and the real audio sits loose under Media/. The browse path has
/// always read loose first for exactly that reason. A .bnk opened from a mod went the
/// other way — bytes in hand won — which is right for a sound the mod replaced and
/// wrong for every one it left alone, so leaving a line untouched made it preview as a
/// fraction of a second and a hard cut.
///
/// Both directions are checked here, because a fix that reads loose first would be just
/// as wrong: it would play the game's sound over the mod's.
/// </summary>
public static class PrefetchPreviewTest
{
    /// <summary>XzoundWave.exe --prefetchpreview &lt;skinId&gt;</summary>
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

        var charId = skinId[..4];
        var ch = session.Characters.FirstOrDefault(c => c.CharId == charId);
        var skin = ch?.Skins.FirstOrDefault(s => s.SkinId == skinId);
        if (skin is null) { W($"no such skin: {skinId}"); return 1; }

        var preview = new AudioPreview { VgmstreamPath = settings.VgmstreamPath };

        // Open the game's own .bnk files the way opening a mod does. A mod that changed
        // nothing is the clearest case: every row should behave exactly as it does when
        // browsing, and before the fix none of the streamed ones did.
        foreach (var file in skin.Banks)
        {
            var name = Path.GetFileName(file.Path);
            byte[] raw;
            try { raw = file.Read(); } catch { continue; }

            var sounds = ModBank.ToSounds(session, new ModBank.Bank(name, file.Path, raw));
            if (sounds.Rows.Count == 0) continue;

            // A stub is a media the bank holds a SHORTER copy of than Media/ does.
            var stubs = sounds.Rows
                .Where(r => sounds.Raw.TryGetValue(r.MediaId, out var b) &&
                            session.LooseMedia(r.MediaId) is { } l && l.Size > b.Length)
                .GroupBy(r => r.MediaId).Select(g => g.First()).ToList();

            W($"{name}: {sounds.Rows.Count} media, {stubs.Count} of them prefetch stubs");
            if (stubs.Count == 0) continue;

            var row = stubs[0];
            var stub = sounds.Raw[row.MediaId];
            var full = session.LooseMedia(row.MediaId)!.Read();

            var got = preview.Bytes(session, sounds, row);
            Check("an untouched streamed line previews in full",
                  got is not null && got.Length == full.Length,
                  $"{row.MediaId}: got {got?.Length ?? 0:N0} B, stub is {stub.Length:N0} B, " +
                  $"full is {full.Length:N0} B");
            Check("and it is the loose file, not something else",
                  got is not null && got.AsSpan().SequenceEqual(full));

            // The columns have to agree with what plays. A stub's own length is a
            // quarter of a second, and printing that beside a ten second line reads
            // as a broken sound rather than a stub.
            Check("the list does not print the stub's length as the sound's",
                  row.Seconds is null, row.Seconds is null ? "left to be measured" : $"{row.Seconds:0.00}s");
            Check("and the size column is the real audio's",
                  row.Bytes == (uint)full.Length, $"{row.Bytes:N0} B of {full.Length:N0} B");

            // How big the bug was on this one .bnk. Every one of these played a
            // fraction of a second before the fix.
            var worst = stubs.Min(r => (double)sounds.Raw[r.MediaId].Length /
                                       session.LooseMedia(r.MediaId)!.Size);
            W($"    {stubs.Count} line(s) would have played from a stub; " +
              $"the shortest was {worst:P1} of its audio");

            // ---- the other direction ---------------------------------------
            // A mod that really did replace this sound. Its audio is a whole clip, not
            // a prefix of the game's, so the bytes in hand have to win.
            var donor = sounds.Rows.FirstOrDefault(r =>
                r.MediaId != row.MediaId && session.LooseMedia(r.MediaId) is { });
            if (donor is null) { W("    (no second sound to build a mod from)"); continue; }

            var swapped = session.LooseMedia(donor.MediaId)!.Read();
            var modded = BnkBuilder.Build(raw, new Dictionary<uint, byte[]>
            {
                [row.MediaId] = swapped,
            }).Bytes;

            var modSounds = ModBank.ToSounds(session, new ModBank.Bank(name, "mod.pak", modded));
            var modRow = modSounds.Rows.First(r => r.MediaId == row.MediaId);
            Check("the mod's diff still sees it as changed", modRow.ModTag is "MODDED",
                  modRow.ModTag ?? "(no comparison)");

            Check("a sound the mod replaced keeps its own length",
                  modRow.Seconds is not null && modRow.Bytes == (uint)swapped.Length,
                  $"{modRow.Seconds:0.00}s, {modRow.Bytes:N0} B");

            var fromMod = preview.Bytes(session, modSounds, modRow);
            Check("a sound the mod replaced plays the MOD's audio",
                  fromMod is not null && fromMod.AsSpan().SequenceEqual(swapped),
                  fromMod is null ? "nothing"
                  : fromMod.AsSpan().SequenceEqual(full) ? "played the game's audio instead"
                  : $"{fromMod.Length:N0} B");
            break;   // one .bnk with stubs in it is enough to prove both rules
        }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
