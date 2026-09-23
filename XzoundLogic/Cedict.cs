using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MRAudioKit;

/// <summary>
/// An offline Chinese-English dictionary, for the labels neither the game nor the
/// built-in glossary covers.
///
/// This is a dictionary, not a translator: it gives a gloss per word, so a compound
/// comes out readable but blunt ("标记载具" as "mark / vehicle"). That is the trade
/// for something that ships in a few megabytes, needs no account, no server and no
/// network, and never sends a word anywhere.
///
/// The file is CC-CEDICT, in its published format. It is not bundled here: it is
/// CC BY-SA, so shipping it is the project's decision to make, and the reader simply
/// does nothing until a copy is present.
/// </summary>
public static class Cedict
{
    public const string FileName = "cedict_ts.u8";

    /// <summary>Where a copy is looked for, in order.</summary>
    public static IEnumerable<string> SearchPaths()
    {
        var exeDir = AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, FileName);
        yield return Path.Combine(exeDir, FileName + ".gz");
        yield return Path.Combine(Settings.AppDataDir, FileName);
        yield return Path.Combine(Settings.AppDataDir, FileName + ".gz");
        // A copy downloaded before the tool was renamed is still perfectly good.
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MRAudioKit");
        yield return Path.Combine(legacy, FileName);
        yield return Path.Combine(legacy, FileName + ".gz");
    }

    public static string FoundAt() => SearchPaths().FirstOrDefault(File.Exists);
    public static bool IsAvailable => FoundAt() is not null;

    private static Dictionary<string, string> _entries;
    private static int _longest;

    /// <summary>Simplified Chinese -> a short English gloss. Empty when no file.</summary>
    public static Dictionary<string, string> Entries
    {
        get
        {
            if (_entries is not null) return _entries;
            _entries = [];
            var path = FoundAt();
            if (path is null) return _entries;

            try
            {
                using var raw = File.OpenRead(path);
                Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                    ? new GZipStream(raw, CompressionMode.Decompress) : raw;
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    // Traditional Simplified [pin1 yin1] /gloss/gloss/
                    var sp = line.IndexOf(' ');
                    if (sp < 0) continue;
                    var sp2 = line.IndexOf(' ', sp + 1);
                    if (sp2 < 0) continue;
                    var simplified = line[(sp + 1)..sp2];

                    var slash = line.IndexOf('/');
                    if (slash < 0 || slash + 1 >= line.Length) continue;
                    var gloss = line[(slash + 1)..].TrimEnd('/');
                    var first = gloss.Split('/')[0].Trim();

                    // Drop the dictionary's editorial furniture: variants and
                    // cross-references are noise in a one-line label.
                    if (first.Length == 0 || first.StartsWith("variant of", StringComparison.OrdinalIgnoreCase)
                        || first.StartsWith("see ", StringComparison.OrdinalIgnoreCase)
                        || first.StartsWith("old variant", StringComparison.OrdinalIgnoreCase)) continue;

                    // Strip the parenthetical usage notes that make a label unreadable.
                    var paren = first.IndexOf('(');
                    if (paren > 0) first = first[..paren].Trim();
                    if (first.Length == 0) continue;

                    if (_entries.TryAdd(simplified, first) && simplified.Length > _longest)
                        _longest = simplified.Length;
                }
            }
            catch { _entries = []; }
            return _entries;
        }
    }

    public static int Count => Entries.Count;

    public const string Source = "https://www.mdbg.net/chinese/export/cedict/cedict_1_0_ts_utf-8_mdbg.txt.gz";
    public const string Licence = "CC-CEDICT, licensed CC BY-SA 4.0 by MDBG.";

    /// <summary>
    /// Download a copy into the user's app data. Deliberately NOT into the program
    /// folder or the repository: the dictionary is share-alike licensed, so whether
    /// it ships with the tool is the project's decision, not something a download
    /// button should make on its own.
    /// </summary>
    public static async Task<string> DownloadAsync(Action<string> progress,
                                                   CancellationToken cancel = default)
    {
        var dir = Settings.AppDataDir;
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, FileName + ".gz");

        progress?.Invoke("downloading the dictionary…");
        using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(3) })
        {
            http.DefaultRequestHeaders.Add("User-Agent", "XzoundWave");
            var bytes = await http.GetByteArrayAsync(Source, cancel).ConfigureAwait(false);
            await File.WriteAllBytesAsync(dest, bytes, cancel).ConfigureAwait(false);
        }

        // Force a reload, and prove it parsed rather than just landed.
        _entries = null;
        _longest = 0;
        var n = Count;
        if (n == 0)
        {
            try { File.Delete(dest); } catch { }
            throw new InvalidDataException("the downloaded file did not parse as CC-CEDICT.");
        }
        progress?.Invoke($"dictionary ready — {n:N0} entries");
        return dest;
    }

    /// <summary>
    /// Translate a run of Chinese by taking the longest dictionary match at each
    /// position. Chinese is not spaced, so a shorter-first pass would split words:
    /// 载具 ("vehicle") would come out as "carry" and "tool".
    /// </summary>
    public static string Gloss(string chinese)
    {
        var dict = Entries;
        if (dict.Count == 0 || string.IsNullOrEmpty(chinese)) return null;

        var parts = new List<string>();
        var i = 0;
        var matched = false;
        while (i < chinese.Length)
        {
            var take = Math.Min(_longest, chinese.Length - i);
            string hit = null;
            for (; take > 0; take--)
            {
                if (dict.TryGetValue(chinese.Substring(i, take), out hit)) break;
            }
            if (hit is not null)
            {
                parts.Add(hit);
                i += take;
                matched = true;
            }
            else
            {
                // Leave what the dictionary does not know, rather than dropping it.
                parts.Add(chinese[i].ToString());
                i++;
            }
        }
        return matched ? string.Join(" ", parts) : null;
    }
}
