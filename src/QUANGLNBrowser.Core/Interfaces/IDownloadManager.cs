using QUANGLNBrowser.Core.Models;

namespace QUANGLNBrowser.Core.Interfaces;

public sealed record DownloadProgressEventArgs(
    string DownloadId,
    string Status,
    long DownloadedBytes,
    long TotalBytes,
    double ProgressPercent,
    string? ErrorMessage = null);

/// <summary>
/// Giao diện bộ tải đa luồng 3 chế độ (Direct 8 luồng RandomAccess, FFmpeg HLS/DASH remux, yt-dlp extractor).
/// </summary>
public interface IDownloadManager : IDisposable
{
    event EventHandler<DownloadProgressEventArgs>? DownloadProgressChanged;

    string DefaultDownloadDirectory { get; set; }

    Task<DownloadItem> EnqueueDownloadAsync(DetectedMedia media, string? customSavePath = null, string? profileId = null);
    Task PauseDownloadAsync(string downloadId);
    Task ResumeDownloadAsync(string downloadId);
    Task CancelDownloadAsync(string downloadId);
    IReadOnlyList<DownloadItem> GetActiveDownloads();
}
