using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security;
using System.Text;

namespace QUANGLNBrowser.Core.Services;

/// <summary>
/// Hồ sơ giọng đọc AI Tiếng Việt cho QUANGLN Player.
/// </summary>
public sealed record VoiceProfileConfig(
    string Id,
    string DisplayName,
    string RegionLabel,
    string EdgeVoiceName,
    string DefaultPitch,
    string DefaultRate,
    string Description);

/// <summary>
/// Dịch vụ tổng hợp giọng đọc AI Tiếng Việt (MP3) cho tính năng AI Lồng tiếng trong QUANGLN Player:
/// - Ưu tiên hàng đầu: Giọng đọc nơ-ron AI tự nhiên cao cấp Microsoft Neural TTS (vi-VN-NamMinhNeural cho Nam Miền Nam & Nam Trầm, vi-VN-HoaiMyNeural cho Nữ Miền Bắc & Nữ Trẻ Trung).
/// - Dự phòng thông minh: Google Translate TTS khi mất kết nối Edge TTS.
/// - Bộ nhớ đệm RAM LRU để tái sử dụng tức thì các câu thoại lặp lại.
/// </summary>
public sealed class VietnameseTtsService : IDisposable
{
    private const int MaxChunkChars = 200;
    private const int MaxCacheEntries = 250;
    private const string EdgeWsEndpoint =
        "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=6A5AA1D4EA654941A3E45624933F068D";

    public static readonly IReadOnlyDictionary<string, VoiceProfileConfig> Profiles =
        new Dictionary<string, VoiceProfileConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["quan"] = new("quan", "Minh Quân", "Nam Miền Nam", "vi-VN-NamMinhNeural", "+0Hz", "+0%", "Ấm áp, tự nhiên, phù hợp phim điện ảnh"),
            ["linh"] = new("linh", "Khánh Linh", "Nữ Miền Bắc", "vi-VN-HoaiMyNeural", "+0Hz", "+0%", "Truyền cảm, rõ ràng, phù hợp tài liệu & anime"),
            ["nam"]  = new("nam",  "Hoàng Nam", "Nam Trầm",    "vi-VN-NamMinhNeural", "-16Hz", "-3%", "Trầm ấm kiểu Review Phim & Trailer kịch tính"),
            ["vy"]   = new("vy",   "Thảo Vy",   "Nữ Trẻ Trung", "vi-VN-HoaiMyNeural", "+20Hz", "+6%", "Năng động, tươi sáng, giàu biểu cảm"),
            ["auto"] = new("auto", "Đa nhân vật", "Tự động",    "vi-VN-NamMinhNeural", "+0Hz", "+0%", "AI tự nhận diện giọng Nam / Nữ theo từng nhân vật")
        };

    private static readonly HttpClient SharedHttp = CreateHttpClient();
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, string> _base64AudioCache = new(StringComparer.Ordinal);
    private int _consecutiveEdgeFailures;
    private DateTime _lastEdgeFailureTime = DateTime.MinValue;
    private bool _disposed;

    public VietnameseTtsService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? SharedHttp;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36");
        return client;
    }

    /// <summary>
    /// Tổng hợp đoạn văn bản Tiếng Việt thành Data URI MP3 (`data:audio/mpeg;base64,...`)
    /// theo đúng hồ sơ giọng (Minh Quân, Khánh Linh, Hoàng Nam, Thảo Vy, Đa nhân vật).
    /// </summary>
    public async Task<string?> SynthesizeDataUriAsync(
        string? text,
        string voiceId = "auto",
        string gender = "male",
        string emotion = "natural",
        double speedRate = 1.0,
        CancellationToken cancellationToken = default)
    {
        string cleaned = NormalizeSpeechText(text);
        if (string.IsNullOrEmpty(cleaned))
        {
            return null;
        }

        string normVoice = string.IsNullOrWhiteSpace(voiceId) ? "auto" : voiceId.Trim().ToLowerInvariant();
        string normGender = string.IsNullOrWhiteSpace(gender) ? "male" : gender.Trim().ToLowerInvariant();
        string normEmotion = string.IsNullOrWhiteSpace(emotion) ? "natural" : emotion.Trim().ToLowerInvariant();

        string cacheKey = $"{normVoice}:{normGender}:{normEmotion}:{speedRate:F2}:{cleaned}";
        if (_base64AudioCache.TryGetValue(cacheKey, out string? cached))
        {
            return cached;
        }

        byte[]? audioBytes = null;
        string logSnippet = cleaned.Length <= 45 ? cleaned : cleaned[..45] + "...";
        string sourceEngine = "chưa rõ";
        bool shouldTryEdge = _consecutiveEdgeFailures < 3 ||
                             (DateTime.UtcNow - _lastEdgeFailureTime) > TimeSpan.FromSeconds(60);

        if (shouldTryEdge)
        {
            audioBytes = await TrySynthesizeEdgeTtsAsync(cleaned, normVoice, normGender, normEmotion, speedRate, cancellationToken);
            if (audioBytes is not null && audioBytes.Length > 0)
            {
                _consecutiveEdgeFailures = 0;
                sourceEngine = "Microsoft Edge Neural TTS";
            }
            else
            {
                Interlocked.Increment(ref _consecutiveEdgeFailures);
                _lastEdgeFailureTime = DateTime.UtcNow;
            }
        }

        if (audioBytes is null || audioBytes.Length == 0)
        {
            audioBytes = await FallbackGoogleTtsAsync(cleaned, cancellationToken);
            if (audioBytes is not null && audioBytes.Length > 0)
            {
                sourceEngine = "Google TTS Fallback";
            }
        }

        if (audioBytes is null || audioBytes.Length == 0)
        {
            AppLogger.Warn($"[AI Lồng tiếng] Không thể tổng hợp giọng đọc cho câu: \"{logSnippet}\"");
            return null;
        }

        AppLogger.Info($"[AI Lồng tiếng] Tổng hợp thành công ({sourceEngine}, {audioBytes.Length} bytes, giọng {normVoice}): \"{logSnippet}\"");

        string dataUri = "data:audio/mpeg;base64," + Convert.ToBase64String(audioBytes);

        if (_base64AudioCache.Count >= MaxCacheEntries)
        {
            _base64AudioCache.Clear();
        }
        _base64AudioCache[cacheKey] = dataUri;
        return dataUri;
    }

    private async Task<byte[]?> TrySynthesizeEdgeTtsAsync(
        string text,
        string voiceId,
        string gender,
        string emotion,
        double speedRate,
        CancellationToken cancellationToken)
    {
        try
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Pragma", "no-cache");
            ws.Options.SetRequestHeader("Cache-Control", "no-cache");
            ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0");
            ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
            ws.Options.SetRequestHeader("Accept-Encoding", "gzip, deflate, br, zstd");
            ws.Options.SetRequestHeader("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");

            string connectionId = Guid.NewGuid().ToString("N");
            string wsUrl = $"{EdgeWsEndpoint}&ConnectionId={connectionId}";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            await ws.ConnectAsync(new Uri(wsUrl), timeoutCts.Token);
            if (ws.State != WebSocketState.Open)
            {
                return null;
            }

            // Gửi message 1: speech.config
            string configMsg =
                "Content-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";
            byte[] configBytes = Encoding.UTF8.GetBytes(configMsg);
            await ws.SendAsync(new ArraySegment<byte>(configBytes), WebSocketMessageType.Text, true, timeoutCts.Token);

            // Xác định giọng đọc, pitch và rate
            var (edgeVoice, pitchStr, rateStr) = ResolveVoiceParams(voiceId, gender, emotion, speedRate);

            string requestId = Guid.NewGuid().ToString("N");
            string escapedText = SecurityElement.Escape(text);
            string ssmlMsg =
                $"X-RequestId:{requestId}\r\nContent-Type:application/ssml+xml\r\nPath:ssml\r\n\r\n" +
                $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='vi-VN'>" +
                $"<voice name='{edgeVoice}'>" +
                $"<prosody pitch='{pitchStr}' rate='{rateStr}' volume='+0%'>{escapedText}</prosody>" +
                $"</voice></speak>";

            byte[] ssmlBytes = Encoding.UTF8.GetBytes(ssmlMsg);
            await ws.SendAsync(new ArraySegment<byte>(ssmlBytes), WebSocketMessageType.Text, true, timeoutCts.Token);

            // Đọc các chunk audio nhị phân
            using var audioStream = new MemoryStream();
            byte[] buffer = new byte[8192];

            while (ws.State == WebSocketState.Open && !timeoutCts.IsCancellationRequested)
            {
                var receiveResult = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeoutCts.Token);
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (receiveResult.MessageType == WebSocketMessageType.Text)
                {
                    string textMsg = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                    if (textMsg.Contains("Path:turn.end", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
                else if (receiveResult.MessageType == WebSocketMessageType.Binary && receiveResult.Count > 2)
                {
                    // 2 bytes đầu là header length (big endian)
                    int headerLen = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2));
                    int audioOffset = 2 + headerLen;

                    if (audioOffset < receiveResult.Count)
                    {
                        string headerText = Encoding.ASCII.GetString(buffer, 2, Math.Min(headerLen, receiveResult.Count - 2));
                        if (headerText.Contains("Path:audio", StringComparison.Ordinal))
                        {
                            int audioBytesCount = receiveResult.Count - audioOffset;
                            audioStream.Write(buffer, audioOffset, audioBytesCount);
                        }
                    }
                }
            }

            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            }
            catch { }

            return audioStream.Length > 256 ? audioStream.ToArray() : null;
        }
        catch (Exception ex)
        {
            AppLogger.Info($"Edge TTS synthesis note: {ex.Message}");
            return null;
        }
    }

    private static (string EdgeVoice, string Pitch, string Rate) ResolveVoiceParams(
        string voiceId,
        string gender,
        string emotion,
        double speedRate)
    {
        string edgeVoice;
        int pitchOffsetHz = 0;
        int ratePercent = (int)Math.Round((speedRate - 1.0) * 100);

        switch (voiceId.ToLowerInvariant())
        {
            case "quan": // Minh Quân — Nam Miền Nam (ấm áp, tự nhiên)
                edgeVoice = "vi-VN-NamMinhNeural";
                pitchOffsetHz = 0;
                break;

            case "linh": // Khánh Linh — Nữ Miền Bắc (truyền cảm, rõ ràng)
                edgeVoice = "vi-VN-HoaiMyNeural";
                pitchOffsetHz = 0;
                break;

            case "nam": // Hoàng Nam — Nam Trầm (Review Phim, Trailer kịch tính)
                edgeVoice = "vi-VN-NamMinhNeural";
                pitchOffsetHz = -16;
                ratePercent -= 3;
                break;

            case "vy": // Thảo Vy — Nữ Trẻ Trung (năng động, tươi sáng)
                edgeVoice = "vi-VN-HoaiMyNeural";
                pitchOffsetHz = +20;
                ratePercent += 5;
                break;

            default: // auto: Đa nhân vật tự động
                if (gender.Equals("female", StringComparison.OrdinalIgnoreCase))
                {
                    edgeVoice = "vi-VN-HoaiMyNeural";
                    pitchOffsetHz = +3;
                }
                else
                {
                    edgeVoice = "vi-VN-NamMinhNeural";
                    pitchOffsetHz = -2;
                }
                break;
        }

        // Điều chỉnh sắc thái cảm xúc
        if (emotion.Equals("dramatic", StringComparison.OrdinalIgnoreCase))
        {
            pitchOffsetHz -= 4;
            ratePercent += 4;
        }
        else if (emotion.Equals("soft", StringComparison.OrdinalIgnoreCase))
        {
            pitchOffsetHz += 4;
            ratePercent -= 6;
        }

        ratePercent = Math.Clamp(ratePercent, -50, 100);
        string pitchStr = $"{pitchOffsetHz:+#;-#;+0}Hz";
        string rateStr = $"{ratePercent:+#;-#;+0}%";

        return (edgeVoice, pitchStr, rateStr);
    }

    private async Task<byte[]?> FallbackGoogleTtsAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            var chunks = SplitIntoSpeechChunks(text, MaxChunkChars);
            using var mergedStream = new MemoryStream();

            foreach (string chunk in chunks)
            {
                string url = $"https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&tl=vi&q={Uri.EscapeDataString(chunk)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Referer", "https://translate.google.com/");
                using var resp = await _http.SendAsync(req, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                byte[] bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length > 0)
                {
                    await mergedStream.WriteAsync(bytes, cancellationToken);
                }
            }

            return mergedStream.Length > 0 ? mergedStream.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    public static string NormalizeSpeechText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    public static List<string> SplitIntoSpeechChunks(string text, int maxLen = MaxChunkChars)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return chunks;
        }

        string remaining = text.Trim();
        while (remaining.Length > maxLen)
        {
            int splitAt = remaining.LastIndexOfAny(['.', '!', '?', ';', ',', ' '], maxLen);
            if (splitAt < maxLen / 3)
            {
                splitAt = maxLen;
            }

            string head = remaining[..splitAt].Trim();
            if (head.Length > 0)
            {
                chunks.Add(head);
            }

            remaining = remaining[splitAt..].Trim();
        }

        if (remaining.Length > 0)
        {
            chunks.Add(remaining);
        }

        return chunks;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _base64AudioCache.Clear();
    }
}
