using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QUANGLNBrowser.App.Services;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;
using QUANGLNBrowser.Engine;
using QUANGLNBrowser.Media;
using QUANGLNBrowser.Network;

namespace QUANGLNBrowser.App.Views;

/// <summary>
/// Cửa sổ QUANGLN Player — Trình phát Video AI Vietsub &amp; AI Lồng tiếng,
/// tự động phân giải luồng YouTube/TikTok/Facebook (1080p DASH Video + Audio đồng bộ) qua yt-dlp.exe,
/// trích xuất &amp; dịch phụ đề tự động qua PageTranslationService,
/// tổng hợp giọng đọc AI Tiếng Việt thật qua VietnameseTtsService (MP3),
/// đồng thời hỗ trợ dự phòng giải mã phần cứng libmpv-2.dll căn khớp chính xác khung hình.
/// Tầng hiển thị sử dụng Microsoft.Web.WebView2 hỗ trợ đầy đủ tăng tốc phần cứng GPU và giải mã video.
/// </summary>
public partial class MpvPlayerWindow : Window
{
    private const string PlayerCmdPrefix = "QUANGLN_PLAYER_CMD:";
    private static readonly string[] SupportedSubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt"];
    private static readonly string[] SupportedVideoExtensions = [".mp4", ".webm", ".mkv", ".mov", ".m4v", ".avi"];

    private readonly WebView2 PlayerBrowser;
    private readonly PageTranslationService _translationService = new();
    private readonly VietnameseTtsService _ttsService = new();
    private readonly JobObjectManager _jobManager = new();
    private readonly DispatcherTimer _mpvTickTimer;
    private readonly CancellationTokenSource _windowCts = new();

    private DetectedMedia? _currentMedia;
    private DetectedMedia? _pendingMedia;
    private bool _isPlayerDomReady;
    private Rect _lastStageRect = new(0, 45, 860, 620);
    private int _latestTtsSeq;
    private volatile bool _isClosed;
    private List<object>? _cachedJsDownloadFormats;

    public event Action<DetectedMedia>? DownloadRequested;

    public MpvPlayerWindow()
    {
        InitializeComponent();

        PlayerBrowser = new WebView2();
        PlayerBrowserHost.Child = PlayerBrowser;

        BrowserThemeManager.ThemeChanged += OnGlobalThemeChanged;
        Closed += OnWindowClosed;

        _mpvTickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _mpvTickTimer.Tick += OnMpvTick;

        _ = InitializePlayerBrowserAsync();
    }

    private async Task InitializePlayerBrowserAsync()
    {
        try
        {
            var env = await WebView2EnvironmentManager.GetStandardEnvironmentAsync();
            await PlayerBrowser.EnsureCoreWebView2Async(env);

            if (PlayerBrowser.CoreWebView2 is null)
            {
                return;
            }

            PlayerBrowser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            PlayerBrowser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            PlayerBrowser.CoreWebView2.Settings.AreDevToolsEnabled = true;

            PlayerBrowser.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                if (!e.IsSuccess)
                {
                    AppLogger.Error($"[MpvPlayerWindow] Điều hướng WebView2 player.html thất bại: Status={e.WebErrorStatus}");
                }
                else
                {
                    AppLogger.Info("[MpvPlayerWindow] player.html đã nạp thành công trong WebView2.");
                }
            };

            string playerHtmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "player.html");
            if (File.Exists(playerHtmlPath))
            {
                PlayerBrowser.CoreWebView2.Navigate(new Uri(playerHtmlPath).AbsoluteUri);
            }
            else
            {
                AppLogger.Error($"[MpvPlayerWindow] Không tìm thấy player.html tại: {playerHtmlPath}");
            }

            // Watchdog: Nếu DOM chưa sẵn sàng sau 2 giây, kích hoạt bootstrap dự phòng
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000, _windowCts.Token);
                    if (!_isPlayerDomReady && !_isClosed)
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            if (!_isPlayerDomReady && PlayerBrowser.CoreWebView2 is not null && !_isClosed)
                            {
                                AppLogger.Warn("[MpvPlayerWindow] player-ready chưa nhận được sau 2s, kích hoạt bootstrapPlayer() dự phòng...");
                                await PlayerBrowser.CoreWebView2.ExecuteScriptAsync("window.bootstrapPlayer && window.bootstrapPlayer();");
                            }
                        });
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("Khởi tạo WebView2 cho MpvPlayerWindow thất bại", ex);
        }
    }

    public void PlayMedia(DetectedMedia media)
    {
        if (media is null || string.IsNullOrWhiteSpace(media.Url))
        {
            AppLogger.Warn("[MpvPlayerWindow] PlayMedia nhận media null hoặc URL rỗng");
            return;
        }

        media.Url = MediaStreamParser.SanitizeWatchUrl(media.Url);
        AppLogger.Info($"[MpvPlayerWindow] PlayMedia nhận URL={media.Url}, Title={media.DisplayTitle}, Type={media.Type}");
        Title = $"QUANGLN Player — {media.DisplayTitle}";
        _currentMedia = media;
        _pendingMedia = media;

        if (_isPlayerDomReady && PlayerBrowser.CoreWebView2 is not null)
        {
            _ = PushPendingMediaToPlayerAsync();
        }
    }

    public void SetAvailableDownloadFormats(IEnumerable<DetectedMedia> formats)
    {
        var list = formats?.ToList();
        if (list is null || list.Count == 0) return;

        var jsDownloadFormats = new List<object>();
        foreach (var fmt in list)
        {
            string val = fmt.QualityLabel.Replace("p", "").Trim();
            string sizeText = fmt.FileSize > 0 ? fmt.DisplaySize : string.Empty;
            bool isAudio = val.Equals("audio", StringComparison.OrdinalIgnoreCase);
            string name = isAudio ? "Trung bình" : (val switch
            {
                "2160" => "4K UHD",
                "1440" => "2K QHD",
                "1080" => "Full HD",
                "720" => "HD",
                "480" => "Tiêu chuẩn",
                "360" => "Trung bình",
                "240" => "Thấp",
                "144" => "Di động",
                _ => val + "p"
            });
            string res = isAudio ? "128kbps" : (val + "p");
            string ext = isAudio
                ? (string.IsNullOrEmpty(sizeText) ? ".m4a" : $".m4a • {sizeText}")
                : (string.IsNullOrEmpty(sizeText) ? ".mp4" : $".mp4 • {sizeText}");

            jsDownloadFormats.Add(new
            {
                value = val,
                name,
                res,
                ext,
                sizeText,
                isAudio
            });
        }

        _cachedJsDownloadFormats = jsDownloadFormats;

        if (_isPlayerDomReady && PlayerBrowser.CoreWebView2 is not null)
        {
            string json = JsonSerializer.Serialize(jsDownloadFormats);
            ExecuteJs($"if (window.__quanglnSetPlayerDownloadFormats) window.__quanglnSetPlayerDownloadFormats({json});");
        }
    }

    public void PausePlayback()
    {
        try
        {
            Interlocked.Increment(ref _latestTtsSeq);

            if (NativeMpvContainer.Visibility == Visibility.Visible && VideoHost.IsMpvInitialized)
            {
                VideoHost.SetPaused(true);
            }

            if (_isPlayerDomReady && PlayerBrowser.CoreWebView2 is not null)
            {
                ExecuteJs("window.__quanglnPlayerPause && window.__quanglnPlayerPause();");
            }
        }
        catch
        {
            // Bỏ qua nếu trình duyệt đang đóng
        }
    }

    private async Task PushPendingMediaToPlayerAsync()
    {
        if (_pendingMedia is null || !_isPlayerDomReady || PlayerBrowser.CoreWebView2 is null)
        {
            return;
        }

        var media = _pendingMedia;
        _pendingMedia = null;

        HideNativeMpvOverlay();

        bool needsYtDlpResolve =
            media.Type == MediaStreamType.YtDlp ||
            MediaStreamParser.IsYtDlpFirstPlatform(media.Url);

        if (needsYtDlpResolve)
        {
            await ResolveAndLoadWebMediaAsync(media, autoPlay: true);
            return;
        }

        string resolvedUrl = media.Url;
        if (File.Exists(resolvedUrl))
        {
            resolvedUrl = new Uri(Path.GetFullPath(resolvedUrl)).AbsoluteUri;
        }

        string ext = Path.GetExtension(media.Url).ToLowerInvariant();
        bool isNativePreferred = ext == ".mkv" || ext == ".avi" || ext == ".flv" || ext == ".wmv" || ext == ".mov";
        if (isNativePreferred && File.Exists(AppPaths.GetNativeDllPath("libmpv-2.dll")))
        {
            AppLogger.Info($"[QUANGLN Player] File định dạng {ext} được giải mã trực tiếp bằng libmpv");
            ActivateNativeMpvFallback(media);
            return;
        }

        PushMediaPayloadToJs(
            resolvedUrl,
            audioUrl: media.AudioUrl,
            sourcePageUrl: media.Url,
            media.DisplayTitle,
            Path.GetFileName(media.Url),
            string.IsNullOrWhiteSpace(media.QualityLabel) ? "HD Stream" : media.QualityLabel,
            0,
            null);

        // Nạp trực tiếp phụ đề nếu đã được trích xuất từ nguồn web (ví dụ: Motchill VTT)
        if (!string.IsNullOrWhiteSpace(media.SubtitleUrl))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    AppLogger.Info($"[AI Vietsub] Đang nạp phụ đề từ web URL: {media.SubtitleUrl}");
                    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    if (!string.IsNullOrWhiteSpace(media.UserAgent))
                    {
                        hc.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", media.UserAgent);
                    }
                    if (!string.IsNullOrWhiteSpace(media.Referer))
                    {
                        hc.DefaultRequestHeaders.TryAddWithoutValidation("Referer", media.Referer);
                    }
                    string vttRaw = await hc.GetStringAsync(media.SubtitleUrl, _windowCts.Token);
                    var segments = SubtitleTranscriptService.ParseSubtitleContent(vttRaw);
                    if (segments.Count > 0 && !_isClosed)
                    {
                        var jsSubs = new List<object>();
                        foreach (var s in segments.Take(2500))
                        {
                            jsSubs.Add(new
                            {
                                start = s.StartSeconds,
                                end = s.EndSeconds,
                                speaker = s.Speaker,
                                gender = s.Gender,
                                en = string.Empty,
                                vi = s.EffectiveVietnameseText,
                                customVi = s.EffectiveVietnameseText
                            });
                        }

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_isPlayerDomReady && PlayerBrowser.CoreWebView2 is not null && !_isClosed)
                            {
                                string segJson = JsonSerializer.Serialize(jsSubs);
                                string nameJson = JsonSerializer.Serialize("Phụ đề từ nguồn web");
                                ExecuteJs($"window.__quanglnPlayerLoadSubtitles && window.__quanglnPlayerLoadSubtitles({segJson}, {nameJson});");
                                AppLogger.Info($"[AI Vietsub] Nạp thành công {jsSubs.Count} dòng phụ đề từ web vào player.");
                            }
                        });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLogger.Warn($"[AI Vietsub] Không thể nạp phụ đề trực tiếp từ {media.SubtitleUrl}: {ex.Message}");
                }
            });
        }

        // Tự động dò tìm phụ đề ngầm nếu video phát từ trang web (hỗ trợ phụ đề & AI lồng tiếng cho luồng phát trực tiếp)
        string? candidatePageUrl = !string.IsNullOrWhiteSpace(media.Referer) && media.Referer.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? media.Referer
            : (media.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? media.Url : null);

        if (!string.IsNullOrWhiteSpace(candidatePageUrl))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    AppLogger.Info($"[AI Vietsub] Tự động dò tìm phụ đề ngầm từ trang nguồn: {candidatePageUrl}");
                    var extracted = await StreamPlaybackResolver.DownloadSubtitlesViaYtDlpAsync(
                        candidatePageUrl,
                        _jobManager,
                        cookie: media.Cookie,
                        userAgent: media.UserAgent,
                        preferredSourceLang: "auto",
                        cancellationToken: _windowCts.Token);

                    if (extracted.VietnameseSegments.Count > 0 || extracted.OriginalSegments.Count > 0)
                    {
                        var jsSubs = await BuildTranslatedJsSubtitlesAsync(extracted, _windowCts.Token);
                        if (jsSubs.Count > 0 && !_isClosed)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (_isPlayerDomReady && PlayerBrowser.CoreWebView2 is not null && !_isClosed)
                                {
                                    string segJson = JsonSerializer.Serialize(jsSubs);
                                    string nameJson = JsonSerializer.Serialize(
                                        string.IsNullOrWhiteSpace(extracted.DetectedOriginalLang)
                                            ? "AI Vietsub Tự Động"
                                            : $"AI Vietsub ({extracted.DetectedOriginalLang.ToUpperInvariant()} → VI)");
                                    ExecuteJs($"window.__quanglnPlayerLoadSubtitles && window.__quanglnPlayerLoadSubtitles({segJson}, {nameJson});");
                                    AppLogger.Info($"[AI Vietsub] Tự động nạp thành công {jsSubs.Count} dòng phụ đề vào player.");
                                }
                            });
                        }
                    }
                    else
                    {
                        AppLogger.Info($"[AI Vietsub] Không tìm thấy phụ đề có sẵn trên máy chủ video cho {candidatePageUrl}.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLogger.Warn($"[AI Vietsub] Tự động dò phụ đề không khả dụng: {ex.Message}");
                }
            });
        }
    }

    private async Task ResolveAndLoadWebMediaAsync(DetectedMedia media, bool autoPlay)
    {
        SetPlayerLoading(true, "Đang phân tích luồng video HD...");
        AppLogger.Info($"[QUANGLN Player] Bắt đầu phân giải luồng video cho {media.Url}");

        var streamTask = StreamPlaybackResolver.ResolveAsync(
            media.Url,
            _jobManager,
            media.Cookie,
            media.UserAgent,
            _windowCts.Token);

        var subsTask = StreamPlaybackResolver.DownloadSubtitlesViaYtDlpAsync(
            media.Url,
            _jobManager,
            media.Cookie,
            media.UserAgent,
            preferredSourceLang: null,
            cancellationToken: _windowCts.Token);

        // Chờ luồng phát video trước để phát ngay lập tức (không để tải phụ đề làm chậm video)
        var resolved = await streamTask;

        if (_windowCts.IsCancellationRequested)
        {
            return;
        }

        if (!resolved.Success || string.IsNullOrWhiteSpace(resolved.StreamUrl))
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SetPlayerLoading(false);
                AppLogger.Warn($"[QUANGLN Player] Không thể trích xuất luồng video qua yt-dlp cho {media.Url}: {resolved.ErrorMessage}");

                if (File.Exists(AppPaths.GetNativeDllPath("libmpv-2.dll")))
                {
                    AppLogger.Info($"[QUANGLN Player] Dự phòng giải mã trực tiếp bằng libmpv cho: {media.Url}");
                    ActivateNativeMpvFallback(media, null);
                }
                else
                {
                    string warnMsg = string.IsNullOrWhiteSpace(resolved.ErrorMessage)
                        ? "Không thể trích xuất luồng video từ trang này. Vui lòng bấm nút Phát (Play) video trên web trước khi bấm Xem riêng để trình duyệt bắt luồng."
                        : $"Không thể phát tách cửa sổ: {resolved.ErrorMessage}";
                    string safeWarn = JsonSerializer.Serialize(warnMsg);
                    ExecuteJs($"window.__quanglnShowToast && window.__quanglnShowToast({safeWarn});");
                }
            });
            return;
        }

        AppLogger.Info($"[QUANGLN Player] Phân giải luồng video thành công: {resolved.Title} ({resolved.QualityLabel})");

        await Dispatcher.InvokeAsync(() =>
        {
            Title = $"QUANGLN Player — {resolved.Title}";
            SetPlayerLoading(false);

            if (resolved.DownloadFormats.Count > 0)
            {
                SetAvailableDownloadFormats(resolved.DownloadFormats);
            }

            if (autoPlay)
            {
                PushMediaPayloadToJs(
                    resolved.StreamUrl,
                    resolved.AudioUrl,
                    media.Url,
                    resolved.Title,
                    resolved.Title,
                    resolved.QualityLabel,
                    resolved.DurationSeconds,
                    null,
                    _cachedJsDownloadFormats);
            }
            else if (PlayerBrowser.CoreWebView2 is not null)
            {
                string titleJson = JsonSerializer.Serialize(resolved.Title);
                ExecuteJs($"var el = document.getElementById('ytdlp-res-title'); if (el) el.textContent = {titleJson};");
            }
        });

        // Phụ đề được tải ngầm song song, khi sẵn sàng tự động nạp vào player mà không ngắt quãng video
        _ = Task.Run(async () =>
        {
            try
            {
                var extractedSubs = await subsTask;
                if (extractedSubs != null && (extractedSubs.VietnameseSegments.Count > 0 || extractedSubs.OriginalSegments.Count > 0))
                {
                    var jsSubtitles = await BuildTranslatedJsSubtitlesAsync(extractedSubs, _windowCts.Token);
                    if (jsSubtitles.Count > 0 && !_isClosed)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_isPlayerDomReady && PlayerBrowser.CoreWebView2 is not null && !_isClosed)
                            {
                                string segJson = JsonSerializer.Serialize(jsSubtitles);
                                string nameJson = JsonSerializer.Serialize(
                                    string.IsNullOrWhiteSpace(extractedSubs.DetectedOriginalLang)
                                        ? "AI Vietsub Tự Động"
                                        : $"AI Vietsub ({extractedSubs.DetectedOriginalLang.ToUpperInvariant()} → VI)");
                                ExecuteJs($"window.__quanglnPlayerLoadSubtitles && window.__quanglnPlayerLoadSubtitles({segJson}, {nameJson});");
                                AppLogger.Info($"[AI Vietsub] Nạp ngầm thành công {jsSubtitles.Count} dòng phụ đề vào player.");
                            }
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Warn($"[AI Vietsub] Tải phụ đề ngầm qua yt-dlp không khả dụng: {ex.Message}");
            }
        });
    }

    private async Task<List<object>> BuildTranslatedJsSubtitlesAsync(
        ExtractedSubtitlesResult extractedSubs,
        CancellationToken cancellationToken)
    {
        var jsSubtitles = new List<object>();
        try
        {
            var viSegs = extractedSubs.VietnameseSegments;
            var origSegs = extractedSubs.OriginalSegments;

            AppLogger.Info($"[AI Vietsub] Đang xử lý phụ đề: {viSegs.Count} dòng tiếng Việt sẵn có, {origSegs.Count} dòng ngôn ngữ gốc ({extractedSubs.DetectedOriginalLang ?? "chưa rõ"}).");

            if (viSegs.Count > 0)
            {
                foreach (var s in viSegs.Take(2500))
                {
                    var matchingOrig = origSegs.FirstOrDefault(o => Math.Abs(o.StartSeconds - s.StartSeconds) < 1.5);
                    jsSubtitles.Add(new
                    {
                        start = s.StartSeconds,
                        end = s.EndSeconds,
                        speaker = s.Speaker,
                        gender = s.Gender,
                        en = matchingOrig?.OriginalText ?? string.Empty,
                        vi = s.EffectiveVietnameseText,
                        customVi = s.EffectiveVietnameseText
                    });
                }
            }
            else if (origSegs.Count > 0)
            {
                int maxTranslate = Math.Min(origSegs.Count, 600);
                SetPlayerLoading(true, $"Đang dịch AI {maxTranslate} dòng phụ đề sang Tiếng Việt...");
                var limited = origSegs.Take(maxTranslate).ToList();
                var sourceTexts = limited.Select(s => s.OriginalText).ToList();
                var translatedMap = await _translationService.TranslateBatchAsync(sourceTexts, "vi", cancellationToken);

                foreach (var s in limited)
                {
                    string viText = translatedMap.TryGetValue(s.OriginalText, out string? tr) && !string.IsNullOrWhiteSpace(tr)
                        ? tr
                        : s.OriginalText;

                    jsSubtitles.Add(new
                    {
                        start = s.StartSeconds,
                        end = s.EndSeconds,
                        speaker = s.Speaker,
                        gender = s.Gender,
                        en = s.OriginalText,
                        vi = viText,
                        customVi = viText
                    });
                }
            }

            AppLogger.Info($"[AI Vietsub] Hoàn tất chuẩn bị {jsSubtitles.Count} dòng phụ đề song ngữ cho QUANGLN Player.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Subtitle translation for QUANGLN Player failed", ex);
        }

        return jsSubtitles;
    }

    private void PushMediaPayloadToJs(
        string streamUrl,
        string? audioUrl,
        string? sourcePageUrl,
        string title,
        string filename,
        string quality,
        double durationSeconds,
        List<object>? subtitles,
        List<object>? downloadFormats = null)
    {
        if (!_isPlayerDomReady || PlayerBrowser.CoreWebView2 is null)
        {
            return;
        }

        string payloadJson = JsonSerializer.Serialize(new
        {
            url = streamUrl,
            audioUrl,
            sourcePageUrl,
            title,
            filename,
            quality,
            duration = durationSeconds,
            subtitles,
            downloadFormats = downloadFormats ?? _cachedJsDownloadFormats
        });

        ExecuteJs($"window.__quanglnPlayerLoadMedia && window.__quanglnPlayerLoadMedia({payloadJson});");
    }

    private void SetPlayerLoading(bool isLoading, string? message = null)
    {
        if (!_isPlayerDomReady || PlayerBrowser.CoreWebView2 is null)
        {
            return;
        }

        string msgJson = JsonSerializer.Serialize(message ?? string.Empty);
        string flag = isLoading ? "true" : "false";
        ExecuteJs($"if (window.__quanglnSetLoading) window.__quanglnSetLoading({flag}, {msgJson}); if (window.__quanglnPlayerSetLoading) window.__quanglnPlayerSetLoading({flag}, {msgJson});");
    }

    private void ActivateNativeMpvFallback(DetectedMedia media, List<object>? subtitles = null)
    {
        void Run()
        {
            ApplyNativeMpvBounds();
            NativeMpvContainer.Visibility = Visibility.Visible;
            VideoHost.ResizeChildWindow((int)_lastStageRect.Width, (int)_lastStageRect.Height);
            VideoHost.LoadMedia(media);
            _mpvTickTimer.Start();

            if (_isPlayerDomReady && PlayerBrowser.CoreWebView2 is not null)
            {
                string infoJson = JsonSerializer.Serialize(new
                {
                    url = media.Url,
                    title = media.DisplayTitle,
                    subtitles = subtitles ?? new List<object>()
                });
                ExecuteJs($"window.__quanglnMpvActivated && window.__quanglnMpvActivated(true, {infoJson});");
            }
        }

        if (Dispatcher.CheckAccess())
        {
            Run();
        }
        else
        {
            Dispatcher.Invoke(Run);
        }
    }

    private void HideNativeMpvOverlay()
    {
        _mpvTickTimer.Stop();
        if (NativeMpvContainer.Visibility == Visibility.Visible)
        {
            try
            {
                if (VideoHost.IsMpvInitialized)
                {
                    VideoHost.Stop();
                }
            }
            catch
            {
                // Bỏ qua nếu mpv chưa khởi tạo
            }
            NativeMpvContainer.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyNativeMpvBounds()
    {
        if (_lastStageRect.Width < 100 || _lastStageRect.Height < 80)
        {
            return;
        }

        NativeMpvContainer.Margin = new Thickness(_lastStageRect.Left, _lastStageRect.Top, 0, 0);
        NativeMpvContainer.Width = _lastStageRect.Width;
        NativeMpvContainer.Height = _lastStageRect.Height;
        VideoHost.ResizeChildWindow((int)_lastStageRect.Width, (int)_lastStageRect.Height);
    }

    private void OnMpvTick(object? sender, EventArgs e)
    {
        if (NativeMpvContainer.Visibility != Visibility.Visible ||
            !VideoHost.IsMpvInitialized ||
            !_isPlayerDomReady ||
            PlayerBrowser.CoreWebView2 is null)
        {
            return;
        }

        double pos = VideoHost.GetPositionSeconds();
        double dur = VideoHost.GetDurationSeconds();
        string paused = VideoHost.IsPaused ? "true" : "false";

        string script = string.Format(
            CultureInfo.InvariantCulture,
            "window.__quanglnMpvTick && window.__quanglnMpvTick({0:F2}, {1:F2}, {2});",
            pos,
            dur,
            paused);
        ExecuteJs(script);
    }

    private void SyncThemeToPlayer(string? themeKey)
    {
        if (!_isPlayerDomReady || PlayerBrowser.CoreWebView2 is null)
        {
            return;
        }

        string mapped = (themeKey ?? "coccoc").Trim().ToLowerInvariant() switch
        {
            "crimson" => "crimson",
            "cyan" => "cyan",
            "purple" => "purple",
            _ => "neon"
        };

        string arg = JsonSerializer.Serialize(mapped);
        ExecuteJs($"window.__quanglnApplyTheme && window.__quanglnApplyTheme({arg});");
    }

    private void OnGlobalThemeChanged(object? sender, BrowserThemePreset preset)
    {
        Dispatcher.InvokeAsync(() => SyncThemeToPlayer(preset.Key));
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_isClosed)
        {
            return;
        }

        try
        {
            string raw = e.TryGetWebMessageAsString();
            if (!string.IsNullOrWhiteSpace(raw) && raw.StartsWith(PlayerCmdPrefix, StringComparison.Ordinal))
            {
                string json = raw[PlayerCmdPrefix.Length..];
                Dispatcher.InvokeAsync(async () => await HandlePlayerCommandAsync(json));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error handling WebMessage in MpvPlayerWindow", ex);
        }
    }

    private async Task HandlePlayerCommandAsync(string json)
    {
        if (_isClosed)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string action = root.TryGetProperty("action", out var actProp) ? (actProp.GetString() ?? string.Empty) : string.Empty;

            AppLogger.Info($"[MpvPlayerWindow] Nhận lệnh từ player.html: action={action}");

            switch (action)
            {
                case "player-ready":
                    _isPlayerDomReady = true;
                    SyncThemeToPlayer(BrowserThemeManager.CurrentThemeKey);
                    if (_cachedJsDownloadFormats is not null)
                    {
                        string formatsJson = JsonSerializer.Serialize(_cachedJsDownloadFormats);
                        ExecuteJs($"if (window.__quanglnSetPlayerDownloadFormats) window.__quanglnSetPlayerDownloadFormats({formatsJson});");
                    }
                    await PushPendingMediaToPlayerAsync();
                    break;

                case "js-error":
                {
                    string jsErr = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "" : "";
                    string jsFile = root.TryGetProperty("filename", out var jsFnProp) ? jsFnProp.GetString() ?? "" : "";
                    int jsLine = root.TryGetProperty("lineno", out var jsLnProp) ? jsLnProp.GetInt32() : 0;
                    AppLogger.Warn($"[MpvPlayerWindow JS Error] {jsFile}:{jsLine} - {jsErr}");
                    break;
                }

                case "stage-rect":
                    double left = root.TryGetProperty("left", out var lp) && lp.TryGetDouble(out double l) ? l : 0;
                    double top = root.TryGetProperty("top", out var tp) && tp.TryGetDouble(out double t) ? t : 45;
                    double width = root.TryGetProperty("width", out var wp) && wp.TryGetDouble(out double w) ? w : 860;
                    double height = root.TryGetProperty("height", out var hp) && hp.TryGetDouble(out double h) ? h : 620;
                    _lastStageRect = new Rect(left, top, width, height);
                    if (NativeMpvContainer.Visibility == Visibility.Visible)
                    {
                        ApplyNativeMpvBounds();
                    }
                    break;

                case "win-min":
                    WindowState = WindowState.Minimized;
                    break;

                case "win-max":
                case "win-fullscreen":
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    break;

                case "win-close":
                    Close();
                    break;

                case "pin-top":
                    if (root.TryGetProperty("pinned", out var pinProp))
                    {
                        Topmost = pinProp.ValueKind == JsonValueKind.True;
                    }
                    else
                    {
                        Topmost = !Topmost;
                    }
                    break;

                case "theme-change":
                    if (root.TryGetProperty("theme", out var themeProp))
                    {
                        string themeKey = themeProp.GetString() ?? "neon";
                        string browserTheme = themeKey == "neon" ? "coccoc" : themeKey;
                        BrowserThemeManager.ApplyTheme(browserTheme);
                    }
                    break;

                case "speak-vi":
                    if (root.TryGetProperty("text", out var ttsTextProp))
                    {
                        string speechText = ttsTextProp.GetString() ?? string.Empty;
                        string voiceId = root.TryGetProperty("voice", out var vIdProp) ? (vIdProp.GetString() ?? "auto") : "auto";
                        string gender = root.TryGetProperty("gender", out var gProp) ? (gProp.GetString() ?? "male") : "male";
                        string emotion = root.TryGetProperty("emotion", out var emProp) ? (emProp.GetString() ?? "natural") : "natural";
                        double rate = root.TryGetProperty("rate", out var rProp) && rProp.TryGetDouble(out double rv) ? rv : 1.0;
                        double volume = root.TryGetProperty("volume", out var vProp) && vProp.TryGetDouble(out double vv) ? vv : 0.9;
                        int seq = root.TryGetProperty("seq", out var seqProp) && seqProp.TryGetInt32(out int sv) ? sv : 0;

                        Volatile.Write(ref _latestTtsSeq, seq);
                        AppLogger.Info($"[AI Lồng tiếng] Nhận yêu cầu đọc thoại (seq: {seq}, giọng: {voiceId}): \"{(speechText.Length > 40 ? speechText[..40] + "..." : speechText)}\"");

                        string? dataUri = null;
                        try
                        {
                            dataUri = await _ttsService.SynthesizeDataUriAsync(speechText, voiceId, gender, emotion, rate, _windowCts.Token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            AppLogger.Warn($"[AI Lồng tiếng] Lỗi tổng hợp giọng đọc: {ex.Message}");
                        }

                        if (!string.IsNullOrEmpty(dataUri) &&
                            !_isClosed &&
                            Volatile.Read(ref _latestTtsSeq) == seq &&
                            PlayerBrowser.CoreWebView2 is not null)
                        {
                            string uriJson = JsonSerializer.Serialize(dataUri);
                            string script = string.Format(
                                CultureInfo.InvariantCulture,
                                "window.__quanglnPlayTtsAudio && window.__quanglnPlayTtsAudio({0}, 1.0, {1:F2}, {2});",
                                uriJson,
                                volume,
                                seq);
                            ExecuteJs(script);
                        }
                    }
                    break;

                case "stop-tts":
                    Interlocked.Increment(ref _latestTtsSeq);
                    break;

                case "rescan-vietsub":
                    if (root.TryGetProperty("url", out var rescanUrlProp))
                    {
                        string rescanUrl = (rescanUrlProp.GetString() ?? string.Empty).Trim();
                        string? srcLang = root.TryGetProperty("srcLang", out var langProp) ? langProp.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(rescanUrl))
                        {
                            AppLogger.Info($"[AI Vietsub] Bắt đầu quét lại phụ đề cho video: {rescanUrl}");
                            SetPlayerLoading(true, "Đang trích xuất & dịch AI phụ đề từ video...");
                            try
                            {
                                var extracted = await StreamPlaybackResolver.DownloadSubtitlesViaYtDlpAsync(
                                    rescanUrl,
                                    _jobManager,
                                    cookie: null,
                                    userAgent: null,
                                    preferredSourceLang: srcLang,
                                    cancellationToken: _windowCts.Token);

                                var translatedSubs = await BuildTranslatedJsSubtitlesAsync(extracted, _windowCts.Token);
                                SetPlayerLoading(false);

                                if (!_isClosed && PlayerBrowser.CoreWebView2 is not null)
                                {
                                    if (translatedSubs.Count > 0)
                                    {
                                        string segJson = JsonSerializer.Serialize(translatedSubs);
                                        string nameJson = JsonSerializer.Serialize(
                                            string.IsNullOrWhiteSpace(extracted.DetectedOriginalLang)
                                                ? "AI Vietsub"
                                                : $"AI Vietsub ({extracted.DetectedOriginalLang.ToUpperInvariant()} → VI)");
                                        ExecuteJs($"window.__quanglnPlayerLoadSubtitles && window.__quanglnPlayerLoadSubtitles({segJson}, {nameJson});");
                                        AppLogger.Info($"[AI Vietsub] Đã nạp thành công {translatedSubs.Count} dòng phụ đề vào player.");
                                    }
                                    else
                                    {
                                        string rawTitle = Title.Replace("QUANGLN Player — ", "");
                                        string titleJson = JsonSerializer.Serialize(rawTitle);
                                        ExecuteJs($"window.__quanglnPlayerNoSubtitlesFound && window.__quanglnPlayerNoSubtitlesFound({titleJson});");
                                        AppLogger.Info($"[AI Vietsub] Không tìm thấy phụ đề rời trên máy chủ cho: {rescanUrl}");
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                SetPlayerLoading(false);
                                AppLogger.Error("[AI Vietsub] Lỗi khi quét lại phụ đề", ex);
                                string rawTitle = Title.Replace("QUANGLN Player — ", "");
                                string titleJson = JsonSerializer.Serialize(rawTitle);
                                ExecuteJs($"window.__quanglnPlayerNoSubtitlesFound && window.__quanglnPlayerNoSubtitlesFound({titleJson});");
                            }
                        }
                    }
                    break;

                case "pick-subtitle-file":
                    _ = Dispatcher.InvokeAsync(async () =>
                    {
                        var dlg = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = "Chọn tệp phụ đề rời (.SRT, .VTT, .ASS) cho phim",
                            Filter = "Tệp phụ đề (*.srt;*.vtt;*.ass;*.ssa)|*.srt;*.vtt;*.ass;*.ssa|Tất cả tệp (*.*)|*.*",
                            Multiselect = false
                        };
                        if (dlg.ShowDialog(this) == true && File.Exists(dlg.FileName))
                        {
                            await LoadSubtitleFromFileAsync(dlg.FileName);
                        }
                    });
                    break;

                case "search-subtitles-online":
                    if (root.TryGetProperty("query", out var queryProp))
                    {
                        string q = (queryProp.GetString() ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(q))
                        {
                            q = Title.Replace("QUANGLN Player — ", "");
                        }
                        q = Regex.Replace(q, @"(?i)(motchill|phim\s*online|phimmoi|vietsub|thuyet\s*minh|tập\s*\d+|tap\s*\d+|hls|index\.m3u8)", " ").Trim();
                        q = Regex.Replace(q, @"\s+", " ");
                        string searchUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(q + " vietsub srt")}";
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = searchUrl,
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }
                    break;

                case "load-sample-subtitles":
                    var sampleSubs = new List<object>
                    {
                        new { start = 0.5, end = 4.2, speaker = "Minh Quân", gender = "male", en = "Xin chào! Bạn đang trải nghiệm tính năng AI Lồng tiếng của QUANGLN Player.", vi = "Xin chào! Bạn đang trải nghiệm tính năng AI Lồng tiếng của QUANGLN Player.", customVi = "Xin chào! Bạn đang trải nghiệm tính năng AI Lồng tiếng của QUANGLN Player." },
                        new { start = 4.5, end = 8.8, speaker = "Khánh Linh", gender = "female", en = "Hệ thống hỗ trợ thuyết minh đa nhân vật với giọng đọc nơ-ron tự nhiên như người thật.", vi = "Hệ thống hỗ trợ thuyết minh đa nhân vật với giọng đọc nơ-ron tự nhiên như người thật.", customVi = "Hệ thống hỗ trợ thuyết minh đa nhân vật với giọng đọc nơ-ron tự nhiên như người thật." },
                        new { start = 9.2, end = 13.8, speaker = "Hoàng Nam", gender = "male", en = "Với phim Hardsub trên web, bạn có thể nạp tệp phụ đề .SRT rời để kích hoạt thuyết minh tiếng Việt.", vi = "Với phim Hardsub trên web, bạn có thể nạp tệp phụ đề .SRT rời để kích hoạt thuyết minh tiếng Việt.", customVi = "Với phim Hardsub trên web, bạn có thể nạp tệp phụ đề .SRT rời để kích hoạt thuyết minh tiếng Việt." },
                        new { start = 14.2, end = 19.0, speaker = "Thảo Vy", gender = "female", en = "Chúc bạn có những giây phút xem phim tuyệt vời cùng QUANGLN Browser!", vi = "Chúc bạn có những giây phút xem phim tuyệt vời cùng QUANGLN Browser!", customVi = "Chúc bạn có những giây phút xem phim tuyệt vời cùng QUANGLN Browser!" }
                    };
                    string sampleJson = JsonSerializer.Serialize(sampleSubs);
                    ExecuteJs($"window.__quanglnPlayerLoadSubtitles && window.__quanglnPlayerLoadSubtitles({sampleJson}, 'Kịch bản thử nghiệm AI Lồng tiếng');");
                    break;

                case "translate-subtitles":
                    if (root.TryGetProperty("lines", out var linesProp) && linesProp.ValueKind == JsonValueKind.Array)
                    {
                        var sourceLines = linesProp.EnumerateArray()
                            .Select(x => x.GetString() ?? string.Empty)
                            .ToList();

                        if (sourceLines.Count > 0)
                        {
                            var batch = await _translationService.TranslateBatchAsync(sourceLines, "vi", _windowCts.Token);
                            if (batch.Count > 0 && PlayerBrowser.CoreWebView2 is not null)
                            {
                                var translatedList = sourceLines
                                    .Select(line => batch.TryGetValue(line, out string? tr) && !string.IsNullOrWhiteSpace(tr) ? tr : line)
                                    .ToList();
                                string translatedJson = JsonSerializer.Serialize(translatedList);
                                ExecuteJs($"window.__quanglnReceiveTranslatedSubtitles && window.__quanglnReceiveTranslatedSubtitles({translatedJson});");
                            }
                        }
                    }
                    break;

                case "ytdlp-resolve":
                    if (root.TryGetProperty("url", out var resolveUrlProp))
                    {
                        string targetUrl = (resolveUrlProp.GetString() ?? string.Empty).Trim();
                        bool autoPlay = !root.TryGetProperty("autoPlay", out var ap) || ap.ValueKind != JsonValueKind.False;
                        if (!string.IsNullOrWhiteSpace(targetUrl))
                        {
                            await ResolveAndLoadWebMediaAsync(
                                new DetectedMedia { Url = targetUrl, Title = targetUrl, Type = MediaStreamType.YtDlp },
                                autoPlay);
                        }
                    }
                    break;

                case "player-download-media":
                case "ytdlp-download":
                    if (root.TryGetProperty("url", out var urlProp))
                    {
                        string rawUrl = (urlProp.GetString() ?? string.Empty).Trim();
                        string title = root.TryGetProperty("title", out var titleProp) && !string.IsNullOrWhiteSpace(titleProp.GetString())
                            ? titleProp.GetString()!.Trim()
                            : (_currentMedia?.Title ?? _pendingMedia?.Title ?? "QUANGLN_Player_Video");
                        string quality = root.TryGetProperty("quality", out var qProp) ? (qProp.GetString() ?? "best") : "best";

                        if ((string.IsNullOrWhiteSpace(rawUrl) || rawUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) && _currentMedia is not null)
                        {
                            rawUrl = !string.IsNullOrWhiteSpace(_currentMedia.Url) ? _currentMedia.Url : _currentMedia.Referer;
                        }

                        if (!string.IsNullOrWhiteSpace(rawUrl))
                        {
                            AppLogger.Info($"[Tải Video] Bắt đầu tải video từ Player: '{title}' ({quality}) - {rawUrl}");
                            var mediaToDl = new DetectedMedia
                            {
                                Url = rawUrl,
                                Title = title,
                                QualityLabel = quality,
                                Referer = _currentMedia?.Referer ?? _pendingMedia?.Referer ?? string.Empty,
                                UserAgent = _currentMedia?.UserAgent ?? _pendingMedia?.UserAgent ?? string.Empty,
                                Cookie = _currentMedia?.Cookie ?? _pendingMedia?.Cookie ?? string.Empty,
                                Type = MediaStreamParser.IsYtDlpFirstPlatform(rawUrl) ? MediaStreamType.YtDlp : MediaStreamType.Direct
                            };

                            if (quality.Equals("audio", StringComparison.OrdinalIgnoreCase))
                            {
                                mediaToDl.FormatSelector = "bestaudio[ext=m4a]/bestaudio/best";
                                mediaToDl.MimeType = "audio/mp4";
                            }
                            else if (int.TryParse(quality.Replace("p", "").Trim(), out int parsedQualityHeight) && parsedQualityHeight > 0)
                            {
                                mediaToDl.FormatSelector = MediaStreamParser.BuildCompatibleMp4FormatSelector(parsedQualityHeight);
                            }
                            else
                            {
                                mediaToDl.FormatSelector = MediaStreamParser.BuildCompatibleMp4FormatSelector(null);
                            }

                            DownloadRequested?.Invoke(mediaToDl);
                        }
                    }
                    break;

                case "save-srt":
                    if (root.TryGetProperty("filename", out var fnProp) && root.TryGetProperty("content", out var contentProp))
                    {
                        string fileName = Path.GetFileName(fnProp.GetString() ?? "vietsub.srt");
                        string content = contentProp.GetString() ?? string.Empty;
                        string downloadsDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Downloads",
                            "QUANGLNBrowser");
                        Directory.CreateDirectory(downloadsDir);
                        string targetPath = Path.Combine(downloadsDir, fileName);
                        await File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
                    }
                    break;

                case "video-decode-fallback":
                    if (root.TryGetProperty("url", out var fbUrlProp))
                    {
                        string fbUrl = fbUrlProp.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(fbUrl) && File.Exists(AppPaths.GetNativeDllPath("libmpv-2.dll")))
                        {
                            var targetMedia = _currentMedia ?? new DetectedMedia { Url = fbUrl };
                            if (string.IsNullOrWhiteSpace(targetMedia.Title) && root.TryGetProperty("title", out var titleProp))
                            {
                                targetMedia.Title = titleProp.GetString() ?? string.Empty;
                            }
                            AppLogger.Info($"[MpvPlayerWindow] Chuyển đổi giải mã phần cứng libmpv cho: {targetMedia.Url}");
                            ActivateNativeMpvFallback(targetMedia);
                        }
                    }
                    break;

                case "mpv-hide":
                    HideNativeMpvOverlay();
                    break;

                case "mpv-set-paused":
                    if (VideoHost.IsMpvInitialized && root.TryGetProperty("paused", out var pausedProp))
                    {
                        VideoHost.SetPaused(pausedProp.ValueKind == JsonValueKind.True);
                    }
                    break;

                case "mpv-seek":
                    if (VideoHost.IsMpvInitialized && root.TryGetProperty("seconds", out var secProp) && secProp.TryGetDouble(out double sec))
                    {
                        VideoHost.SeekToSeconds(sec);
                    }
                    break;

                case "mpv-volume":
                    if (VideoHost.IsMpvInitialized)
                    {
                        if (root.TryGetProperty("volume", out var volProp) && volProp.TryGetDouble(out double vol))
                        {
                            VideoHost.SetVolume(vol);
                        }
                        if (root.TryGetProperty("muted", out var muteProp))
                        {
                            VideoHost.SetMuted(muteProp.ValueKind == JsonValueKind.True);
                        }
                    }
                    break;

                case "mpv-speed":
                    if (VideoHost.IsMpvInitialized && root.TryGetProperty("speed", out var spdProp) && spdProp.TryGetDouble(out double spd))
                    {
                        VideoHost.SetSpeed(spd);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("HandlePlayerCommandAsync failed", ex);
        }
    }

    private void ExecuteJs(string script)
    {
        try
        {
            if (Dispatcher.CheckAccess())
            {
                if (PlayerBrowser.CoreWebView2 is not null)
                {
                    _ = PlayerBrowser.CoreWebView2.ExecuteScriptAsync(script);
                }
            }
            else
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (PlayerBrowser.CoreWebView2 is not null)
                    {
                        _ = PlayerBrowser.CoreWebView2.ExecuteScriptAsync(script);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("ExecuteJs in MpvPlayerWindow failed", ex);
        }
    }

    private void OnTitleBarDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // Bỏ qua nếu chuột nhả nhanh
            }
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async Task LoadSubtitleFromFileAsync(string file)
    {
        try
        {
            AppLogger.Info($"[AI Vietsub] Đang nạp tệp phụ đề: {file}");
            string raw = await File.ReadAllTextAsync(file, Encoding.UTF8);
            var segments = SubtitleTranscriptService.ParseSubtitleContent(raw);
            if (segments.Count > 0 && _isPlayerDomReady && PlayerBrowser.CoreWebView2 is not null && !_isClosed)
            {
                // Kiểm tra xem phụ đề đã có tiếng Việt chưa
                bool hasVietnamese = segments.Take(30).Any(s =>
                    Regex.IsMatch(s.OriginalText, "[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ]", RegexOptions.IgnoreCase));

                if (!hasVietnamese)
                {
                    int maxTrans = Math.Min(segments.Count, 600);
                    SetPlayerLoading(true, $"Đang dịch AI {maxTrans} dòng phụ đề sang Tiếng Việt...");
                    var limited = segments.Take(maxTrans).ToList();
                    var sourceTexts = limited.Select(s => s.OriginalText).ToList();
                    var translatedMap = await _translationService.TranslateBatchAsync(sourceTexts, "vi", _windowCts.Token);
                    foreach (var s in limited)
                    {
                        if (translatedMap.TryGetValue(s.OriginalText, out string? tr) && !string.IsNullOrWhiteSpace(tr))
                        {
                            s.VietnameseText = tr;
                        }
                    }
                    SetPlayerLoading(false);
                    AppLogger.Info($"[AI Vietsub] Đã dịch AI xong {limited.Count} dòng phụ đề sang Tiếng Việt.");
                }

                var jsSegments = segments.Select(s => new
                {
                    start = s.StartSeconds,
                    end = s.EndSeconds,
                    speaker = s.Speaker,
                    gender = s.Gender,
                    en = s.OriginalText,
                    vi = s.EffectiveVietnameseText,
                    customVi = s.EffectiveVietnameseText
                });
                string segJson = JsonSerializer.Serialize(jsSegments);
                string nameJson = JsonSerializer.Serialize(Path.GetFileName(file));
                ExecuteJs($"window.__quanglnPlayerLoadSubtitles && window.__quanglnPlayerLoadSubtitles({segJson}, {nameJson});");
                AppLogger.Info($"[AI Vietsub] Nạp thành công {segments.Count} dòng phụ đề từ tệp {Path.GetFileName(file)} vào player.");
            }
        }
        catch (Exception ex)
        {
            SetPlayerLoading(false);
            AppLogger.Error("Failed to load subtitle file", ex);
        }
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (_isClosed || !e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] files ||
            files.Length == 0)
        {
            return;
        }

        foreach (string file in files)
        {
            string ext = Path.GetExtension(file);
            if (SupportedSubtitleExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                await LoadSubtitleFromFileAsync(file);
            }
            else if (SupportedVideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) || File.Exists(file))
            {
                PlayMedia(new DetectedMedia
                {
                    Url = file,
                    Title = Path.GetFileNameWithoutExtension(file),
                    QualityLabel = "Local HD"
                });
            }
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            e.Handled = true;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        PausePlayback();

        try
        {
            VideoHost.Stop();
            VideoHost.Dispose();
            NativeMpvContainer.Child = null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error disposing VideoHost in MpvPlayerWindow", ex);
        }

        _mpvTickTimer.Stop();

        try
        {
            _windowCts.Cancel();
        }
        catch { }

        _jobManager.Dispose();
        BrowserThemeManager.ThemeChanged -= OnGlobalThemeChanged;

        if (PlayerBrowser.CoreWebView2 is not null)
        {
            try
            {
                PlayerBrowser.CoreWebView2.IsMuted = true;
                PlayerBrowser.CoreWebView2.Stop();
                PlayerBrowser.CoreWebView2.Navigate("about:blank");
                PlayerBrowser.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }
            catch { }
        }

        PlayerBrowser.Dispose();
    }
}
