using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MRAudioKit;

/// <summary>
/// Keeps the usmap current from the community depot, so a patch does not leave the
/// tool showing hashes instead of event names.
///
/// A usmap is per game build and the mappings are what turn a bank's hashes into
/// readable names, so a stale one is the difference between a usable list and a wall
/// of numbers. The depot publishes one per changelist; the newest is the one that
/// matches the build people are playing.
///
/// The approach is the one Repak-X uses against the same repository: list the folder
/// through the GitHub contents API, pick the highest changelist out of the filenames,
/// and cache the listing's ETag so repeat checks cost nothing against the unauthenticated
/// rate limit.
/// </summary>
public static class UsmapFetch
{
    private const string ListingUrl =
        "https://api.github.com/repos/SpaceDepot/rivals-depot/contents/usmap?ref=main";

    /// <summary>Where downloaded maps are kept. Never the exe's folder: a build wipes that.</summary>
    public static string ManagedDir => Path.Combine(Settings.AppDataDir, "usmap");

    public sealed record Result(
        bool Changed, bool UpToDate, string Path, string FileName,
        long? Build, string Release, string Message);

    /// <summary>
    /// The changelist out of a depot filename such as
    /// <c>5.3.2-3870120+++depot_marvel+S10.0_release-Marvel.usmap</c>. Ordering by this
    /// rather than by date is what makes "latest" mean the newest game build.
    /// </summary>
    public static long? BuildOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var dash = name.IndexOf('-');
        if (dash < 0) return null;
        var rest = name[(dash + 1)..];
        var plus = rest.IndexOf('+');
        if (plus <= 0) return null;
        return long.TryParse(rest[..plus], out var cl) ? cl : null;
    }

    /// <summary>The readable season tag, e.g. <c>S10.0_release</c>.</summary>
    public static string ReleaseOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var i = name.IndexOf("+++", StringComparison.Ordinal);
        if (i < 0) return null;
        var after = name[(i + 3)..];
        var plus = after.IndexOf('+');
        if (plus < 0) return null;
        var release = after[(plus + 1)..].Split('-')[0];
        return release.Length == 0 ? null : release;
    }

    /// <summary>
    /// Is this path one we downloaded? A file the user picked themselves is theirs,
    /// and a routine check must not quietly swap it out from under them.
    /// </summary>
    public static bool IsManaged(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        static string N(string s) => s.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        return N(path).StartsWith(N(ManagedDir) + "\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// Look for a newer usmap and fetch it.
    /// </summary>
    /// <param name="force">
    /// A button press rather than a background check: ignore the cached ETag, and adopt
    /// the downloaded file even if the user currently points at one of their own.
    /// </param>
    public static async Task<Result> UpdateAsync(Settings settings, bool force,
                                                 Action<string> progress = null,
                                                 CancellationToken cancel = default)
    {
        progress?.Invoke("checking for a newer usmap…");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XzoundWave");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        using var req = new HttpRequestMessage(HttpMethod.Get, ListingUrl);
        if (!force && !string.IsNullOrEmpty(settings.UsmapEtag))
            req.Headers.TryAddWithoutValidation("If-None-Match", settings.UsmapEtag);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, cancel).ConfigureAwait(false); }
        catch (Exception ex)
        {
            return new Result(false, false, settings.UsmapPath, settings.UsmapFileName,
                BuildOf(settings.UsmapFileName), ReleaseOf(settings.UsmapFileName),
                "could not reach the depot: " + ex.Message);
        }

        using (resp)
        {
            // Nothing changed since last time -- but only believe it if the file we
            // recorded is still on disk.
            if ((int)resp.StatusCode == 304 &&
                !string.IsNullOrEmpty(settings.UsmapPath) && File.Exists(settings.UsmapPath))
                return new Result(false, true, settings.UsmapPath, settings.UsmapFileName,
                    BuildOf(settings.UsmapFileName), ReleaseOf(settings.UsmapFileName),
                    "already the newest the depot has.");

            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 304)
            {
                var hint = (int)resp.StatusCode == 403
                    ? " (GitHub rate limit — try again in a few minutes)" : "";
                return new Result(false, false, settings.UsmapPath, settings.UsmapFileName,
                    BuildOf(settings.UsmapFileName), ReleaseOf(settings.UsmapFileName),
                    $"the depot returned {(int)resp.StatusCode}{hint}.");
            }

            var etag = resp.Headers.ETag?.Tag;
            string body;
            if ((int)resp.StatusCode == 304)
            {
                // Listing unchanged but our file is gone: ask again without the ETag.
                using var again = await http.GetAsync(ListingUrl, cancel).ConfigureAwait(false);
                if (!again.IsSuccessStatusCode)
                    return new Result(false, false, null, null, null, null,
                        $"the depot returned {(int)again.StatusCode}.");
                etag = again.Headers.ETag?.Tag;
                body = await again.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            }
            else body = await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

            var (name, sha, url) = PickLatest(body);
            if (name is null)
                return new Result(false, false, settings.UsmapPath, settings.UsmapFileName,
                    null, null, "no usmap files in the depot listing.");

            var build = BuildOf(name);
            var release = ReleaseOf(name);
            var target = Path.Combine(ManagedDir, name);

            // A path the user chose is not ours to replace on a routine check.
            var custom = !string.IsNullOrWhiteSpace(settings.UsmapPath) && !IsManaged(settings.UsmapPath);
            var adopt = force || !custom;

            if (File.Exists(target) && settings.UsmapSha == sha)
            {
                settings.UsmapFileName = name;
                settings.UsmapEtag = etag ?? settings.UsmapEtag;
                if (adopt) settings.UsmapPath = target;
                settings.Save();
                return new Result(false, true, adopt ? target : settings.UsmapPath, name,
                    build, release,
                    custom && !adopt
                        ? $"the depot's newest is {name}, but your own usmap is selected."
                        : "already the newest the depot has.");
            }

            progress?.Invoke($"downloading {name}…");
            try
            {
                Directory.CreateDirectory(ManagedDir);
                var bytes = await http.GetByteArrayAsync(url, cancel).ConfigureAwait(false);
                if (bytes.Length < 1024)
                    return new Result(false, false, settings.UsmapPath, settings.UsmapFileName,
                        build, release, $"the download was only {bytes.Length} bytes — ignored.");

                // Write beside then move, so an interrupted download cannot leave a
                // half-written mappings file that fails far away from here.
                var tmp = target + ".part";
                await File.WriteAllBytesAsync(tmp, bytes, cancel).ConfigureAwait(false);
                File.Move(tmp, target, overwrite: true);

                settings.UsmapFileName = name;
                settings.UsmapSha = sha;
                settings.UsmapEtag = etag ?? settings.UsmapEtag;
                if (adopt) settings.UsmapPath = target;
                settings.Save();

                return new Result(true, false, adopt ? target : settings.UsmapPath, name,
                    build, release,
                    adopt
                        ? $"updated to {release ?? "the latest"} (build {build}), {bytes.Length:N0} B."
                        : $"downloaded {name}, but your own usmap is still the one selected.");
            }
            catch (Exception ex)
            {
                return new Result(false, false, settings.UsmapPath, settings.UsmapFileName,
                    build, release, "download failed: " + ex.Message);
            }
        }
    }

    /// <summary>Highest changelist in the listing, with its blob sha and download url.</summary>
    private static (string Name, string Sha, string Url) PickLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (null, null, null);

            string bestName = null, bestSha = null, bestUrl = null;
            long best = -1;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.TryGetProperty("type", out var t) && t.GetString() != "file") continue;
                if (!e.TryGetProperty("name", out var n)) continue;
                var name = n.GetString();
                if (name is null || !name.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase)) continue;
                if (BuildOf(name) is not { } cl || cl <= best) continue;

                best = cl;
                bestName = name;
                bestSha = e.TryGetProperty("sha", out var s) ? s.GetString() : null;
                bestUrl = e.TryGetProperty("download_url", out var u) ? u.GetString() : null;
            }
            return bestUrl is null ? (null, null, null) : (bestName, bestSha, bestUrl);
        }
        catch { return (null, null, null); }
    }

    /// <summary>XzoundWave.exe --usmap [--force]</summary>
    public static int Run(bool force)
    {
        var settings = Settings.Load();
        var current = settings.UsmapPath;
        Console.WriteLine($"current : {(string.IsNullOrEmpty(current) ? "(none set)" : current)}");
        if (!string.IsNullOrEmpty(settings.UsmapFileName))
            Console.WriteLine($"          build {BuildOf(settings.UsmapFileName)}, " +
                              $"{ReleaseOf(settings.UsmapFileName)}" +
                              (IsManaged(current) ? "" : "  [your own file, not ours]"));
        Console.WriteLine();

        // Task.Run keeps the await off whatever thread called in; blocking straight on
        // a dispatcher thread is what deadlocked the translator.
        var r = Task.Run(() => UsmapFetch.UpdateAsync(settings, force, Console.WriteLine))
                    .GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine($"latest  : {r.FileName ?? "(unknown)"}");
        if (r.Build is not null) Console.WriteLine($"          build {r.Build}, {r.Release}");
        Console.WriteLine($"path    : {r.Path ?? "(none)"}");
        Console.WriteLine($"result  : {r.Message}");
        return r.Path is null ? 1 : 0;
    }
}
