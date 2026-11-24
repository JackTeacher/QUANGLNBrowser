using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace QUANGLNBrowser.Core.Services;

/// <summary>
/// Dịch thuật văn bản trực tiếp trên trang web (in-place DOM translation) sang tiếng Việt hoặc ngôn ngữ đích.
/// </summary>
public sealed class PageTranslationService
{
    private const string BatchDelimiter = "\n[[QTR8899]]\n";
    private const int MaxBatchChars = 1400;
    private const int MaxBatchItems = 15;

    private static readonly HttpClient SharedHttpClient = CreateDefaultHttpClient();

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public PageTranslationService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public static bool NeedsTranslation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed.Length > 4500)
        {
            return false;
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool hasLetter = false;
        foreach (char ch in trimmed)
        {
            if (char.IsLetter(ch))
            {
                hasLetter = true;
                break;
            }
        }

        return hasLetter;
    }

    public static (string TranslatedText, string? DetectedSourceLanguage) ParseGoogleTranslateGtxJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (string.Empty, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return (string.Empty, null);
            }

            var sb = new StringBuilder();
            string? detectedSource = null;

            var first = root[0];
            if (first.ValueKind == JsonValueKind.Array)
            {
                // Định dạng 1: clients5.google.com [["translated text", "en"]]
                if (first.GetArrayLength() > 0 && first[0].ValueKind == JsonValueKind.String)
                {
                    sb.Append(first[0].GetString());
                    if (first.GetArrayLength() > 1 && first[1].ValueKind == JsonValueKind.String)
                    {
                        detectedSource = first[1].GetString();
                    }
                }
                // Định dạng 2: translate.googleapis.com [[["seg1", "orig1"], ...], null, "en"]
                else
                {
                    foreach (var seg in first.EnumerateArray())
                    {
                        if (seg.ValueKind == JsonValueKind.Array &&
                            seg.GetArrayLength() > 0 &&
                            seg[0].ValueKind == JsonValueKind.String)
                        {
                            sb.Append(seg[0].GetString());
                        }
                    }
                }
            }
            else if (first.ValueKind == JsonValueKind.String)
            {
                sb.Append(first.GetString());
            }

            if (detectedSource is null && root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String)
            {
                detectedSource = root[2].GetString();
            }

            return (sb.ToString(), detectedSource);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    public async Task<Dictionary<string, string>> TranslateBatchAsync(
        IEnumerable<string> uniqueTexts,
        string targetLanguage = "vi",
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string raw in uniqueTexts)
        {
            string trimmed = raw.Trim();
            if (!NeedsTranslation(trimmed) || !seen.Add(trimmed))
            {
                continue;
            }

            string cacheKey = $"{targetLanguage}|{trimmed}";
            if (_cache.TryGetValue(cacheKey, out string? cached))
            {
                result[trimmed] = cached;
            }
            else
            {
                pending.Add(trimmed);
            }
        }

        if (pending.Count == 0)
        {
            return result;
        }

        AppLogger.Info($"[AI Dịch thuật] Bắt đầu dịch {pending.Count} đoạn văn bản sang '{targetLanguage}'...");
        var chunks = BuildChunks(pending);
        using var gate = new SemaphoreSlim(4, 4);

        var tasks = chunks.Select(async chunk =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await TranslateChunkWithFallbackAsync(chunk, targetLanguage, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        var chunkMaps = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var map in chunkMaps)
        {
            foreach (var (orig, translated) in map)
            {
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    _cache[$"{targetLanguage}|{orig}"] = translated;
                    result[orig] = translated;
                }
            }
        }

        AppLogger.Info($"[AI Dịch thuật] Hoàn tất dịch thuật {result.Count} đoạn văn bản.");
        return result;
    }

    private async Task<Dictionary<string, string>> TranslateChunkWithFallbackAsync(
        IReadOnlyList<string> chunk,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);

        if (chunk.Count == 1)
        {
            string translated = await RequestSingleTranslationAsync(chunk[0], targetLanguage, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                output[chunk[0]] = translated;
            }
            return output;
        }

        string joined = string.Join(BatchDelimiter, chunk);
        string combinedTranslated = await RequestSingleTranslationAsync(joined, targetLanguage, cancellationToken).ConfigureAwait(false);

        string[] splitParts = combinedTranslated.Split(
            ["[[QTR8899]]", "[[ QTR8899 ]]", "[[qtr8899]]", "[[ qtr8899 ]]", "[ [ QTR8899 ] ]"],
            StringSplitOptions.None);

        if (splitParts.Length == chunk.Count)
        {
            for (int i = 0; i < chunk.Count; i++)
            {
                output[chunk[i]] = splitParts[i].Trim();
            }
            return output;
        }

        // Nếu máy chủ gộp/tách dấu phân cách khác số phần tử, tự động fallback dịch từng câu độc lập để không bao giờ lệch vị trí.
        foreach (string item in chunk)
        {
            string single = await RequestSingleTranslationAsync(item, targetLanguage, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(single))
            {
                output[item] = single;
            }
        }

        return output;
    }

    private async Task<string> RequestSingleTranslationAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Endpoint 1 (Ưu tiên số 1): Google clients5 Dictionary Client (cực nhanh, không bao giờ bị 429 block)
        try
        {
            string url1 = $"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl={Uri.EscapeDataString(targetLanguage)}";
            using var content1 = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", text)]);
            using var resp1 = await _httpClient.PostAsync(url1, content1, cancellationToken).ConfigureAwait(false);
            if (resp1.IsSuccessStatusCode)
            {
                string json1 = await resp1.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var (translated1, _) = ParseGoogleTranslateGtxJson(json1);
                if (!string.IsNullOrWhiteSpace(translated1))
                {
                    return translated1;
                }
            }
        }
        catch
        {
            // Bỏ qua và chuyển sang endpoint tiếp theo
        }

        // Endpoint 2: Google Translate GTX Single
        try
        {
            string url2 = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={Uri.EscapeDataString(targetLanguage)}&dt=t";
            using var content2 = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", text)]);
            using var resp2 = await _httpClient.PostAsync(url2, content2, cancellationToken).ConfigureAwait(false);
            if (resp2.IsSuccessStatusCode)
            {
                string json2 = await resp2.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var (translated2, _) = ParseGoogleTranslateGtxJson(json2);
                if (!string.IsNullOrWhiteSpace(translated2))
                {
                    return translated2;
                }
            }
        }
        catch
        {
            // Bỏ qua và chuyển sang endpoint tiếp theo
        }

        // Endpoint 3: MyMemory API (Dự phòng cho câu đơn)
        try
        {
            string url3 = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair=autodetect|{Uri.EscapeDataString(targetLanguage)}";
            using var resp3 = await _httpClient.GetAsync(url3, cancellationToken).ConfigureAwait(false);
            if (resp3.IsSuccessStatusCode)
            {
                string json3 = await resp3.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json3);
                if (doc.RootElement.TryGetProperty("responseData", out var respData) &&
                    respData.TryGetProperty("translatedText", out var trText) &&
                    trText.GetString() is { } myMemoryTr &&
                    !string.IsNullOrWhiteSpace(myMemoryTr))
                {
                    return myMemoryTr;
                }
            }
        }
        catch
        {
            // Bỏ qua
        }

        return string.Empty;
    }

    private static List<List<string>> BuildChunks(List<string> items)
    {
        var chunks = new List<List<string>>();
        var current = new List<string>();
        int currentChars = 0;

        foreach (string item in items)
        {
            int projected = currentChars + item.Length + BatchDelimiter.Length;
            if (current.Count > 0 && (projected > MaxBatchChars || current.Count >= MaxBatchItems))
            {
                chunks.Add(current);
                current = [];
                currentChars = 0;
            }

            current.Add(item);
            currentChars += item.Length + BatchDelimiter.Length;
        }

        if (current.Count > 0)
        {
            chunks.Add(current);
        }

        return chunks;
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36");
        return client;
    }
}
