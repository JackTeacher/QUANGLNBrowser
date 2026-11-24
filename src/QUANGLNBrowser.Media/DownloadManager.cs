using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using QUANGLNBrowser.Core.Interfaces;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;
using QUANGLNBrowser.Network;

namespace QUANGLNBrowser.Media;

/// <summary>
/// Bộ tải Video/Media đa luồng 3 chế độ (kiểu Cốc Cốc Savior):
/// 1. Direct: 8 luồng HttpClient + RandomAccess.WriteAsync hỗ trợ Pause/Resume, tự nhận diện nếu URL là trang web/manifest để chuyển sang yt-dlp/FFmpeg.
/// 2. HlsDash (.m3u8 / .mpd): chạy ngầm native\ffmpeg.exe (-c copy -bsf:a aac_adtstoasc -movflags +faststart), tự fallback sang yt-dlp nếu manifest phức tạp.
/// 3. YtDlp: chạy ngầm native\yt-dlp.exe (--ffmpeg-location native\, ưu tiên H.264 avc1 + AAC mp4a) ghép ra file .mp4 hoàn chỉnh xem được trên mọi trình phát.
/// </summary>
public sealed class DownloadManager : IDownloadManager
{
    public const int DefaultThreadCount = 8;

    private enum DirectProbeOutcome
    {
        BinaryMedia,
        WebPageHtml,
        HlsOrDashManifest
    }

    private sealed class ChunkRange
    {
        public long Start { get; init; }
        public long End { get; init; }
        public long CurrentOffset;
    }

    private sealed class DownloadJobState
    {
        public required DownloadItem Item { get; init; }
        public required DetectedMedia Media { get; init; }
        public CancellationTokenSource? Cts { get; set; }
        public Process? ChildProcess { get; set; }
        public ChunkRange[]? Chunks { get; set; }
        public bool SupportsRange { get; set; }
        public double LastPercent { get; set; }
    }

    private readonly JobObjectManager _jobManager;
    private readonly IVaultService? _vaultService;
    private readonly HttpClient _httpClient;
    private string _defaultDownloadsDir;
    private readonly ConcurrentDictionary<string, DownloadJobState> _jobs = new();
    private bool _disposed;

    public event EventHandler<DownloadProgressEventArgs>? DownloadProgressChanged;

    public string DefaultDownloadDirectory
    {
        get => _defaultDownloadsDir;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _defaultDownloadsDir = value.Trim();
            }
        }
    }

    public DownloadManager(
        JobObjectManager jobManager,
        IVaultService? vaultService = null,
        string? defaultDownloadsDir = null,
        HttpClient? httpClient = null)
    {
        _jobManager = jobManager;
        _vaultService = vaultService;
        _defaultDownloadsDir = defaultDownloadsDir ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "QUANGLNBrowser");

        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
    }

    public IReadOnlyList<DownloadItem> GetActiveDownloads() =>
        _jobs.Values
            .Select(j => j.Item)
            .OrderByDescending(i => i.CreatedAt)
            .ToArray();

    public async Task<DownloadItem> EnqueueDownloadAsync(
        DetectedMedia media,
        string? customSavePath = null,
        string? profileId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(_defaultDownloadsDir);

        // Chuẩn hóa chế độ tải giống Cốc Cốc:
        // Nếu URL hoặc Referer thuộc YouTube/TikTok/Facebook/Vimeo... hoặc là chunk CDN lẻ, chuyển sang YtDlp với URL trang gốc
        NormalizeMediaForDownload(media);

        string fileName = MediaStreamParser.InferSafeFileName(media.Title, media.Url, media.Type, media.MimeType);
        string savePath = !string.IsNullOrWhiteSpace(customSavePath)
            ? customSavePath
            : GetUniqueFilePath(Path.Combine(_defaultDownloadsDir, fileName));

        var headersDict = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(media.Referer)) headersDict["Referer"] = media.Referer;
        if (!string.IsNullOrWhiteSpace(media.UserAgent)) headersDict["User-Agent"] = media.UserAgent;
        if (!string.IsNullOrWhiteSpace(media.Cookie)) headersDict["Cookie"] = media.Cookie;
        if (!string.IsNullOrWhiteSpace(media.FormatSelector)) headersDict["FormatSelector"] = media.FormatSelector;

        var item = new DownloadItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ProfileId = profileId ?? _vaultService?.CurrentProfile?.Id ?? "primary",
            Url = media.Url,
            FileName = Path.GetFileName(savePath),
            SavePath = savePath,
            TotalBytes = media.FileSize,
            DownloadedBytes = 0,
            Status = "Queued",
            MediaType = media.Type switch
            {
                MediaStreamType.HlsOrDash => "HlsDash",
                MediaStreamType.YtDlp => "YtDlp",
                _ => "Direct"
            },
            HeadersJson = JsonSerializer.Serialize(headersDict),
            CreatedAt = DateTime.UtcNow
        };

        var state = new DownloadJobState
        {
            Item = item,
            Media = media,
            Cts = new CancellationTokenSource()
        };

        _jobs[item.Id] = state;
        await SaveToVaultAsync(item);
        RaiseProgress(state, "Queued", 0);

        _ = RunDownloadJobAsync(state);
        return item;
    }

    private static void NormalizeMediaForDownload(DetectedMedia media)
    {
        if (media.Type == MediaStreamType.YtDlp)
        {
            if (string.IsNullOrWhiteSpace(media.FormatSelector))
            {
                media.FormatSelector = MediaStreamParser.BuildCompatibleMp4FormatSelector(1080);
            }
            return;
        }

        // Nếu bắt nhầm chunk CDN lẻ (googlevideo.com, fbcdn bytestart, tiktokcdn...) -> dùng trang Referer để tải qua yt-dlp
        if (MediaStreamParser.IsRawStreamingCdnChunk(media.Url))
        {
            if (!string.IsNullOrWhiteSpace(media.Referer) &&
                media.Referer.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !MediaStreamParser.IsRawStreamingCdnChunk(media.Referer))
            {
                media.Url = media.Referer;
            }
            media.Type = MediaStreamType.YtDlp;
            if (string.IsNullOrWhiteSpace(media.FormatSelector))
            {
                media.FormatSelector = MediaStreamParser.BuildCompatibleMp4FormatSelector(1080);
            }
            return;
        }

        // Nếu URL là trang YouTube/TikTok/Facebook/Vimeo... -> bắt buộc dùng yt-dlp
        if (MediaStreamParser.IsYtDlpFirstPlatform(media.Url))
        {
            media.Type = MediaStreamType.YtDlp;
            if (string.IsNullOrWhiteSpace(media.FormatSelector))
            {
                media.FormatSelector = MediaStreamParser.BuildCompatibleMp4FormatSelector(1080);
            }
        }
    }

    public async Task PauseDownloadAsync(string downloadId)
    {
        if (!_jobs.TryGetValue(downloadId, out var state))
        {
            return;
        }

        if (state.Item.Status is not ("Downloading" or "Queued" or "Remuxing"))
        {
            return;
        }

        state.Item.Status = "Paused";
        state.Cts?.Cancel();
        KillChildProcessIfRunning(state);

        await SaveToVaultAsync(state.Item);
        RaiseProgress(state, "Paused", state.LastPercent);
    }

    public async Task ResumeDownloadAsync(string downloadId)
    {
        if (!_jobs.TryGetValue(downloadId, out var state))
        {
            return;
        }

        if (state.Item.Status is not ("Paused" or "Failed"))
        {
            return;
        }

        state.Cts?.Dispose();
        state.Cts = new CancellationTokenSource();
        state.Item.Status = "Downloading";
        await SaveToVaultAsync(state.Item);
        RaiseProgress(state, "Downloading", state.LastPercent);

        _ = RunDownloadJobAsync(state);
    }

    public async Task CancelDownloadAsync(string downloadId)
    {
        if (!_jobs.TryRemove(downloadId, out var state))
        {
            return;
        }

        state.Item.Status = "Failed";
        state.Cts?.Cancel();
        state.Cts?.Dispose();
        KillChildProcessIfRunning(state);

        if (_vaultService is not null)
        {
            await _vaultService.DeleteDownloadAsync(downloadId);
        }

        RaiseProgress(state, "Failed", state.LastPercent, "Đã hủy tải xuống.");
    }

    private async Task RunDownloadJobAsync(DownloadJobState state)
    {
        var ct = state.Cts?.Token ?? CancellationToken.None;
        try
        {
            AppLogger.Info($"Starting download [{state.Item.MediaType}] id={state.Item.Id} url={state.Media.Url} -> {state.Item.SavePath}");

            switch (state.Media.Type)
            {
                case MediaStreamType.HlsOrDash:
                    await RunFfmpegRemuxJobAsync(state, ct);
                    break;
                case MediaStreamType.YtDlp:
                    await RunYtDlpJobAsync(state, ct);
                    break;
                default:
                    await RunDirectMultiThreadJobAsync(state, ct);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            if (state.Item.Status != "Paused")
            {
                state.Item.Status = "Paused";
                await SaveToVaultAsync(state.Item);
                RaiseProgress(state, "Paused", state.LastPercent);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Download job {state.Item.Id} ({state.Item.Url}) failed", ex);
            state.Item.Status = "Failed";
            await SaveToVaultAsync(state.Item);
            RaiseProgress(state, "Failed", state.LastPercent, ex.Message);
        }
    }

    private async Task RunDirectMultiThreadJobAsync(DownloadJobState state, CancellationToken ct)
    {
        state.Item.Status = "Downloading";
        await SaveToVaultAsync(state.Item);

        if (state.Chunks is null)
        {
            var (outcome, totalBytes, supportsRange) = await ProbeDirectResourceAsync(state.Media, ct);

            // Nếu probe phát hiện URL thực chất là trang HTML (ví dụ link bài viết/trang xem video) -> chuyển ngay sang yt-dlp
            if (outcome == DirectProbeOutcome.WebPageHtml)
            {
                AppLogger.Info($"Direct probe detected HTML page for {state.Media.Url}; rerouting to yt-dlp.");
                state.Media.Type = MediaStreamType.YtDlp;
                state.Item.MediaType = "YtDlp";
                if (string.IsNullOrWhiteSpace(state.Media.FormatSelector))
                {
                    state.Media.FormatSelector = MediaStreamParser.BuildCompatibleMp4FormatSelector(1080);
                }
                await RunYtDlpJobAsync(state, ct);
                return;
            }

            // Nếu probe phát hiện manifest HLS/DASH (#EXTM3U / .m3u8) -> chuyển ngay sang FFmpeg Remux
            if (outcome == DirectProbeOutcome.HlsOrDashManifest)
            {
                AppLogger.Info($"Direct probe detected HLS/DASH manifest for {state.Media.Url}; rerouting to FFmpeg.");
                state.Media.Type = MediaStreamType.HlsOrDash;
                state.Item.MediaType = "HlsDash";
                await RunFfmpegRemuxJobAsync(state, ct);
                return;
            }

            if (totalBytes > 0)
            {
                state.Item.TotalBytes = totalBytes;
            }
            state.SupportsRange = supportsRange && state.Item.TotalBytes > 64 * 1024;

            if (state.SupportsRange)
            {
                state.Chunks = BuildChunks(state.Item.TotalBytes, DefaultThreadCount);
            }
        }

        string? dir = Path.GetDirectoryName(state.Item.SavePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (state.SupportsRange && state.Chunks is not null)
        {
            using SafeFileHandle fileHandle = File.OpenHandle(
                state.Item.SavePath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite,
                FileOptions.Asynchronous);

            if (RandomAccess.GetLength(fileHandle) != state.Item.TotalBytes)
            {
                RandomAccess.SetLength(fileHandle, state.Item.TotalBytes);
            }

            long lastReportedTick = Environment.TickCount64;
            var chunkTasks = state.Chunks.Select(chunk => DownloadChunkAsync(state, fileHandle, chunk, () =>
            {
                long downloaded = state.Chunks.Sum(c => c.CurrentOffset - c.Start);
                state.Item.DownloadedBytes = downloaded;
                double pct = state.Item.TotalBytes > 0
                    ? Math.Clamp(downloaded * 100.0 / state.Item.TotalBytes, 0.0, 100.0)
                    : 0.0;
                state.LastPercent = pct;

                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref lastReportedTick) >= 120)
                {
                    Interlocked.Exchange(ref lastReportedTick, now);
                    RaiseProgress(state, "Downloading", pct);
                }
            }, ct));

            await Task.WhenAll(chunkTasks);
            state.Item.DownloadedBytes = state.Item.TotalBytes;
        }
        else
        {
            using var req = CreateHttpRequest(HttpMethod.Get, state.Media);
            if (state.Item.DownloadedBytes > 0 && File.Exists(state.Item.SavePath))
            {
                req.Headers.Range = new RangeHeaderValue(state.Item.DownloadedBytes, null);
            }

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            bool append = resp.StatusCode == HttpStatusCode.PartialContent && state.Item.DownloadedBytes > 0;
            if (!append)
            {
                state.Item.DownloadedBytes = 0;
            }

            if (resp.Content.Headers.ContentLength.HasValue && state.Item.TotalBytes <= 0)
            {
                state.Item.TotalBytes = resp.Content.Headers.ContentLength.Value + state.Item.DownloadedBytes;
            }

            await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(
                state.Item.SavePath,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.Read);

            byte[] buffer = new byte[65536];
            int read;
            long lastTick = Environment.TickCount64;
            while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                state.Item.DownloadedBytes += read;
                double pct = state.Item.TotalBytes > 0
                    ? Math.Clamp(state.Item.DownloadedBytes * 100.0 / state.Item.TotalBytes, 0.0, 100.0)
                    : 50.0;
                state.LastPercent = pct;

                long now = Environment.TickCount64;
                if (now - lastTick >= 120)
                {
                    lastTick = now;
                    RaiseProgress(state, "Downloading", pct);
                }
            }
        }

        // Kiểm tra tính toàn vẹn của file tải về: nếu là file text/HTML nhỏ (< 32 KB) thì fallback sang yt-dlp
        if (await IsDownloadedFileActuallyHtmlOrTinyManifestAsync(state.Item.SavePath, ct))
        {
            AppLogger.Info($"Downloaded file at {state.Item.SavePath} is HTML/manifest; falling back to yt-dlp.");
            TryDeleteFile(state.Item.SavePath);
            if (!string.IsNullOrWhiteSpace(state.Media.Referer) &&
                state.Media.Referer.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                state.Media.Url = state.Media.Referer;
            }
            state.Media.Type = MediaStreamType.YtDlp;
            state.Item.MediaType = "YtDlp";
            await RunYtDlpJobAsync(state, ct);
            return;
        }

        state.Item.Status = "Completed";
        state.LastPercent = 100.0;
        await SaveToVaultAsync(state.Item);
        RaiseProgress(state, "Completed", 100.0);
    }

    private async Task DownloadChunkAsync(
        DownloadJobState state,
        SafeFileHandle fileHandle,
        ChunkRange chunk,
        Action onBytesWritten,
        CancellationToken ct)
    {
        long current = Interlocked.Read(ref chunk.CurrentOffset);
        if (current > chunk.End)
        {
            return;
        }

        using var req = CreateHttpRequest(HttpMethod.Get, state.Media);
        req.Headers.Range = new RangeHeaderValue(current, chunk.End);

        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            long writeOffset = Interlocked.Read(ref chunk.CurrentOffset);
            long remaining = (chunk.End - writeOffset) + 1;
            int toWrite = (int)Math.Min(read, Math.Max(0, remaining));
            if (toWrite <= 0)
            {
                break;
            }

            await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, toWrite), writeOffset, ct);
            Interlocked.Add(ref chunk.CurrentOffset, toWrite);
            onBytesWritten();
        }
    }

    private async Task<(DirectProbeOutcome Outcome, long TotalBytes, bool SupportsRange)> ProbeDirectResourceAsync(
        DetectedMedia media,
        CancellationToken ct)
    {
        try
        {
            using var rangeReq = CreateHttpRequest(HttpMethod.Get, media);
            rangeReq.Headers.Range = new RangeHeaderValue(0, 1023);
            using var resp = await _httpClient.SendAsync(rangeReq, HttpCompletionOption.ResponseHeadersRead, ct);

            string contentType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
            if (contentType.Contains("text/html", StringComparison.Ordinal) ||
                contentType.Contains("application/xhtml", StringComparison.Ordinal))
            {
                return (DirectProbeOutcome.WebPageHtml, 0, false);
            }

            if (contentType.Contains("mpegurl", StringComparison.Ordinal) ||
                contentType.Contains("dash+xml", StringComparison.Ordinal))
            {
                return (DirectProbeOutcome.HlsOrDashManifest, 0, false);
            }

            // Đọc thử tối đa 256 byte đầu để kiểm tra chữ ký #EXTM3U hoặc <!DOCTYPE html
            await using var probeStream = await resp.Content.ReadAsStreamAsync(ct);
            byte[] headerBytes = new byte[256];
            int read = await probeStream.ReadAsync(headerBytes.AsMemory(0, headerBytes.Length), ct);
            if (read > 0)
            {
                string prefix = Encoding.ASCII.GetString(headerBytes, 0, Math.Min(read, 64)).TrimStart();
                if (prefix.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    return (DirectProbeOutcome.HlsOrDashManifest, 0, false);
                }
                if (prefix.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                    prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                {
                    return (DirectProbeOutcome.WebPageHtml, 0, false);
                }
            }

            if (resp.StatusCode == HttpStatusCode.PartialContent)
            {
                // QUAN TRỌNG: Lấy tổng dung lượng thực của file từ Content-Range (bytes 0-1023/TOTAL),
                // tuyệt đối KHÔNG lấy Content-Length của gói probe 1024 bytes!
                long total = resp.Content.Headers.ContentRange?.Length ?? 0;
                return (DirectProbeOutcome.BinaryMedia, total, total > 0);
            }

            if (resp.IsSuccessStatusCode)
            {
                bool accepts = resp.Headers.AcceptRanges.Contains("bytes");
                long total = resp.Content.Headers.ContentLength ?? media.FileSize;
                return (DirectProbeOutcome.BinaryMedia, total, accepts && total > 0);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Error($"Range probe failed for {media.Url}", ex);
        }

        return (DirectProbeOutcome.BinaryMedia, media.FileSize, false);
    }

    private static async Task<bool> IsDownloadedFileActuallyHtmlOrTinyManifestAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        var info = new FileInfo(filePath);
        if (info.Length == 0)
        {
            return true;
        }

        if (info.Length > 256 * 1024)
        {
            return false;
        }

        byte[] buf = new byte[(int)Math.Min(128, info.Length)];
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int read = await fs.ReadAsync(buf, ct);
        if (read <= 0)
        {
            return true;
        }

        string prefix = Encoding.ASCII.GetString(buf, 0, read).TrimStart();
        return prefix.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase);
    }

    private static ChunkRange[] BuildChunks(long totalBytes, int threadCount)
    {
        int count = (int)Math.Min(threadCount, Math.Max(1, totalBytes / (64 * 1024)));
        var chunks = new ChunkRange[count];
        long chunkSize = totalBytes / count;

        for (int i = 0; i < count; i++)
        {
            long start = i * chunkSize;
            long end = (i == count - 1) ? (totalBytes - 1) : (start + chunkSize - 1);
            chunks[i] = new ChunkRange
            {
                Start = start,
                End = end,
                CurrentOffset = start
            };
        }

        return chunks;
    }

    private static HttpRequestMessage CreateHttpRequest(HttpMethod method, DetectedMedia media)
    {
        var req = new HttpRequestMessage(method, media.Url);
        if (!string.IsNullOrWhiteSpace(media.Referer) && Uri.TryCreate(media.Referer, UriKind.Absolute, out var refUri))
        {
            req.Headers.Referrer = refUri;
        }
        if (!string.IsNullOrWhiteSpace(media.UserAgent))
        {
            req.Headers.TryAddWithoutValidation("User-Agent", media.UserAgent);
        }
        if (!string.IsNullOrWhiteSpace(media.Cookie))
        {
            req.Headers.TryAddWithoutValidation("Cookie", media.Cookie);
        }
        return req;
    }

    private async Task RunFfmpegRemuxJobAsync(DownloadJobState state, CancellationToken ct)
    {
        string ffmpegPath = AppPaths.GetNativeExePath("ffmpeg.exe");
        if (!File.Exists(ffmpegPath))
        {
            throw new FileNotFoundException("Không tìm thấy native\\ffmpeg.exe.", ffmpegPath);
        }

        state.Item.Status = "Remuxing";
        await SaveToVaultAsync(state.Item);
        RaiseProgress(state, "Remuxing", state.LastPercent);

        var headerBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(state.Media.Referer))
        {
            headerBuilder.Append($"Referer: {state.Media.Referer}\r\n");
        }
        if (!string.IsNullOrWhiteSpace(state.Media.UserAgent))
        {
            headerBuilder.Append($"User-Agent: {state.Media.UserAgent}\r\n");
        }
        if (!string.IsNullOrWhiteSpace(state.Media.Cookie))
        {
            headerBuilder.Append($"Cookie: {state.Media.Cookie}\r\n");
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add("-y");
        if (headerBuilder.Length > 0)
        {
            psi.ArgumentList.Add("-headers");
            psi.ArgumentList.Add(headerBuilder.ToString());
        }
        psi.ArgumentList.Add("-reconnect");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_streamed");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_delay_max");
        psi.ArgumentList.Add("5");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(state.Media.Url);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-bsf:a");
        psi.ArgumentList.Add("aac_adtstoasc");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(state.Item.SavePath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Không thể khởi chạy tiến trình ffmpeg.exe.");

        _jobManager.AssignProcess(proc);
        state.ChildProcess = proc;

        using var reg = ct.Register(() => KillChildProcessIfRunning(state));

        TimeSpan totalDuration = TimeSpan.Zero;
        while (!proc.StandardError.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await proc.StandardError.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (totalDuration == TimeSpan.Zero && MediaStreamParser.TryParseFfmpegDuration(line, out var dur))
            {
                totalDuration = dur;
            }
            else if (MediaStreamParser.TryParseFfmpegCurrentTime(line, out var current))
            {
                double pct = totalDuration.TotalSeconds > 0
                    ? Math.Clamp(current.TotalSeconds * 100.0 / totalDuration.TotalSeconds, 0.0, 99.5)
                    : Math.Min(95.0, state.LastPercent + 1.0);

                state.LastPercent = pct;
                if (File.Exists(state.Item.SavePath))
                {
                    state.Item.DownloadedBytes = new FileInfo(state.Item.SavePath).Length;
                }
                RaiseProgress(state, "Remuxing", pct);
            }
        }

        await proc.WaitForExitAsync(ct);
        state.ChildProcess = null;

        long outputLength = File.Exists(state.Item.SavePath) ? new FileInfo(state.Item.SavePath).Length : 0;

        // Nếu FFmpeg thất bại hoặc file xuất ra quá nhỏ (< 16 KB, ví dụ HLS master playlist mã hóa phức tạp),
        // tự động chuyển sang yt-dlp để tải và ghép luồng HLS/DASH
        if (proc.ExitCode != 0 || outputLength < 16 * 1024)
        {
            AppLogger.Info($"FFmpeg exit={proc.ExitCode}, size={outputLength}B for {state.Media.Url}; falling back to yt-dlp.");
            TryDeleteFile(state.Item.SavePath);
            await RunYtDlpJobAsync(state, ct);
            return;
        }

        state.Item.DownloadedBytes = outputLength;
        state.Item.TotalBytes = outputLength;
        state.Item.Status = "Completed";
        state.LastPercent = 100.0;
        await SaveToVaultAsync(state.Item);
        RaiseProgress(state, "Completed", 100.0);
    }

    private async Task RunYtDlpJobAsync(DownloadJobState state, CancellationToken ct)
    {
        string ytDlpPath = AppPaths.GetNativeExePath("yt-dlp.exe");
        if (!File.Exists(ytDlpPath))
        {
            throw new FileNotFoundException("Không tìm thấy native\\yt-dlp.exe.", ytDlpPath);
        }

        string nativeDir = Path.GetDirectoryName(ytDlpPath)!;
        state.Item.Status = "Downloading";
        await SaveToVaultAsync(state.Item);
        RaiseProgress(state, "Downloading", state.LastPercent);

        // Xóa file rỗng hoặc file lỗi trước đó (nếu có) để yt-dlp không bỏ qua
        TryDeleteFile(state.Item.SavePath);

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("--newline");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-part");
        psi.ArgumentList.Add("--force-overwrites");
        psi.ArgumentList.Add("--ffmpeg-location");
        psi.ArgumentList.Add(nativeDir);

        string formatSelector = !string.IsNullOrWhiteSpace(state.Media.FormatSelector)
            ? state.Media.FormatSelector
            : MediaStreamParser.BuildCompatibleMp4FormatSelector(1080);

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(formatSelector);

        if (!formatSelector.StartsWith("bestaudio", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--merge-output-format");
            psi.ArgumentList.Add("mp4");
            psi.ArgumentList.Add("--remux-video");
            psi.ArgumentList.Add("mp4");
        }

        if (!string.IsNullOrWhiteSpace(state.Media.UserAgent))
        {
            psi.ArgumentList.Add("--user-agent");
            psi.ArgumentList.Add(state.Media.UserAgent);
        }
        if (!string.IsNullOrWhiteSpace(state.Media.Referer))
        {
            psi.ArgumentList.Add("--referer");
            psi.ArgumentList.Add(state.Media.Referer);
        }
        if (!string.IsNullOrWhiteSpace(state.Media.Cookie))
        {
            psi.ArgumentList.Add("--add-header");
            psi.ArgumentList.Add($"Cookie:{state.Media.Cookie}");
        }

        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(state.Item.SavePath);
        psi.ArgumentList.Add(state.Media.Url);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Không thể khởi chạy tiến trình yt-dlp.exe.");

        _jobManager.AssignProcess(proc);
        state.ChildProcess = proc;

        using var reg = ct.Register(() => KillChildProcessIfRunning(state));

        // Đọc stderr song song để không bao giờ bị nghẽn buffer và lưu lại thông báo lỗi nếu có
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);

        while (!proc.StandardOutput.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await proc.StandardOutput.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (MediaStreamParser.TryParseYtDlpProgress(line, out double pct))
            {
                state.LastPercent = pct;
                if (File.Exists(state.Item.SavePath))
                {
                    state.Item.DownloadedBytes = new FileInfo(state.Item.SavePath).Length;
                }
                RaiseProgress(state, "Downloading", pct);
            }
            else if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("[VideoRemuxer]", StringComparison.OrdinalIgnoreCase))
            {
                state.Item.Status = "Remuxing";
                RaiseProgress(state, "Remuxing", 99.0);
            }
        }

        string stderrText = await stderrTask;
        await proc.WaitForExitAsync(ct);
        state.ChildProcess = null;

        if (proc.ExitCode != 0)
        {
            string errDetail = string.IsNullOrWhiteSpace(stderrText)
                ? $"mã lỗi {proc.ExitCode}"
                : stderrText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? $"mã lỗi {proc.ExitCode}";
            throw new InvalidOperationException($"yt-dlp không thể tải video ({errDetail}).");
        }

        // Xác nhận file đích (trong trường hợp yt-dlp tự gắn thêm đuôi .mp4 hoặc .m4a)
        string actualPath = ResolveActualYtDlpOutputFile(state.Item.SavePath);
        if (!File.Exists(actualPath))
        {
            throw new InvalidOperationException("yt-dlp đã chạy xong nhưng không tạo được file đầu ra.");
        }

        long finalLen = new FileInfo(actualPath).Length;
        if (finalLen < 10 * 1024)
        {
            throw new InvalidOperationException($"File tải về quá nhỏ ({finalLen} bytes), luồng video không hợp lệ.");
        }

        state.Item.SavePath = actualPath;
        state.Item.FileName = Path.GetFileName(actualPath);
        state.Item.DownloadedBytes = finalLen;
        state.Item.TotalBytes = finalLen;
        state.Item.Status = "Completed";
        state.LastPercent = 100.0;
        await SaveToVaultAsync(state.Item);
        RaiseProgress(state, "Completed", 100.0);
    }

    private static string ResolveActualYtDlpOutputFile(string expectedPath)
    {
        if (File.Exists(expectedPath))
        {
            return expectedPath;
        }

        string[] candidateSuffixes = [".mp4", ".mkv", ".webm", ".m4a", ".mp3"];
        foreach (string suffix in candidateSuffixes)
        {
            if (File.Exists(expectedPath + suffix))
            {
                return expectedPath + suffix;
            }

            string changed = Path.ChangeExtension(expectedPath, suffix);
            if (File.Exists(changed))
            {
                return changed;
            }
        }

        return expectedPath;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Bỏ qua nếu file chưa tồn tại hoặc đang khóa
        }
    }

    private static void KillChildProcessIfRunning(DownloadJobState state)
    {
        var proc = state.ChildProcess;
        if (proc is null)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Bỏ qua nếu process đã thoát
        }
    }

    private static string GetUniqueFilePath(string desiredPath)
    {
        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        string dir = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(desiredPath);
        string ext = Path.GetExtension(desiredPath);

        for (int i = 1; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    private async Task SaveToVaultAsync(DownloadItem item)
    {
        if (_vaultService is not null && _vaultService.IsUnlocked)
        {
            await _vaultService.UpsertDownloadAsync(item);
        }
    }

    private void RaiseProgress(DownloadJobState state, string status, double percent, string? error = null)
    {
        DownloadProgressChanged?.Invoke(
            this,
            new DownloadProgressEventArgs(
                state.Item.Id,
                status,
                state.Item.DownloadedBytes,
                state.Item.TotalBytes,
                percent,
                error));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (var state in _jobs.Values)
        {
            state.Cts?.Cancel();
            state.Cts?.Dispose();
            KillChildProcessIfRunning(state);
        }
        _jobs.Clear();
    }
}
