using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;
using QUANGLNBrowser.Network;

namespace QUANGLNBrowser.Media;

/// <summary>
/// Kết quả phân giải luồng phát trực tiếp từ yt-dlp.exe cho QUANGLN Player.
/// Ưu tiên WebM (VP9/VP8 + Opus) tương thích 100% với nhân Chromium CefSharp bản mở nguồn.
/// </summary>
public sealed class ResolvedPlaybackStream
{
    public bool Success { get; init; }
    public string StreamUrl { get; init; } = string.Empty;
    public string? AudioUrl { get; init; }
    public string Title { get; init; } = string.Empty;
    public string QualityLabel { get; init; } = "HD";
    public double DurationSeconds { get; init; }
    public string? VietnameseSubtitleUrl { get; init; }
    public string? OriginalSubtitleUrl { get; init; }
    public string? OriginalSubtitleLang { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<DetectedMedia> DownloadFormats { get; init; } = Array.Empty<DetectedMedia>();
}

/// <summary>
/// Kết quả trích xuất phụ đề thực tế từ yt-dlp.exe (.vtt / .srt).
/// </summary>
public sealed class ExtractedSubtitlesResult
{
    public List<SubtitleSegment> VietnameseSegments { get; init; } = new();
    public List<SubtitleSegment> OriginalSegments { get; init; } = new();
    public string? DetectedOriginalLang { get; init; }
}

/// <summary>
/// Phân giải đường dẫn YouTube, TikTok, Facebook, Bilibili... thành luồng Video + Audio HTTPS trực tiếp
/// (ưu tiên WebM VP9 + Opus phát mượt trong HTML5 &lt;video&gt; + &lt;audio&gt; của CefSharp) và trích xuất phụ đề thật qua yt-dlp.exe.
/// </summary>
public static class StreamPlaybackResolver
{
    public static async Task<ResolvedPlaybackStream> ResolveAsync(
        string pageUrl,
        JobObjectManager? jobManager = null,
        string? cookie = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pageUrl))
        {
            return new ResolvedPlaybackStream { Success = false, ErrorMessage = "URL trống." };
        }

        pageUrl = MediaStreamParser.SanitizeWatchUrl(pageUrl);

        string ytDlpPath = AppPaths.GetNativeExePath("yt-dlp.exe");
        if (!File.Exists(ytDlpPath))
        {
            return new ResolvedPlaybackStream { Success = false, ErrorMessage = "Không tìm thấy yt-dlp.exe trong thư mục native." };
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add("--skip-download");
            psi.ArgumentList.Add("-J");

            if (!string.IsNullOrWhiteSpace(userAgent))
            {
                psi.ArgumentList.Add("--user-agent");
                psi.ArgumentList.Add(userAgent);
            }

            string? cleanCookie = !string.IsNullOrWhiteSpace(cookie)
                ? cookie.Replace("\r", " ").Replace("\n", " ").Trim()
                : null;

            if (!string.IsNullOrWhiteSpace(cleanCookie))
            {
                psi.ArgumentList.Add("--add-header");
                psi.ArgumentList.Add($"Cookie: {cleanCookie}");
            }

            psi.ArgumentList.Add(pageUrl);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new ResolvedPlaybackStream { Success = false, ErrorMessage = "Không thể khởi chạy yt-dlp.exe." };
            }

            jobManager?.AssignProcess(proc);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new ResolvedPlaybackStream { Success = false, ErrorMessage = "Hết thời gian phân tích luồng video." };
            }

            string json = await stdoutTask;
            string err = await stderrTask;

            if (string.IsNullOrWhiteSpace(json))
            {
                return new ResolvedPlaybackStream
                {
                    Success = false,
                    ErrorMessage = string.IsNullOrWhiteSpace(err) ? "yt-dlp không trả về dữ liệu luồng." : err.Trim()
                };
            }

            var streamResult = ParseYtDlpSingleVideoJson(json);
            var downloadFormats = MediaStreamParser.ParseYtDlpJsonFormats(json, pageUrl, cleanCookie, userAgent);
            return new ResolvedPlaybackStream
            {
                Success = streamResult.Success,
                StreamUrl = streamResult.StreamUrl,
                AudioUrl = streamResult.AudioUrl,
                Title = streamResult.Title,
                QualityLabel = streamResult.QualityLabel,
                DurationSeconds = streamResult.DurationSeconds,
                VietnameseSubtitleUrl = streamResult.VietnameseSubtitleUrl,
                OriginalSubtitleUrl = streamResult.OriginalSubtitleUrl,
                OriginalSubtitleLang = streamResult.OriginalSubtitleLang,
                ErrorMessage = streamResult.ErrorMessage,
                DownloadFormats = downloadFormats
            };
        }
        catch (Exception ex)
        {
            AppLogger.Error("StreamPlaybackResolver.ResolveAsync failed", ex);
            return new ResolvedPlaybackStream { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Tải phụ đề thật (ưu tiên tiếng Việt, sau đó Anh/Hàn/Nhật/Trung) trực tiếp bằng yt-dlp.exe.
    /// </summary>
    public static async Task<ExtractedSubtitlesResult> DownloadSubtitlesViaYtDlpAsync(
        string pageUrl,
        JobObjectManager? jobManager = null,
        string? cookie = null,
        string? userAgent = null,
        string? preferredSourceLang = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ExtractedSubtitlesResult();
        if (string.IsNullOrWhiteSpace(pageUrl))
        {
            return result;
        }

        pageUrl = MediaStreamParser.SanitizeWatchUrl(pageUrl);

        string ytDlpPath = AppPaths.GetNativeExePath("yt-dlp.exe");
        if (!File.Exists(ytDlpPath))
        {
            return result;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"quangln_subs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string subLangList = string.IsNullOrWhiteSpace(preferredSourceLang) ||
                             preferredSourceLang.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "vi,en,ko,ja,zh-Hans,zh-Hant,zh"
            : $"vi,{preferredSourceLang.Trim()},en,ko,ja";

        try
        {
            // Bước 1: Ưu tiên phụ đề chính thức (manual subtitles)
            await RunYtDlpSubtitleFetchAsync(
                ytDlpPath,
                pageUrl,
                tempDir,
                writeAutoSubs: false,
                subLangs: subLangList,
                jobManager,
                cookie,
                userAgent,
                cancellationToken);

            // Bước 2: Nếu chưa có phụ đề chính thức, tải phụ đề tự động (auto-captions)
            if (Directory.GetFiles(tempDir, "*.vtt").Length == 0)
            {
                await RunYtDlpSubtitleFetchAsync(
                    ytDlpPath,
                    pageUrl,
                    tempDir,
                    writeAutoSubs: true,
                    subLangs: subLangList,
                    jobManager,
                    cookie,
                    userAgent,
                    cancellationToken);
            }

            var vttFiles = Directory.GetFiles(tempDir, "*.vtt");
            string? viFile = vttFiles.FirstOrDefault(f => IsLangFile(f, "vi"));
            string? origFile = (!string.IsNullOrWhiteSpace(preferredSourceLang) &&
                                !preferredSourceLang.Equals("auto", StringComparison.OrdinalIgnoreCase)
                                    ? vttFiles.FirstOrDefault(f => IsLangFile(f, preferredSourceLang))
                                    : null)
                               ?? vttFiles.FirstOrDefault(f => IsLangFile(f, "en"))
                               ?? vttFiles.FirstOrDefault(f => IsLangFile(f, "ko"))
                               ?? vttFiles.FirstOrDefault(f => IsLangFile(f, "ja"))
                               ?? vttFiles.FirstOrDefault(f => !IsLangFile(f, "vi"));

            if (viFile is not null)
            {
                string rawVi = await File.ReadAllTextAsync(viFile, Encoding.UTF8, cancellationToken);
                result.VietnameseSegments.AddRange(SubtitleTranscriptService.ParseSubtitleContent(rawVi));
            }

            if (origFile is not null)
            {
                string rawOrig = await File.ReadAllTextAsync(origFile, Encoding.UTF8, cancellationToken);
                result.OriginalSegments.AddRange(SubtitleTranscriptService.ParseSubtitleContent(rawOrig));
            }

            return new ExtractedSubtitlesResult
            {
                VietnameseSegments = result.VietnameseSegments,
                OriginalSegments = result.OriginalSegments,
                DetectedOriginalLang = origFile is not null ? ExtractLangFromVttName(origFile) : null
            };
        }
        catch (Exception ex)
        {
            AppLogger.Error("DownloadSubtitlesViaYtDlpAsync failed", ex);
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Bỏ qua nếu hệ thống đang giải phóng tệp tạm
            }
        }
    }

    private static async Task RunYtDlpSubtitleFetchAsync(
        string ytDlpPath,
        string pageUrl,
        string tempDir,
        bool writeAutoSubs,
        string subLangs,
        JobObjectManager? jobManager,
        string? cookie,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--skip-download");
        psi.ArgumentList.Add(writeAutoSubs ? "--write-auto-subs" : "--write-subs");
        psi.ArgumentList.Add("--sub-langs");
        psi.ArgumentList.Add(subLangs);
        psi.ArgumentList.Add("--sub-format");
        psi.ArgumentList.Add("vtt");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(Path.Combine(tempDir, "sub.%(ext)s"));

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            psi.ArgumentList.Add("--user-agent");
            psi.ArgumentList.Add(userAgent);
        }

        if (!string.IsNullOrWhiteSpace(cookie))
        {
            string cleanCookie = cookie.Replace("\r", " ").Replace("\n", " ").Trim();
            if (!string.IsNullOrWhiteSpace(cleanCookie))
            {
                psi.ArgumentList.Add("--add-header");
                psi.ArgumentList.Add($"Cookie: {cleanCookie}");
            }
        }

        psi.ArgumentList.Add(pageUrl);

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return;
        }

        jobManager?.AssignProcess(proc);

        _ = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = proc.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(18));

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static bool IsLangFile(string filePath, string langPrefix)
    {
        string name = Path.GetFileName(filePath);
        return name.Contains($".{langPrefix}.", StringComparison.OrdinalIgnoreCase) ||
               name.Contains($".{langPrefix}-", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractLangFromVttName(string filePath)
    {
        var parts = Path.GetFileNameWithoutExtension(filePath).Split('.');
        return parts.Length >= 2 ? parts[^1] : null;
    }

    public static ResolvedPlaybackStream ParseYtDlpSingleVideoJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ResolvedPlaybackStream { Success = false, ErrorMessage = "JSON trả về không hợp lệ." };
        }

        string title = TryReadString(root, "title") ?? "QUANGLN Stream";
        double duration = TryReadDouble(root, "duration");

        // Ưu tiên 1: WebM (VP9/VP8 + Opus/Vorbis) vì CefSharp mã nguồn mở giải mã 100% mượt mà không bị DEMUXER_ERROR
        string? bestWebmVideoUrl = null;
        int bestWebmVideoHeight = -1;

        string? bestWebmAudioUrl = null;
        double bestWebmAudioAbr = -1;

        // Ưu tiên 2: Progressive gộp sẵn (WebM ưu tiên hơn MP4)
        string? progressiveWebmUrl = null;
        int progressiveWebmHeight = -1;

        string? progressiveMp4Url = null;
        int progressiveMp4Height = -1;

        // Ưu tiên 3: MP4/M4A DASH dự phòng nếu trang web không cung cấp WebM
        string? bestMp4VideoUrl = null;
        int bestMp4VideoHeight = -1;
        string? bestM4aAudioUrl = null;
        double bestM4aAudioAbr = -1;

        if (root.TryGetProperty("formats", out var formatsProp) &&
            formatsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var fmt in formatsProp.EnumerateArray())
            {
                if (fmt.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? fmtUrl = TryReadString(fmt, "url");
                if (string.IsNullOrWhiteSpace(fmtUrl) || !fmtUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string protocol = TryReadString(fmt, "protocol") ?? "https";
                if (protocol.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ||
                    protocol.Contains("mhtml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string vcodec = TryReadString(fmt, "vcodec") ?? "none";
                string acodec = TryReadString(fmt, "acodec") ?? "none";
                string ext = TryReadString(fmt, "ext") ?? string.Empty;
                int height = TryReadInt(fmt, "height");
                double abr = TryReadDouble(fmt, "abr");

                bool hasVideo = !string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase);
                bool hasAudio = !string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase);

                bool isOpenVideoCodec =
                    ext.Equals("webm", StringComparison.OrdinalIgnoreCase) ||
                    vcodec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase) ||
                    vcodec.StartsWith("vp09", StringComparison.OrdinalIgnoreCase) ||
                    vcodec.StartsWith("vp8", StringComparison.OrdinalIgnoreCase) ||
                    vcodec.StartsWith("av01", StringComparison.OrdinalIgnoreCase);

                bool isOpenAudioCodec =
                    ext.Equals("webm", StringComparison.OrdinalIgnoreCase) ||
                    acodec.Contains("opus", StringComparison.OrdinalIgnoreCase) ||
                    acodec.Contains("vorbis", StringComparison.OrdinalIgnoreCase);

                if (hasVideo && hasAudio)
                {
                    if (isOpenVideoCodec && isOpenAudioCodec)
                    {
                        if (height > progressiveWebmHeight)
                        {
                            progressiveWebmHeight = height;
                            progressiveWebmUrl = fmtUrl;
                        }
                    }
                    else if (ext.Equals("mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        if (height > progressiveMp4Height)
                        {
                            progressiveMp4Height = height;
                            progressiveMp4Url = fmtUrl;
                        }
                    }
                }
                else if (hasVideo && !hasAudio && height > 0 && height <= 1080)
                {
                    if (isOpenVideoCodec)
                    {
                        // Ưu tiên VP9 (nhẹ CPU/GPU) hơn AV1 cùng độ phân giải
                        bool isVp9 = vcodec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase) ||
                                     vcodec.StartsWith("vp09", StringComparison.OrdinalIgnoreCase);
                        if (height > bestWebmVideoHeight || (height == bestWebmVideoHeight && isVp9))
                        {
                            bestWebmVideoHeight = height;
                            bestWebmVideoUrl = fmtUrl;
                        }
                    }
                    else if (ext.Equals("mp4", StringComparison.OrdinalIgnoreCase) && height > bestMp4VideoHeight)
                    {
                        bestMp4VideoHeight = height;
                        bestMp4VideoUrl = fmtUrl;
                    }
                }
                else if (!hasVideo && hasAudio)
                {
                    if (isOpenAudioCodec)
                    {
                        if (abr > bestWebmAudioAbr || bestWebmAudioUrl is null)
                        {
                            bestWebmAudioAbr = abr;
                            bestWebmAudioUrl = fmtUrl;
                        }
                    }
                    else if (ext.Equals("m4a", StringComparison.OrdinalIgnoreCase) || ext.Equals("mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        if (abr > bestM4aAudioAbr || bestM4aAudioUrl is null)
                        {
                            bestM4aAudioAbr = abr;
                            bestM4aAudioUrl = fmtUrl;
                        }
                    }
                }
            }
        }

        // Chọn tổ hợp tối ưu nhất tương thích CefSharp:
        // 1. WebM DASH 1080p/720p (VP9 + Opus) — chuẩn mở, phát mượt trong CefSharp, có đầy đủ phụ đề & AI lồng tiếng!
        // 2. WebM Progressive
        // 3. MP4 Progressive
        // 4. MP4 + M4A DASH
        string? finalVideoUrl = null;
        string? finalAudioUrl = null;
        string qualityLabel = "HD";

        if (!string.IsNullOrWhiteSpace(bestWebmVideoUrl) && !string.IsNullOrWhiteSpace(bestWebmAudioUrl))
        {
            finalVideoUrl = bestWebmVideoUrl;
            finalAudioUrl = bestWebmAudioUrl;
            qualityLabel = FormatQuality(bestWebmVideoHeight);
        }
        else if (!string.IsNullOrWhiteSpace(progressiveWebmUrl))
        {
            finalVideoUrl = progressiveWebmUrl;
            qualityLabel = FormatQuality(progressiveWebmHeight);
        }
        else if (!string.IsNullOrWhiteSpace(progressiveMp4Url))
        {
            finalVideoUrl = progressiveMp4Url;
            qualityLabel = FormatQuality(progressiveMp4Height);
        }
        else if (!string.IsNullOrWhiteSpace(bestMp4VideoUrl) && !string.IsNullOrWhiteSpace(bestM4aAudioUrl))
        {
            finalVideoUrl = bestMp4VideoUrl;
            finalAudioUrl = bestM4aAudioUrl;
            qualityLabel = FormatQuality(bestMp4VideoHeight);
        }
        else
        {
            string? rootUrl = TryReadString(root, "url");
            if (!string.IsNullOrWhiteSpace(rootUrl) && rootUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                finalVideoUrl = rootUrl;
                qualityLabel = FormatQuality(TryReadInt(root, "height"));
            }
        }

        string? viSubUrl = FindCaptionTrackUrl(root, "subtitles", "vi")
                           ?? FindCaptionTrackUrl(root, "automatic_captions", "vi");

        string? origSubUrl = FindCaptionTrackUrl(root, "subtitles", "en")
                             ?? FindCaptionTrackUrl(root, "automatic_captions", "en");
        string? origLang = origSubUrl is not null ? "en" : null;

        if (origSubUrl is null)
        {
            var firstCaption = FindFirstCaptionTrack(root, "subtitles")
                               ?? FindFirstCaptionTrack(root, "automatic_captions");
            if (firstCaption.HasValue)
            {
                origSubUrl = firstCaption.Value.Url;
                origLang = firstCaption.Value.Lang;
            }
        }

        if (string.IsNullOrWhiteSpace(finalVideoUrl))
        {
            return new ResolvedPlaybackStream
            {
                Success = false,
                Title = title,
                DurationSeconds = duration,
                ErrorMessage = "Không tìm thấy luồng phát trực tiếp phù hợp trong kết quả yt-dlp."
            };
        }

        return new ResolvedPlaybackStream
        {
            Success = true,
            StreamUrl = finalVideoUrl,
            AudioUrl = finalAudioUrl,
            Title = title,
            QualityLabel = qualityLabel,
            DurationSeconds = duration,
            VietnameseSubtitleUrl = viSubUrl,
            OriginalSubtitleUrl = origSubUrl,
            OriginalSubtitleLang = origLang
        };
    }

    private static string? TryReadString(JsonElement obj, string propName)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(propName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static int TryReadInt(JsonElement obj, string propName)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(propName, out var prop) &&
            prop.ValueKind == JsonValueKind.Number)
        {
            if (prop.TryGetInt32(out int i))
            {
                return i;
            }
            if (prop.TryGetDouble(out double d))
            {
                return (int)Math.Round(d);
            }
        }
        return 0;
    }

    private static double TryReadDouble(JsonElement obj, string propName)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(propName, out var prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetDouble(out double d))
        {
            return d;
        }
        return 0;
    }

    private static string FormatQuality(int height) =>
        height >= 2160 ? "4K 2160p" : (height > 0 ? $"{height}p" : "HD");

    private static string? FindCaptionTrackUrl(JsonElement root, string sectionName, string langPrefix)
    {
        if (!root.TryGetProperty(sectionName, out var section) || section.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var prop in section.EnumerateObject())
        {
            if (!prop.Name.Equals(langPrefix, StringComparison.OrdinalIgnoreCase) &&
                !prop.Name.StartsWith(langPrefix + "-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? url = PickBestVttOrSrtUrl(prop.Value);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    private static (string Url, string Lang)? FindFirstCaptionTrack(JsonElement root, string sectionName)
    {
        if (!root.TryGetProperty(sectionName, out var section) || section.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var prop in section.EnumerateObject())
        {
            string? url = PickBestVttOrSrtUrl(prop.Value);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return (url, prop.Name);
            }
        }

        return null;
    }

    private static string? PickBestVttOrSrtUrl(JsonElement trackArray)
    {
        if (trackArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? fallback = null;
        foreach (var item in trackArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string ext = TryReadString(item, "ext") ?? string.Empty;
            string? url = TryReadString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (ext.Equals("vtt", StringComparison.OrdinalIgnoreCase) || ext.Equals("srt", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            fallback ??= url;
        }

        return fallback;
    }
}
