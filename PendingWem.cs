using System.IO;
using System.Text.RegularExpressions;

using Xzound;

namespace MRAudioKit;

/// <summary>
/// A custom .wem staged for the build, identified entirely by its filename.
///
/// The convention is <c>{MediaID}-{whatever you want}.wem</c>: everything from the
/// first hyphen on is the author's own note and is ignored for assignment. That is
/// what makes batch drops work -- a folder of files someone named months ago still
/// assigns itself, and the note survives so they can remember what each one was for.
/// </summary>
public sealed class PendingWem
{
    public uint MediaId { get; init; }
    public string Note { get; init; } = "";
    public string FileName { get; init; }
    public string FullPath { get; init; }
    public long Bytes { get; init; }
    public string Codec { get; init; }
    public ushort FormatTag { get; init; }

    /// <summary>
    /// The wem bytes, when this file was converted from mp3/wav/ogg/etc rather than
    /// dropped in as a wem. Null means the file on disk is already the payload, so
    /// the build reads it straight from <see cref="FullPath"/> and nothing is held
    /// in memory for the common case of a folder full of wems.
    /// </summary>
    public byte[] Payload { get; init; }

    /// <summary>Source extension when converted (e.g. "mp3"), empty when not.</summary>
    public string Converted { get; init; } = "";
    /// <summary>Which bank this lands in — or that it ships as a loose streamed wem.</summary>
    public string Status { get; set; } = "";

    /// <summary>What is being overwritten: the shipped voice line, or the event name
    /// when there is no subtitle. Purely for the author's reference while working.</summary>
    public string Original { get; set; } = "";
    public string OriginalEvent { get; set; } = "";

    /// <summary>Every bank embedding this media, when it is in more than one.</summary>
    public string BankList { get; set; } = "";
    public string Tip
    {
        get
        {
            var head = string.IsNullOrWhiteSpace(OriginalEvent)
                ? Original : $"{OriginalEvent}\n{Original}".TrimEnd();
            var tail = Status.StartsWith("loose", StringComparison.OrdinalIgnoreCase)
                ? "\n\nNo bank embeds this id, so it ships as a loose Media/ wem instead."
                : "";
            var banks = string.IsNullOrWhiteSpace(BankList) ? "" : $"\n\nEmbedded in:\n{BankList}";
            return head + tail + banks;
        }
    }

    private static readonly Regex Leading = new(@"^\s*(\d{1,10})", RegexOptions.Compiled);

    /// <summary>Null when the filename does not begin with a media id.</summary>
    /// <summary>
    /// Convert one file to wem bytes, whatever it started as. Split out of
    /// <see cref="FromFile"/> so a single file can be staged against MANY sounds
    /// without being decoded and re-encoded once per sound -- fifty ids would
    /// otherwise mean fifty identical encodes.
    /// </summary>
    public static byte[] ToWemBytes(string path, out string reason)
    {
        reason = null;
        try
        {
            if (AudioDecode.IsWem(path)) return File.ReadAllBytes(path);
            return XzoundCore.Encode(AudioDecode.ToPcm(path));
        }
        catch (Exception ex)
        {
            reason = $"{Path.GetFileName(path)} — {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// One file assigned to one media id, with the id given rather than read from the
    /// filename. Used when the author picks the targets in the grid instead of naming
    /// them in the file.
    /// </summary>
    public static PendingWem ForMedia(uint mediaId, string path, byte[] bytes, string note)
    {
        var info = BnkBuilder.Inspect(bytes);
        return new PendingWem
        {
            MediaId = mediaId,
            Note = note ?? "",
            FileName = Path.GetFileName(path),
            FullPath = path,
            Bytes = bytes.Length,
            Codec = info.Codec,
            FormatTag = info.FormatTag,
            Payload = bytes,
            Converted = AudioDecode.IsWem(path)
                ? "" : Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
        };
    }

    public static PendingWem FromFile(string path, out string reason)
    {
        reason = null;
        var name = Path.GetFileNameWithoutExtension(path);
        var m = Leading.Match(name);
        if (!m.Success || !uint.TryParse(m.Groups[1].Value, out var id))
        {
            reason = $"{Path.GetFileName(path)} — filename must start with the media id, " +
                     "e.g. 1000585642-my note.wem";
            return null;
        }

        // A wem is used as-is. Anything else we can decode is converted here, so the
        // author never has to know what a wem is to replace a sound with an mp3.
        byte[] data, payload = null;
        var converted = "";
        if (AudioDecode.IsWem(path))
        {
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex) { reason = $"{Path.GetFileName(path)} — {ex.Message}"; return null; }
        }
        else
        {
            try
            {
                var pcm = AudioDecode.ToPcm(path);
                data = payload = XzoundCore.Encode(pcm);
                converted = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            }
            catch (Exception ex)
            {
                reason = $"{Path.GetFileName(path)} — {ex.Message}";
                return null;
            }
        }

        var info = BnkBuilder.Inspect(data);
        if (!info.IsRiff)
        {
            reason = $"{Path.GetFileName(path)} — {info.Codec}; a replacement must be a Wwise .wem";
            return null;
        }

        // Everything after the first hyphen is the author's note. No hyphen = no note.
        // TrimStart first: "12345 - my note.wem" is a name people actually write, and
        // without it the separator is no longer the first character and leaks into the note.
        var rest = name[m.Length..].TrimStart();
        var note = rest.StartsWith('-') ? rest[1..].Trim() : rest.Trim(' ', '_');

        return new PendingWem
        {
            MediaId = id,
            Note = note,
            FileName = Path.GetFileName(path),
            FullPath = path,
            Bytes = data.Length,
            Codec = info.Codec,
            FormatTag = info.FormatTag,
            Payload = payload,
            Converted = converted,
        };
    }
}
