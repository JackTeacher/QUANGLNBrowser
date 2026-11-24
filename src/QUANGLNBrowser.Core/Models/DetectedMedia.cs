namespace QUANGLNBrowser.Core.Models;

public enum MediaStreamType
{
    Direct,
    HlsOrDash,
    YtDlp
}

/// <summary>
/// Thông tin luồng video/audio bắt được từ trang web (kèm đầy đủ Referer, User-Agent, Cookie).
/// </summary>
public sealed class DetectedMedia
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Referer { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string Cookie { get; set; } = string.Empty;
    public string QualityLabel { get; set; } = string.Empty;
    public string FormatSelector { get; set; } = string.Empty;
    public string SubtitleUrl { get; set; } = string.Empty;
    public string? AudioUrl { get; set; }
    public MediaStreamType Type { get; set; } = MediaStreamType.Direct;
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    public string FormatBadge => Type switch
    {
        MediaStreamType.HlsOrDash => Url.Contains(".mpd", StringComparison.OrdinalIgnoreCase) || MimeType.Contains("dash", StringComparison.OrdinalIgnoreCase)
            ? "DASH"
            : "HLS",
        MediaStreamType.YtDlp => string.IsNullOrWhiteSpace(QualityLabel) ? "YT-DLP" : QualityLabel,
        _ => MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || Url.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || Url.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
            ? "AUDIO"
            : "MP4"
    };

    public string DisplaySize => FileSize switch
    {
        <= 0 => Type == MediaStreamType.HlsOrDash ? "Luồng trực tuyến" : "Tự động",
        < 1024 * 1024 => $"{FileSize / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{FileSize / (1024.0 * 1024.0):F1} MB",
        _ => $"{FileSize / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };

    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title))
            {
                return Title;
            }

            if (Uri.TryCreate(Url, UriKind.Absolute, out var uri))
            {
                string seg = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrWhiteSpace(seg) && seg != "/")
                {
                    return seg;
                }
                return uri.Host;
            }

            return "Video Stream";
        }
    }
}
