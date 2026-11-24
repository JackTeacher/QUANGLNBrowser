using QUANGLNBrowser.Core.Models;

namespace QUANGLNBrowser.Core.Interfaces;

/// <summary>
/// Giao diện dịch vụ bắt luồng video/audio trên từng tab (MP4, HLS .m3u8, DASH .mpd và yt-dlp).
/// </summary>
public interface IMediaSnifferService
{
    event EventHandler<Guid>? MediaDetected;

    void CaptureMedia(Guid tabId, DetectedMedia media);
    IReadOnlyList<DetectedMedia> GetDetectedMedia(Guid tabId);
    void ClearTab(Guid tabId);
    Task<int> ProbePageWithYtDlpAsync(Guid tabId, string pageUrl, string? cookie = null, string? userAgent = null, CancellationToken cancellationToken = default);
}
