using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using QUANGLNBrowser.Core.Interfaces;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;
using QUANGLNBrowser.Network;

namespace QUANGLNBrowser.Media;

/// <summary>
/// Dịch vụ lưu trữ, chuẩn hóa danh sách luồng video/audio phát hiện được trên từng tab
/// và trích xuất định dạng nâng cao qua yt-dlp.exe gắn với JobObjectManager.
/// </summary>
public sealed class MediaSnifferService : IMediaSnifferService
{
    private readonly ConcurrentDictionary<Guid, List<DetectedMedia>> _tabMedia = new();
    private readonly JobObjectManager? _jobManager;

    public event EventHandler<Guid>? MediaDetected;

    public MediaSnifferService(JobObjectManager? jobManager = null)
    {
        _jobManager = jobManager;
    }

    public void CaptureMedia(Guid tabId, DetectedMedia media)
    {
        if (string.IsNullOrWhiteSpace(media.Url) ||
            media.Url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
            media.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var list = _tabMedia.GetOrAdd(tabId, _ => new List<DetectedMedia>());
        lock (list)
        {
            var existing = list.FirstOrDefault(m =>
                string.Equals(m.Url, media.Url, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.FormatSelector, media.FormatSelector, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                // Bổ sung metadata từ OnResourceResponse nếu trước đó chỉ bắt từ OnBeforeResourceLoad
                if (existing.FileSize <= 0 && media.FileSize > 0)
                {
                    existing.FileSize = media.FileSize;
                }
                if (string.IsNullOrEmpty(existing.MimeType) && !string.IsNullOrEmpty(media.MimeType))
                {
                    existing.MimeType = media.MimeType;
                }
                if (string.IsNullOrEmpty(existing.Cookie) && !string.IsNullOrEmpty(media.Cookie))
                {
                    existing.Cookie = media.Cookie;
                }
                if (string.IsNullOrEmpty(existing.Referer) && !string.IsNullOrEmpty(media.Referer))
                {
                    existing.Referer = media.Referer;
                }
                if (string.IsNullOrEmpty(existing.UserAgent) && !string.IsNullOrEmpty(media.UserAgent))
                {
                    existing.UserAgent = media.UserAgent;
                }
            }
            else
            {
                list.Add(media);
            }
        }

        MediaDetected?.Invoke(this, tabId);
    }

    public IReadOnlyList<DetectedMedia> GetDetectedMedia(Guid tabId)
    {
        if (!_tabMedia.TryGetValue(tabId, out var list))
        {
            return Array.Empty<DetectedMedia>();
        }
        lock (list)
        {
            return list.ToArray();
        }
    }

    public void ClearTab(Guid tabId)
    {
        _tabMedia.TryRemove(tabId, out _);
        MediaDetected?.Invoke(this, tabId);
    }

    public async Task<int> ProbePageWithYtDlpAsync(
        Guid tabId,
        string pageUrl,
        string? cookie = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pageUrl) ||
            !pageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        string ytDlpPath = AppPaths.GetNativeExePath("yt-dlp.exe");
        if (!File.Exists(ytDlpPath))
        {
            return 0;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("--dump-json");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            psi.ArgumentList.Add("--user-agent");
            psi.ArgumentList.Add(userAgent);
        }

        if (!string.IsNullOrWhiteSpace(cookie))
        {
            psi.ArgumentList.Add("--add-header");
            psi.ArgumentList.Add($"Cookie:{cookie}");
        }

        psi.ArgumentList.Add(pageUrl);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return 0;
            }

            _jobManager?.AssignProcess(proc);

            string jsonOutput = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            var formats = MediaStreamParser.ParseYtDlpJsonFormats(jsonOutput, pageUrl, cookie, userAgent);
            foreach (var item in formats)
            {
                CaptureMedia(tabId, item);
            }
            return formats.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Error($"yt-dlp probe failed for {pageUrl}", ex);
            return 0;
        }
    }
}
