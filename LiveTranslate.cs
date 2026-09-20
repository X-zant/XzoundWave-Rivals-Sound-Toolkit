using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MRAudioKit;

/// <summary>
/// Last resort for labels the game and the glossary between them cannot translate.
///
/// This is the only part of the tool that talks to a network, so it is deliberate on
/// every count: it is off until a provider is chosen, nothing is sent unless someone
/// asks, only the game's own Chinese label text goes out, and every result is written
/// to disk so a phrase is sent once and never again.
///
/// The provider is pluggable because the choice is a real trade-off: DeepL is the
/// best translation but wants an account, a local model needs installing but nothing
/// ever leaves the machine, and the keyless public service needs neither but is the
/// weakest of the three.
/// </summary>
public static class LiveTranslate
{
    public const string ProviderNone = "none";
    public const string ProviderDeepL = "deepl";
    public const string ProviderLibre = "libretranslate";
    public const string ProviderOllama = "ollama";
    public const string ProviderMyMemory = "mymemory";

    public static readonly string[] Providers =
        [ProviderNone, ProviderDeepL, ProviderLibre, ProviderOllama, ProviderMyMemory];

    /// <summary>What the author needs to know before picking one.</summary>
    public static string Describe(string provider) => provider switch
    {
        ProviderDeepL => "DeepL — best quality. Needs a free API key from deepl.com (an account).",
        ProviderLibre => "LibreTranslate — runs on your machine or your own server. No account, nothing leaves.",
        ProviderOllama => "Ollama — a local model on your machine. No account, nothing leaves.",
        ProviderMyMemory => "MyMemory — no account and no install, but the weakest of the three.",
        _ => "Off. Untranslated labels stay in Chinese.",
    };

    /// <summary>Why a provider cannot be used yet, or null when it is ready.</summary>
    public static string WhyNot(Settings s) => s.TranslateProvider switch
    {
        null or "" or ProviderNone => "no translation provider is chosen",
        ProviderDeepL when string.IsNullOrWhiteSpace(s.DeepLKey) => "DeepL is chosen but no API key is set",
        ProviderLibre when string.IsNullOrWhiteSpace(s.LocalTranslateUrl) =>
            "LibreTranslate is chosen but no server address is set (e.g. http://localhost:5000)",
        ProviderOllama when string.IsNullOrWhiteSpace(s.LocalTranslateUrl) =>
            "Ollama is chosen but no server address is set (e.g. http://localhost:11434)",
        ProviderOllama when string.IsNullOrWhiteSpace(s.LocalTranslateModel) =>
            "Ollama is chosen but no model name is set (e.g. qwen2.5:7b)",
        _ => null,
    };

    public static bool IsLocal(string provider) => provider is ProviderLibre or ProviderOllama;

    /// <summary>Where the text goes, for the confirmation prompt.</summary>
    public static string Destination(Settings s) => s.TranslateProvider switch
    {
        ProviderDeepL => "api-free.deepl.com",
        ProviderLibre or ProviderOllama => s.LocalTranslateUrl + "  (your own machine or server)",
        ProviderMyMemory => "api.mymemory.translated.net",
        _ => "nowhere",
    };

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XzoundWave", "translations.json");

    private static Dictionary<string, string> _cache;

    private static Dictionary<string, string> Cache
    {
        get
        {
            if (_cache is not null) return _cache;
            try
            {
                _cache = File.Exists(CachePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath)) ?? []
                    : [];
            }
            catch { _cache = []; }
            return _cache;
        }
    }

    public static int Count => Cache.Count;

    /// <summary>A previously fetched translation, or null. Never touches a network.</summary>
    public static string Lookup(string chinese) =>
        Cache.TryGetValue(chinese, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath,
                JsonSerializer.Serialize(Cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// Fetch the phrases we do not have. Returns how many were added. Anything that
    /// fails is not cached, so the label stays Chinese and a later run can try again.
    /// </summary>
    public static async Task<int> FetchAsync(Settings settings, IEnumerable<string> phrases,
                                             Action<string> progress, CancellationToken cancel = default)
    {
        var why = WhyNot(settings);
        if (why is not null) throw new InvalidOperationException(why);

        var todo = phrases.Where(p => !string.IsNullOrWhiteSpace(p) && Lookup(p) is null)
                          .Distinct().ToList();
        if (todo.Count == 0) return 0;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Add("User-Agent", "XzoundWave");

        var added = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            if (cancel.IsCancellationRequested) break;
            var phrase = todo[i];
            progress?.Invoke($"translating {i + 1}/{todo.Count}…");
            try
            {
                var text = settings.TranslateProvider switch
                {
                    ProviderDeepL => await DeepL(http, settings, phrase, cancel).ConfigureAwait(false),
                    ProviderLibre => await Libre(http, settings, phrase, cancel).ConfigureAwait(false),
                    ProviderOllama => await Ollama(http, settings, phrase, cancel).ConfigureAwait(false),
                    _ => await MyMemory(http, phrase, cancel).ConfigureAwait(false),
                };

                // A service that echoes the input back has nothing; caching that would
                // mean never trying again.
                if (!string.IsNullOrWhiteSpace(text) && text != phrase &&
                    !CategoryTranslator.NeedsTranslation(text))
                {
                    Cache[phrase] = text.Trim();
                    added++;
                }
            }
            catch (Exception ex) { progress?.Invoke($"  {phrase}: {ex.Message}"); }

            // Only the public service is rate limited; a local model has no reason to wait.
            if (i < todo.Count - 1 && !IsLocal(settings.TranslateProvider))
                await Task.Delay(350, cancel).ConfigureAwait(false);
        }

        if (added > 0) Save();
        return added;
    }

    // ---- providers ---------------------------------------------------------

    private static async Task<string> DeepL(HttpClient http, Settings s, string q, CancellationToken c)
    {
        // A free key ends in ":fx" and uses a different host from a paid one.
        var host = s.DeepLKey.TrimEnd().EndsWith(":fx", StringComparison.Ordinal)
            ? "api-free.deepl.com" : "api.deepl.com";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v2/translate")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["text"] = q, ["source_lang"] = "ZH", ["target_lang"] = "EN",
            }),
        };
        req.Headers.Add("Authorization", "DeepL-Auth-Key " + s.DeepLKey.Trim());
        using var resp = await http.SendAsync(req, c).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(c).ConfigureAwait(false));
        return doc.RootElement.GetProperty("translations")[0].GetProperty("text").GetString();
    }

    private static async Task<string> Libre(HttpClient http, Settings s, string q, CancellationToken c)
    {
        var url = s.LocalTranslateUrl.TrimEnd('/');
        if (!url.EndsWith("/translate", StringComparison.OrdinalIgnoreCase)) url += "/translate";
        var body = new Dictionary<string, string>
        {
            ["q"] = q, ["source"] = "zh", ["target"] = "en", ["format"] = "text",
        };
        if (!string.IsNullOrWhiteSpace(s.DeepLKey)) body["api_key"] = s.DeepLKey.Trim();
        using var resp = await http.PostAsync(url,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), c)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(c).ConfigureAwait(false));
        return doc.RootElement.GetProperty("translatedText").GetString();
    }

    private static async Task<string> Ollama(HttpClient http, Settings s, string q, CancellationToken c)
    {
        var url = s.LocalTranslateUrl.TrimEnd('/');
        if (!url.Contains("/api/", StringComparison.OrdinalIgnoreCase)) url += "/api/generate";

        // These are short UI labels, not prose. Saying so keeps a general model from
        // answering with an explanation instead of a translation.
        var prompt =
            "Translate this Chinese video-game UI label into short English. " +
            "Reply with the translation only, no quotes, no explanation, no punctuation at the end.\n\n" + q;
        var body = new { model = s.LocalTranslateModel, prompt, stream = false, options = new { temperature = 0 } };
        using var resp = await http.PostAsync(url,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), c)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(c).ConfigureAwait(false));
        var text = doc.RootElement.GetProperty("response").GetString() ?? "";
        return text.Trim().Trim('"', '\'', '.', '。').Split('\n')[0].Trim();
    }

    private static async Task<string> MyMemory(HttpClient http, string q, CancellationToken c)
    {
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(q)}&langpair=zh-CN|en";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, c).ConfigureAwait(false));
        return doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
    }
}
