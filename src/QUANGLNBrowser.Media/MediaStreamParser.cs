using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using QUANGLNBrowser.Core.Models;

namespace QUANGLNBrowser.Media;

/// <summary>
/// Phân tích output tiến độ của FFmpeg, yt-dlp, JSON định dạng video, nhận diện nền tảng streaming kiểu Cốc Cốc
/// và chuẩn hóa tên file tải xuống.
/// </summary>
public static partial class MediaStreamParser
{
    [GeneratedRegex(@"Duration:\s*(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled)]
    private static partial Regex FfmpegDurationRegex();

    [GeneratedRegex(@"time=\s*(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled)]
    private static partial Regex FfmpegTimeRegex();

    [GeneratedRegex(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled)]
    private static partial Regex YtDlpPercentRegex();

    private static readonly string[] YtDlpPlatformHosts =
    [
        "youtube.com",
        "youtu.be",
        "tiktok.com",
        "facebook.com",
        "fb.watch",
        "instagram.com",
        "twitter.com",
        "x.com",
        "vimeo.com",
        "dailymotion.com",
        "bilibili.com",
        "twitch.tv",
        "soundcloud.com"
    ];

    private static readonly string[] StreamingCdnChunkHosts =
    [
        "googlevideo.com",
        "tiktokcdn.com",
        "tiktokv.com",
        "byteoversea.com",
        "ibytedtos.com",
        "fbcdn.net",
        "cdninstagram.com",
        "twimg.com",
        "vimeocdn.com",
        "bilivideo.com",
        "dmcdn.net"
    ];

    /// <summary>
    /// Kiểm tra xem URL có thuộc các nền tảng video dùng MSE/DASH cần tải qua yt-dlp (giống Cốc Cốc Savior) hay không.
    /// </summary>
    public static bool IsYtDlpFirstPlatform(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();
        foreach (string p in YtDlpPlatformHosts)
        {
            if (host.Equals(p, StringComparison.Ordinal) || host.EndsWith("." + p, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Kiểm tra xem trang cụ thể có phải là trang đang xem video/clip trên các nền tảng lớn hay không.
    /// </summary>
    public static bool IsLikelyVideoWatchPage(string? url)
    {
        if (!IsYtDlpFirstPlatform(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();
        string pathAndQuery = uri.PathAndQuery.ToLowerInvariant();

        if (host.Contains("youtube.com", StringComparison.Ordinal))
        {
            return pathAndQuery.StartsWith("/watch", StringComparison.Ordinal) ||
                   pathAndQuery.StartsWith("/shorts/", StringComparison.Ordinal) ||
                   pathAndQuery.StartsWith("/embed/", StringComparison.Ordinal) ||
                   pathAndQuery.StartsWith("/live/", StringComparison.Ordinal);
        }

        if (host.Contains("tiktok.com", StringComparison.Ordinal))
        {
            return pathAndQuery.Contains("/video/", StringComparison.Ordinal) ||
                   pathAndQuery.Length > 3;
        }

        if (host.Contains("facebook.com", StringComparison.Ordinal))
        {
            return pathAndQuery.Contains("/watch", StringComparison.Ordinal) ||
                   pathAndQuery.Contains("/videos/", StringComparison.Ordinal) ||
                   pathAndQuery.Contains("/reel/", StringComparison.Ordinal) ||
                   pathAndQuery.Contains("/share/v/", StringComparison.Ordinal) ||
                   pathAndQuery.Contains("/share/r/", StringComparison.Ordinal);
        }

        return pathAndQuery.Length > 1;
    }

    /// <summary>
    /// Làm sạch URL xem video (loại bỏ query danh sách phát list, start_radio, index...)
    /// để yt-dlp và media player nạp trực tiếp video đơn lẻ nhanh gấp 10 lần mà không bị kẹt tải playlist.
    /// </summary>
    public static string SanitizeWatchUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url ?? string.Empty;
        }

        string host = uri.Host.ToLowerInvariant();
        if (host.Contains("youtube.com", StringComparison.Ordinal))
        {
            string q = uri.Query;
            if (!string.IsNullOrWhiteSpace(q))
            {
                var match = System.Text.RegularExpressions.Regex.Match(q, @"[?&]v=([a-zA-Z0-9_-]+)");
                if (match.Success)
                {
                    return $"https://www.youtube.com/watch?v={match.Groups[1].Value}";
                }
            }
        }
        else if (host.Contains("youtu.be", StringComparison.Ordinal))
        {
            string path = uri.AbsolutePath.Trim('/');
            if (!string.IsNullOrWhiteSpace(path))
            {
                var match = System.Text.RegularExpressions.Regex.Match(path, @"^([a-zA-Z0-9_-]+)");
                if (match.Success)
                {
                    return $"https://www.youtube.com/watch?v={match.Groups[1].Value}";
                }
            }
        }

        return url;
    }

    /// <summary>
    /// Nhận diện các request chunk lẻ (DASH range/segment của YouTube, Facebook, TikTok...)
    /// để KHÔNG bắt nhầm làm file video Direct (nguyên nhân gây ra file vài trăm KB không xem được).
    /// </summary>
    public static bool IsRawStreamingCdnChunk(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string host = uri.Host.ToLowerInvariant();
            foreach (string cdn in StreamingCdnChunkHosts)
            {
                if (host.Equals(cdn, StringComparison.Ordinal) || host.EndsWith("." + cdn, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            string path = uri.AbsolutePath.ToLowerInvariant();
            if (path.EndsWith(".ts", StringComparison.Ordinal) ||
                path.EndsWith(".m4s", StringComparison.Ordinal) ||
                path.EndsWith(".m4f", StringComparison.Ordinal) ||
                path.EndsWith(".cmfv", StringComparison.Ordinal) ||
                path.EndsWith(".cmfa", StringComparison.Ordinal) ||
                path.Contains("/segment", StringComparison.Ordinal) ||
                path.Contains("/chunk", StringComparison.Ordinal) ||
                path.Contains("/frag", StringComparison.Ordinal) ||
                path.EndsWith("/init.mp4", StringComparison.Ordinal))
            {
                return true;
            }

            string query = uri.Query.ToLowerInvariant();
            if (query.Contains("bytestart=", StringComparison.Ordinal) ||
                query.Contains("byteend=", StringComparison.Ordinal) ||
                query.Contains("&range=", StringComparison.Ordinal) ||
                query.Contains("?range=", StringComparison.Ordinal) ||
                query.Contains("&ump=1", StringComparison.Ordinal) ||
                query.Contains("&rbuf=", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Chuẩn hóa bộ chọn định dạng yt-dlp:
    /// - Với độ phân giải &gt; 1080p (1440p, 2160p 4K) hoặc Tốt nhất: lấy đúng luồng video độ phân giải cao nhất (VP9/AV1/H.264) + AAC để không bị tụt về 1080p.
    /// - Với &lt;= 1080p: ưu tiên H.264 (avc1) + AAC (mp4a) để tương thích tối đa mọi trình phát Windows.
    /// </summary>
    public static string BuildCompatibleMp4FormatSelector(int? maxHeight = null)
    {
        if (maxHeight.HasValue && maxHeight.Value > 0)
        {
            int h = maxHeight.Value;
            if (h > 1080)
            {
                return $"bestvideo[height<={h}]+bestaudio[acodec^=mp4a]/bestvideo[height<={h}]+bestaudio/best[height<={h}]";
            }
            return $"bestvideo[height<={h}][vcodec^=avc1]+bestaudio[acodec^=mp4a]/bestvideo[height<={h}]+bestaudio/best[height<={h}]";
        }

        return "bestvideo+bestaudio[acodec^=mp4a]/bestvideo+bestaudio/best";
    }

    public static bool TryParseFfmpegDuration(string? line, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = FfmpegDurationRegex().Match(line);
        return match.Success && TryParseHms(match, out duration);
    }

    public static bool TryParseFfmpegCurrentTime(string? line, out TimeSpan currentTime)
    {
        currentTime = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = FfmpegTimeRegex().Match(line);
        return match.Success && TryParseHms(match, out currentTime);
    }

    public static bool TryParseYtDlpProgress(string? line, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = YtDlpPercentRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            percent = Math.Clamp(val, 0.0, 100.0);
            return true;
        }

        return false;
    }

    public static IReadOnlyList<DetectedMedia> ParseYtDlpJsonFormats(
        string jsonOutput,
        string pageUrl,
        string? cookie = null,
        string? userAgent = null)
    {
        if (string.IsNullOrWhiteSpace(jsonOutput))
        {
            return Array.Empty<DetectedMedia>();
        }

        var results = new List<DetectedMedia>();
        try
        {
            string? jsonLine = jsonOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.TrimStart().StartsWith('{'));

            if (string.IsNullOrEmpty(jsonLine))
            {
                return Array.Empty<DetectedMedia>();
            }

            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            string title = root.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String
                ? tProp.GetString() ?? "Video"
                : "Video";

            double durationSec = root.TryGetProperty("duration", out var dProp) && dProp.ValueKind == JsonValueKind.Number && dProp.TryGetDouble(out double d)
                ? d
                : 0;

            var sizeByHeight = new Dictionary<int, long>();
            long bestAudioSize = 0;
            long approxBestSize = 0;

            if (root.TryGetProperty("formats", out var formatsProp) && formatsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var fmt in formatsProp.EnumerateArray())
                {
                    long size = 0;
                    if (fmt.TryGetProperty("filesize", out var fsProp) && fsProp.ValueKind == JsonValueKind.Number)
                    {
                        fsProp.TryGetInt64(out size);
                    }
                    else if (fmt.TryGetProperty("filesize_approx", out var fsaProp) && fsaProp.ValueKind == JsonValueKind.Number)
                    {
                        fsaProp.TryGetInt64(out size);
                    }
                    else if (durationSec > 0 && fmt.TryGetProperty("tbr", out var tbrProp) && tbrProp.ValueKind == JsonValueKind.Number && tbrProp.TryGetDouble(out double tbrKbps))
                    {
                        size = (long)(tbrKbps * 1000.0 / 8.0 * durationSec);
                    }

                    string vcodec = fmt.TryGetProperty("vcodec", out var vcProp) && vcProp.ValueKind == JsonValueKind.String
                        ? vcProp.GetString() ?? string.Empty
                        : string.Empty;
                    string acodec = fmt.TryGetProperty("acodec", out var acProp) && acProp.ValueKind == JsonValueKind.String
                        ? acProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (vcodec == "none" && acodec != "none" && size > bestAudioSize)
                    {
                        bestAudioSize = size;
                    }

                    if (fmt.TryGetProperty("height", out var hProp) && hProp.ValueKind == JsonValueKind.Number && hProp.TryGetInt32(out int h))
                    {
                        if (h >= 240)
                        {
                            int normalizedTier = NormalizeResolutionTier(h);
                            if (!sizeByHeight.TryGetValue(normalizedTier, out long prevSize) || size > prevSize)
                            {
                                sizeByHeight[normalizedTier] = size;
                            }
                        }
                    }

                    if (size > approxBestSize)
                    {
                        approxBestSize = size;
                    }
                }
            }

            int[] targetTiers = [2160, 1440, 1080, 720, 480, 360];
            bool addedVideoTier = false;

            foreach (int tier in targetTiers)
            {
                if (sizeByHeight.TryGetValue(tier, out long videoSize))
                {
                    addedVideoTier = true;
                    long totalEstimatedSize = videoSize > 0 ? videoSize + bestAudioSize : (tier >= 1080 ? approxBestSize : approxBestSize / 2);
                    results.Add(new DetectedMedia
                    {
                        Url = pageUrl,
                        Title = $"{title} ({tier}p MP4)",
                        QualityLabel = $"{tier}p",
                        FormatSelector = BuildCompatibleMp4FormatSelector(tier),
                        FileSize = totalEstimatedSize,
                        MimeType = "video/mp4",
                        Referer = pageUrl,
                        Cookie = cookie ?? string.Empty,
                        UserAgent = userAgent ?? string.Empty,
                        Type = MediaStreamType.YtDlp
                    });
                }
            }

            if (!addedVideoTier)
            {
                results.Add(new DetectedMedia
                {
                    Url = pageUrl,
                    Title = $"{title} (MP4 Chất lượng cao)",
                    QualityLabel = "HD",
                    FormatSelector = BuildCompatibleMp4FormatSelector(),
                    FileSize = approxBestSize + bestAudioSize,
                    MimeType = "video/mp4",
                    Referer = pageUrl,
                    Cookie = cookie ?? string.Empty,
                    UserAgent = userAgent ?? string.Empty,
                    Type = MediaStreamType.YtDlp
                });
            }

            results.Add(new DetectedMedia
            {
                Url = pageUrl,
                Title = $"{title} (Âm thanh M4A)",
                QualityLabel = "AUDIO",
                FormatSelector = "bestaudio[ext=m4a]/bestaudio",
                FileSize = bestAudioSize,
                MimeType = "audio/mp4",
                Referer = pageUrl,
                Cookie = cookie ?? string.Empty,
                UserAgent = userAgent ?? string.Empty,
                Type = MediaStreamType.YtDlp
            });
        }
        catch
        {
            // Bỏ qua nếu JSON không hợp lệ
        }

        return results;
    }

    private static int NormalizeResolutionTier(int height) => height switch
    {
        >= 1800 => 2160,
        >= 1200 => 1440,
        >= 900 => 1080,
        >= 600 => 720,
        >= 420 => 480,
        _ => 360
    };

    public static string InferSafeFileName(string? title, string url, MediaStreamType type, string? mimeType)
    {
        string ext = ".mp4";
        if (!string.IsNullOrEmpty(mimeType))
        {
            if (mimeType.Contains("audio/mpeg", StringComparison.OrdinalIgnoreCase)) ext = ".mp3";
            else if (mimeType.Contains("audio/", StringComparison.OrdinalIgnoreCase)) ext = ".m4a";
            else if (mimeType.Contains("webm", StringComparison.OrdinalIgnoreCase) && type == MediaStreamType.Direct) ext = ".webm";
            else if (mimeType.Contains("matroska", StringComparison.OrdinalIgnoreCase)) ext = ".mkv";
        }

        string baseName = title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseName) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string seg = Path.GetFileNameWithoutExtension(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(seg) && !seg.Equals("watch", StringComparison.OrdinalIgnoreCase) && !seg.Equals("videoplayback", StringComparison.OrdinalIgnoreCase))
            {
                baseName = seg;
            }
        }

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = $"video_{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(baseName.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (cleaned.Length > 110)
        {
            cleaned = cleaned[..110];
        }

        if (cleaned.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
        {
            return cleaned;
        }

        return cleaned + ext;
    }

    private static bool TryParseHms(Match match, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) ||
            !double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return false;
        }

        result = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }
}
