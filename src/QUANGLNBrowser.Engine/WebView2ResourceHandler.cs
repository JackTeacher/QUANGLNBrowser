using System.IO;
using Microsoft.Web.WebView2.Core;
using QUANGLNBrowser.Core.Interfaces;
using QUANGLNBrowser.Core.Models;

namespace QUANGLNBrowser.Engine;

/// <summary>
/// Gắn kết bộ lọc AdBlock và Media Sniffer vào CoreWebView2:
/// - Chặn các request quảng cáo qua WebResourceRequested (bảo vệ miễn trừ tài nguyên video playback).
/// - Phát hiện luồng phát media (.m3u8, .mpd, video/audio mime) qua WebResourceResponseReceived.
/// </summary>
public sealed class WebView2ResourceHandler
{
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

    private readonly IAdBlockEngine _adBlock;
    private readonly IMediaSnifferService _sniffer;
    private readonly Guid _tabId;
    private readonly CoreWebView2Environment _environment;

    public WebView2ResourceHandler(
        CoreWebView2Environment environment,
        IAdBlockEngine adBlock,
        IMediaSnifferService sniffer,
        Guid tabId)
    {
        _environment = environment;
        _adBlock = adBlock;
        _sniffer = sniffer;
        _tabId = tabId;
    }

    public void Attach(CoreWebView2 coreWebView2)
    {
        coreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        coreWebView2.WebResourceRequested += OnWebResourceRequested;
        coreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;
    }

    public void Detach(CoreWebView2 coreWebView2)
    {
        try
        {
            coreWebView2.WebResourceRequested -= OnWebResourceRequested;
            coreWebView2.WebResourceResponseReceived -= OnWebResourceResponseReceived;
        }
        catch
        {
            // Bỏ qua nếu coreWebView2 đã disposed
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string url = e.Request.Uri;
        string resourceType = MapContextToResourceType(e.ResourceContext);

        bool isVideoPlayback = IsVideoPlaybackResource(url, string.Empty, resourceType);

        if (!isVideoPlayback && _adBlock.ShouldBlock(url, string.Empty, resourceType))
        {
            bool isTracker = _adBlock.IsTrackerUrl(url);
            _adBlock.IncrementBlockedCount(_tabId, isTracker);
            // Tạo response rỗng 403 để chặn triệt để request quảng cáo
            e.Response = _environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked by QUANGLN AdBlock", "Content-Type: text/plain");
            return;
        }

        // Sniff luồng manifest HLS (.m3u8) / DASH (.mpd)
        if (IsHlsOrDashUrl(url))
        {
            string referer = e.Request.Headers.Contains("Referer") ? e.Request.Headers.GetHeader("Referer") : string.Empty;
            string ua = e.Request.Headers.Contains("User-Agent") ? e.Request.Headers.GetHeader("User-Agent") : string.Empty;
            string cookie = e.Request.Headers.Contains("Cookie") ? e.Request.Headers.GetHeader("Cookie") : string.Empty;

            _sniffer.CaptureMedia(_tabId, new DetectedMedia
            {
                Url = url,
                Referer = referer,
                UserAgent = ua,
                Cookie = cookie,
                Type = MediaStreamType.HlsOrDash
            });
        }
    }

    private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var response = e.Response;
            if (response is null) return;

            string mime = response.Headers.Contains("Content-Type") 
                ? response.Headers.GetHeader("Content-Type").ToLowerInvariant() 
                : string.Empty;

            if (mime.StartsWith("video/") || mime.StartsWith("audio/") ||
                mime.Contains("mpegurl") || mime.Contains("dash+xml"))
            {
                long size = 0;
                if (response.Headers.Contains("Content-Length"))
                {
                    long.TryParse(response.Headers.GetHeader("Content-Length"), out size);
                }

                bool isSegment = IsMediaSegmentOrCdnChunk(e.Request.Uri);
                bool isManifest = mime.Contains("mpegurl") || mime.Contains("dash+xml") || IsHlsOrDashUrl(e.Request.Uri);

                // Bỏ qua các segment nhỏ (.ts, .m4s) để không làm loãng danh sách luồng phát trực tiếp
                if (!isSegment && (size > 500_000 || isManifest))
                {
                    string referer = e.Request.Headers.Contains("Referer") ? e.Request.Headers.GetHeader("Referer") : string.Empty;
                    string ua = e.Request.Headers.Contains("User-Agent") ? e.Request.Headers.GetHeader("User-Agent") : string.Empty;
                    string cookie = e.Request.Headers.Contains("Cookie") ? e.Request.Headers.GetHeader("Cookie") : string.Empty;

                    _sniffer.CaptureMedia(_tabId, new DetectedMedia
                    {
                        Url = e.Request.Uri,
                        MimeType = mime,
                        FileSize = size,
                        Referer = referer,
                        UserAgent = ua,
                        Cookie = cookie,
                        Type = isManifest ? MediaStreamType.HlsOrDash : MediaStreamType.Direct
                    });
                }
            }
        }
        catch
        {
            // Bỏ qua lỗi đọc response headers
        }
    }

    public static bool IsVideoPlaybackResource(string url, string sourceUrl, string resourceType)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // 1. Luồng HTML5 Audio/Video và MediaSource Extensions
        if (string.Equals(resourceType, "media", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Định dạng HLS manifest (.m3u8), DASH manifest (.mpd)
        if (IsHlsOrDashUrl(url))
        {
            return true;
        }

        // 3. File video/audio direct (.mp4, .mkv, .webm, .m4a, .mp3)
        if (IsDirectMediaUrl(url))
        {
            return true;
        }

        // 4. Video chunk/segment (.ts, .m4s, /segment, /chunk, /frag, range requests)
        if (IsMediaSegmentOrCdnChunk(url))
        {
            return true;
        }

        // 5. Khóa giải mã DRM/EME & Stream Envelopes (.key, /key/, widevine, clearkey, license)
        if (url.Contains(".key", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/key/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("widevine", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("clearkey", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/license", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 6. Runtime engine của các trình phát web phổ biến (JWPlayer, Hls.js, Dash.js, Shaka, Video.js, Plyr)
        if (url.Contains("jwplayer", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("jwpcdn.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("hls.js", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("hls.min.js", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("dash.all", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("dash.min.js", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("shaka-player", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("videojs", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("video.min.js", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("plyr.min.js", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/embed.php", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 7. Embed assets & Anti-adblock probe check của các nguồn phim/video trực tuyến
        if (url.Contains("streamc.xyz", StringComparison.OrdinalIgnoreCase) ||
            sourceUrl.Contains("streamc.xyz", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("player-messages", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("guard-messages", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("cdn-start-messages", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool IsHlsOrDashUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        // Bỏ qua các segment nhỏ .ts, .m4s
        if (url.Contains(".ts?", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".m4s?", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int qIdx = url.IndexOf('?');
        ReadOnlySpan<char> path = qIdx >= 0 ? url.AsSpan(0, qIdx) : url.AsSpan();
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ||
               url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               url.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/hls/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDirectMediaUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        int qIdx = url.IndexOf('?');
        ReadOnlySpan<char> path = qIdx >= 0 ? url.AsSpan(0, qIdx) : url.AsSpan();
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMediaSegmentOrCdnChunk(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return true;
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

            string query = uri.Query.ToLowerInvariant();
            if (query.Contains("bytestart=", StringComparison.Ordinal) ||
                query.Contains("byteend=", StringComparison.Ordinal) ||
                query.Contains("&range=", StringComparison.Ordinal) ||
                query.Contains("?range=", StringComparison.Ordinal) ||
                query.Contains("&ump=1", StringComparison.Ordinal) ||
                query.Contains("&rbuf=", StringComparison.Ordinal) ||
                query.Contains("&sq=", StringComparison.Ordinal))
            {
                return true;
            }
        }

        int qIdx = url.IndexOf('?');
        ReadOnlySpan<char> path = qIdx >= 0 ? url.AsSpan(0, qIdx) : url.AsSpan();
        return path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".m4f", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".cmfv", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".cmfa", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("init.mp4", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/segment", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/chunk", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/frag", StringComparison.OrdinalIgnoreCase);
    }

    private static string MapContextToResourceType(CoreWebView2WebResourceContext context) =>
        context switch
        {
            CoreWebView2WebResourceContext.Document => "main_frame",
            CoreWebView2WebResourceContext.Stylesheet => "stylesheet",
            CoreWebView2WebResourceContext.Script => "script",
            CoreWebView2WebResourceContext.Image => "image",
            CoreWebView2WebResourceContext.Media => "media",
            CoreWebView2WebResourceContext.Font => "font",
            CoreWebView2WebResourceContext.XmlHttpRequest => "xhr",
            CoreWebView2WebResourceContext.Fetch => "fetch",
            CoreWebView2WebResourceContext.Websocket => "websocket",
            _ => "other"
        };
}
