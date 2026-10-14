using System.IO;
using System.Text;

namespace MRAudioKit;

/// <summary>
/// Turns staged .wem files into a repak-ready folder. Lives outside the window so the
/// same code path can be driven headlessly and verified.
/// </summary>
public static class ModBuilder
{
    /// <param name="Retimed">
    /// Replacements whose bank states a different length for them. A music bank records
    /// each clip's own duration beside the clip and places it by that figure, and a
    /// rebuild copies the hierarchy through untouched, so a swap that changes the length
    /// leaves the bank describing audio that is no longer there. Counted and reported
    /// rather than refused: which length to ship is the author's call, and a build that
    /// says nothing about it is the one outcome they cannot act on.
    /// </param>
    public sealed record Result(int Banks, int Loose, int PrefetchTrimmed, string Log,
                                List<string> Outputs, int Muted = 0, int Retimed = 0);

    /// <param name="prefetchMode">
    /// OFF (default): the full custom clip goes INTO the bank, replacing the original
    /// entry outright, and nothing is written under Media/. This is what modders ship
    /// and what is known to work in-game — so the engine evidently sizes the media from
    /// the DIDX entry rather than the HIRC's inMemorySize, which is left untouched
    /// exactly as BNK-Pack leaves it.
    ///
    /// ON: keep the vanilla prefetch length in the bank and ship the full clip as a
    /// loose Media/ file. Structurally this is what the shipped data looks like
    /// (SourceType=PrefetchStreaming, stub == file[0..n]), but it makes a bigger mod
    /// and is not the community's route. Kept as an option, not the default.
    /// </param>
    /// <param name="muted">
    /// Media ids to replace with silence. Muting began as a test-build idea -- quiet
    /// the sound you have already identified so the ones underneath can be heard -- but
    /// it is just as much a mod: silencing a line you do not want is a thing people
    /// ship. A mute means the same thing in every build now, not only in a test one.
    /// A staged replacement wins, because staging a file over a muted sound is plainly
    /// a change of mind.
    /// </param>
    public static Result Build(GameSession session, SkinEntry skin,
                               IReadOnlyList<PendingWem> pending, string outRoot,
                               ISet<string> bankPaths, bool prefetchMode = false,
                               IReadOnlySet<uint> muted = null,
                               BankBase baseBanks = null)
    {
        var log = new StringBuilder();
        var outputs = new List<string>();
        var payload = new Dictionary<uint, byte[]>();
        foreach (var p in pending)
            payload[p.MediaId] = p.Payload ?? File.ReadAllBytes(p.FullPath);

        var banksWritten = 0;
        var trimmed = 0;
        var silenced = 0;
        var placed = new HashSet<uint>();
        var retimed = new List<string>();

        // What the banks involved in this build state about the length of the clips they
        // place, kept across the whole build rather than per bank. A music bank times the clips
        // it STREAMS as well as the ones it embeds, and 575 of this game's 656 music media
        // are streamed, so a mod that replaces one of those has no DIDX entry to be
        // checked against and would otherwise be reported on by nothing.
        var timed = new Dictionary<uint, MusicIndex.Clip>();
        var noted = new HashSet<uint>();

        void Retimed(uint id, byte[] custom)
        {
            if (!timed.TryGetValue(id, out var clip) || !noted.Add(id)) return;
            if (BnkBuilder.Seconds(custom) is not { } got ||
                !MusicIndex.Disagrees(clip.Seconds, got)) return;
            retimed.Add($"media {id}: " +
                        MusicIndex.LengthNote(clip.Seconds, clip.SegmentSeconds, clip.Trimmed, got));
        }

        foreach (var bankPath in bankPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var orig = (baseBanks ?? BankBase.None).Read(session, bankPath);
            if (orig is null) continue;

            var didxSection = BnkBuilder.ReadSections(orig).FirstOrDefault(s => s.Tag == "DIDX");
            if (didxSection is null) continue;

            var perBank = new Dictionary<uint, byte[]>();
            var dataSection = BnkBuilder.ReadSections(orig).FirstOrDefault(s => s.Tag == "DATA");

            // What this bank states about the length of the clips it places. Empty for
            // every bank that times nothing, which is all of them but the music bank, so
            // the check below costs one HIRC read and then says nothing.
            foreach (var (id, clip) in MusicIndex.Timings(orig, Path.GetFileName(bankPath)))
                timed.TryAdd(id, clip);
            foreach (var entry in BnkBuilder.ReadDidx(didxSection.Body))
            {
                // Silence first, so a staged file can still override it below.
                if (muted is not null && muted.Contains(entry.Id) &&
                    !payload.ContainsKey(entry.Id) && dataSection is not null)
                {
                    var original = dataSection.Body.AsSpan((int)entry.Offset, (int)entry.Size).ToArray();
                    var quiet = SilentBankBuilder.Like(original);
                    if (quiet is not null)
                    {
                        perBank[entry.Id] = quiet;
                        placed.Add(entry.Id);
                        silenced++;
                        log.AppendLine($"  media {entry.Id}: muted — {entry.Size:N0} B of audio " +
                                       $"replaced with {quiet.Length:N0} B of silence.");
                    }
                    else log.AppendLine($"  media {entry.Id}: muted, but its header could not be read.");
                    continue;
                }

                if (!payload.TryGetValue(entry.Id, out var custom)) continue;

                // Said before the replacement is reported, so the log reads as the reason
                // and then the act rather than the other way round.
                var before = retimed.Count;
                Retimed(entry.Id, custom);
                if (retimed.Count > before) log.AppendLine("  LENGTH  " + retimed[^1]);

                if (prefetchMode && session.LooseMedia(entry.Id) is not null)
                {
                    var n = Math.Min((int)entry.Size, custom.Length);
                    perBank[entry.Id] = custom[..n];
                    trimmed++;
                    log.AppendLine($"  media {entry.Id}: prefetch mode — bank keeps the first " +
                                   $"{n:N0} B, the full {custom.Length:N0} B ships loose.");
                }
                else
                {
                    // The whole clip replaces the entry. The DIDX size grows from the
                    // vanilla prefetch length to the real length, which is the swap
                    // modders actually ship.
                    perBank[entry.Id] = custom;
                    log.AppendLine($"  media {entry.Id}: {entry.Size:N0} B -> {custom.Length:N0} B in the bank.");
                }
                placed.Add(entry.Id);
            }
            if (perBank.Count == 0) continue;

            var result = BnkBuilder.Build(orig, perBank);
            if (result.Replaced == 0) continue;

            var dest = Path.Combine(outRoot, bankPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, result.Bytes);
            banksWritten++;
            outputs.Add(Path.GetRelativePath(outRoot, dest));
            log.AppendLine($"BANK  {Path.GetFileName(bankPath)}: {result.Replaced} replaced, " +
                           $"{orig.Length:N0} B -> {result.Bytes.Length:N0} B");
        }

        // The bank that TIMES a clip is not always one of the banks being rebuilt. A
        // music bank times the clips it streams exactly as it times the ones it embeds,
        // and 575 of this game's 656 music media stream, so replacing one of those puts
        // no bank in the set above -- the window builds that set from the banks that
        // EMBED a staged id -- and the loop above then reads no timings at all. The skin
        // the staged rows were read from is what states them, so it is asked directly,
        // and only for ids nothing has accounted for yet.
        foreach (var file in skin?.Banks ?? [])
        {
            if (payload.Keys.All(timed.ContainsKey)) break;
            if (bankPaths.Contains(file.Path)) continue;
            byte[] states;
            try { states = file.Read(); } catch { continue; }
            foreach (var (id, clip) in MusicIndex.Timings(states, Path.GetFileName(file.Path)))
                if (payload.ContainsKey(id)) timed.TryAdd(id, clip);
        }

        // A loose Media/ file is written ONLY when the id has no bank entry to replace
        // (the spreadsheet's "MEDIA SFX" case), or when prefetch mode is on. Otherwise
        // the bank carries the whole clip and shipping Media/ is dead weight.
        var loose = 0;
        foreach (var (id, bytes) in payload)
        {
            // The active language's copy first: the overload alone falls through every language,
            // and an English line written over the Japanese file leaves the English one as it was.
            var src = session.LooseMedia(id, session.Language) ?? session.LooseMedia(id);
            if (placed.Contains(id) && !prefetchMode) continue;   // fully in the bank now
            if (src is null && placed.Contains(id)) continue;

            // Mirror the game's own path whenever it ships this id loose -- that is the
            // only way to get the language folder right, and 77,209 of the 80,664 loose
            // files are under one. The bucket rule below is a last resort for an id the
            // game never ships loose, where there is no language to derive.
            var dest = src is not null
                ? Path.Combine(outRoot, src.Path.Replace('/', Path.DirectorySeparatorChar))
                : Path.Combine(outRoot,
                               MediaLayout.PathFor(id).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            loose++;
            outputs.Add(Path.GetRelativePath(outRoot, dest));
            // A streamed clip is timed by its bank exactly as an embedded one is, and the
            // hazard is the same, so it is said here too.
            var beforeLoose = retimed.Count;
            Retimed(id, bytes);
            if (retimed.Count > beforeLoose) log.AppendLine("  LENGTH  " + retimed[^1]);
            var note = pending.FirstOrDefault(p => p.MediaId == id)?.Note;
            log.AppendLine($"AUDIO {id} -> {Path.GetRelativePath(outRoot, dest)}" +
                           (string.IsNullOrWhiteSpace(note) ? "" : $"   [{note}]") +
                           (src is null ? "   [no shipped original — path derived]" : ""));
        }

        // A muted sound no bank holds: silence its loose file, at the game's own path. A staged
        // file still wins, as it does in a bank.
        if (muted is not null)
            foreach (var id in muted.Where(id => !payload.ContainsKey(id) && !placed.Contains(id)).OrderBy(x => x))
            {
                var src = session.LooseMedia(id, session.Language) ?? session.LooseMedia(id);
                if (src is null) continue;
                byte[] quiet = null;
                try { quiet = SilentBankBuilder.Like(src.Read()); } catch { }
                if (quiet is null)
                {
                    log.AppendLine($"  media {id}: muted, but its loose file's header could not be read.");
                    continue;
                }
                var dest = Path.Combine(outRoot, src.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, quiet);
                outputs.Add(Path.GetRelativePath(outRoot, dest));
                loose++;
                silenced++;
                log.AppendLine($"MUTED {id} -> {Path.GetRelativePath(outRoot, dest)}   (loose file, {quiet.Length:N0} B of silence)");
            }

        var header = new StringBuilder();
        // The skin is only a label here. A build driven purely by staged files does not
        // need one: the media ids say which .bnk each sound belongs to.
        header.AppendLine($"{skin?.Label ?? "staged files"}   ({pending.Count} custom file(s)" +
                          (silenced > 0 ? $", {silenced} muted" : "") + ")");
        header.AppendLine();
        if (retimed.Count > 0)
        {
            // At the TOP of the log, above the per-media lines. This is the one thing in a
            // build that leaves the mod internally inconsistent, and the author cannot see
            // it in the output folder: the .bnk is the right size and every check passes.
            header.AppendLine($"LENGTH: {retimed.Count} clip(s) are not the length their .bnk states.");
            foreach (var note in retimed) header.AppendLine("  " + note);
            header.AppendLine();
        }
        if (trimmed > 0)
        {
            header.AppendLine("PREFETCH MODE: the bank keeps each sound's vanilla opening fragment and");
            header.AppendLine("the full clip ships as a loose Media/ file. Opening the .bnk will show");
            header.AppendLine("fragments, not your clips. Ship the WHOLE folder.");
            header.AppendLine();
        }
        else if (loose > 0)
        {
            header.AppendLine($"{loose} file(s) had no bank entry to replace and ship loose under");
            header.AppendLine("WwiseAudio/Media/. Everything else is inside the .bnk.");
            header.AppendLine();
        }
        header.Append(log);

        File.WriteAllText(Path.Combine(outRoot, "build-log.txt"), header.ToString(), new UTF8Encoding(true));
        return new Result(banksWritten, loose, trimmed, header.ToString(), outputs, silenced,
                          retimed.Count);
    }
}
