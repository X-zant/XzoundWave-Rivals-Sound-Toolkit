using System.IO;

namespace MRAudioKit.UI;

/// <summary>
/// Right-click › Extract wem(s)… and Extract as WAV…: the selected sounds' audio out of the game,
/// named {media id}-{event} so a file dropped back in stages onto the sound it came from. The old
/// window had these as Export WEM… and Export WAV…; the port carried only the modded extract.
///
/// What is written is what the row plays, the way Play reaches it (a voice line's full loose file
/// rather than the bank's prefetch stub; an open mod's audio for the mod's rows), never a staged
/// replacement: that is a file the author already has.
/// </summary>
public partial class MainWindow
{
    /// <summary>The render harness's folder for an extract, given instead of asked for.</summary>
    internal string ExtractFolderForShot;

    /// <summary>The render harness: right-click › Extract on the selection.</summary>
    internal Task ExtractRowsForShot(string folder, bool asWav)
    {
        ExtractFolderForShot = folder;
        return ExtractRowsAsync(asWav);
    }

    private async Task ExtractRowsAsync(bool asWav)
    {
        if (Sounds is null) { Say("pick a .bnk in the tree first."); return; }
        var picked = SelectedRows;
        // One file per medium: a clip several events play is still one file.
        var rows = picked.Where(r => !IsProjectRow(r)).GroupBy(r => r.MediaId).Select(g => g.First()).ToList();
        if (rows.Count == 0)
        {
            Say(picked.Count > 0 ? "a new line or an added sound is your own file already; pick one of the game's sounds."
                                 : "select one or more sounds first.");
            return;
        }
        var kind = asWav ? "WAV" : "wem";
        var folder = ExtractFolderForShot ?? await PickFolder($"Where to put {rows.Count} {kind} file(s)");
        if (folder is null) return;

        var preview = Preview();
        var session = Session;
        var sounds = Sounds;
        var done = await Working($"extracting {rows.Count} sound(s)", () =>
        {
            Directory.CreateDirectory(folder);
            int written = 0, failed = 0;
            string last = null, problem = null;
            foreach (var row in rows)
            {
                var data = preview.Bytes(session, sounds, row, ignoreStaged: true);
                if (data is null) { failed++; continue; }
                var note = ModSafeName(row.Display);
                var target = Path.Combine(folder, (string.IsNullOrEmpty(note) ? $"{row.MediaId}" : $"{row.MediaId}-{note}") + "." + kind.ToLowerInvariant());
                // One unwritable name must not stop the rest.
                try
                {
                    if (asWav)
                    {
                        var wav = preview.Decode(data, row.MediaId, out var why);
                        if (wav is null) { failed++; problem ??= why; continue; }
                        File.Copy(wav, target, true);
                    }
                    else File.WriteAllBytes(target, data);
                    written++;
                    last = target;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    problem ??= ex.Message;
                }
            }
            return ((int Written, int Failed, string Last, string Problem)?)(written, failed, last, problem);
        });
        if (done is not { } d) return;   // Working has already said why
        var skipped = picked.Count(IsProjectRow);
        Say((d.Written == 1 ? $"wrote {d.Last}" : $"wrote {d.Written} {kind} file(s) to {folder}") +
            (d.Failed > 0 ? $"; {d.Failed} could not be written{(d.Problem is null ? "" : $" ({d.Problem})")}" : "") +
            (skipped > 0 ? $"; {skipped} of your own (new lines, added sounds) left out" : "") +
            ". Dropping one back in stages it onto the sound it came from.");
    }
}
