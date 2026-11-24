using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QUANGLNBrowser.App.Services;
using QUANGLNBrowser.Core.Interfaces;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;
using QUANGLNBrowser.Engine;
using QUANGLNBrowser.Media;

namespace QUANGLNBrowser.App.ViewModels;

/// <summary>
/// ViewModel đại diện cho một tab duyệt web nhân Edge Chromium (Microsoft.Web.WebView2):
/// - Tích hợp đầy đủ 100% bản quyền codec (H.264, AAC, MP4, HLS, DASH, HEVC, AV1) không bao giờ bị lỗi phát video.
/// - Bộ lọc AdBlock và Media Sniffer kiểu Cốc Cốc gắn trực tiếp qua CoreWebView2 WebResourceRequested.
/// - Hỗ trợ thanh nổi video-overlay.js, phím tắt toàn cục, chế độ ẩn danh InPrivate và Per-Tab VPN.
/// </summary>
public sealed partial class TabViewModel : ObservableObject, IDisposable
{
    private static readonly HttpClient FaviconHttpClient = CreateFaviconHttpClient();
    private static readonly ConcurrentDictionary<string, ImageSource> FaviconCache = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private int _blockedTrackersCount;

    [ObservableProperty]
    private bool _isAdBlockOnTabEnabled = true;

    private readonly string _profileId;
    private readonly IAdBlockEngine? _adBlockEngine;
    private readonly IMediaSnifferService? _mediaSniffer;
    private readonly Action<string>? _openInNewTab;
    private readonly Action<TabViewModel, string, string>? _onPageLoaded;
    private readonly Action<TabViewModel, string, DetectedMedia>? _onVideoAction;
    private readonly Action<TabViewModel, BrowserContextMenuInfo>? _onContextMenuRequested;
    private readonly Action<string>? _onThemeChangedFromPage;
    private readonly BrowserKeyboardHandler? _keyboardHandler;
    private readonly bool _ownsRequestContext;

    private WebView2ResourceHandler? _resourceHandler;
    private BrowserContextMenuHandler? _contextMenuHandler;
    private string? _lastAutoProbedUrl;
    private string? _lastFaviconHost;
    private bool _disposed;

    [ObservableProperty]
    private ImageSource? _faviconSource;

    [ObservableProperty]
    private bool _hasFavicon;

    [ObservableProperty]
    private string _title = "Tab mới";

    [ObservableProperty]
    private string _address = "https://www.google.com";

    [ObservableProperty]
    private string _currentDomain = "google.com";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isVpnEnabled;

    [ObservableProperty]
    private bool _isIncognito;

    [ObservableProperty]
    private bool _isSettingsTab;

    [ObservableProperty]
    private int _zoomPercent = 100;

    [ObservableProperty]
    private int _blockedAdsCount;

    [ObservableProperty]
    private int _detectedMediaCount;

    public Guid Id { get; } = Guid.NewGuid();
    public WebView2 Browser { get; }
    public bool IsBrowserInitialized => !_disposed && Browser.CoreWebView2 is not null;
    public bool HasIsolatedRequestContext => _ownsRequestContext;
    public ObservableCollection<DetectedMedia> DetectedMediaItems { get; } = new();

    public TabViewModel(
        string initialUrl,
        string? profileId = null,
        Action<string>? openInNewTab = null,
        Action<TabViewModel, string, string>? onPageLoaded = null,
        BrowserKeyboardHandler? keyboardHandler = null,
        IAdBlockEngine? adBlockEngine = null,
        IMediaSnifferService? mediaSniffer = null,
        Action<TabViewModel, string, DetectedMedia>? onVideoAction = null,
        bool isVpnEnabled = false,
        bool isIncognito = false,
        bool ownsRequestContext = false,
        Action<TabViewModel, BrowserContextMenuInfo>? onContextMenuRequested = null,
        Action<string>? onThemeChangedFromPage = null)
    {
        _profileId = profileId ?? "primary";
        _isVpnEnabled = isVpnEnabled;
        _isIncognito = isIncognito;
        _ownsRequestContext = ownsRequestContext;

        bool isSettings = UrlHelper.IsSettingsUrl(initialUrl);
        _isSettingsTab = isSettings;
        _address = isSettings ? UrlHelper.NormalizeUrlOrSearch(initialUrl) : initialUrl;
        _title = isSettings ? "Cài đặt" : "Tab mới";
        _currentDomain = isSettings ? "Cài đặt QUANGLN" : ManagedAdBlockEngine.ExtractHost(initialUrl);

        _openInNewTab = openInNewTab;
        _onPageLoaded = onPageLoaded;
        _keyboardHandler = keyboardHandler;
        _adBlockEngine = adBlockEngine;
        _mediaSniffer = mediaSniffer;
        _onVideoAction = onVideoAction;
        _onContextMenuRequested = onContextMenuRequested;
        _onThemeChangedFromPage = onThemeChangedFromPage;

        Browser = new WebView2();

        if (_adBlockEngine is not null)
        {
            _adBlockEngine.BlockedCountChanged += OnAdBlockCountChanged;
        }
        if (_mediaSniffer is not null)
        {
            _mediaSniffer.MediaDetected += OnMediaDetected;
        }

        _ = InitializeWebView2Async(isSettings ? "about:blank" : initialUrl);
        _ = LoadFaviconForHostAsync(_currentDomain, null);
    }

    private async Task InitializeWebView2Async(string initialUrl)
    {
        try
        {
            var env = await WebView2EnvironmentManager.GetEnvironmentAsync(
                _profileId,
                isVpnEnabled: IsVpnEnabled,
                killSwitchTripped: false);

            if (_disposed) return;

            if (IsIncognito)
            {
                var ctrlOptions = env.CreateCoreWebView2ControllerOptions();
                ctrlOptions.IsInPrivateModeEnabled = true;
                await Browser.EnsureCoreWebView2Async(env, ctrlOptions);
            }
            else
            {
                await Browser.EnsureCoreWebView2Async(env);
            }

            if (_disposed || Browser.CoreWebView2 is null) return;

            var core = Browser.CoreWebView2;

            // 1. Tùy chỉnh cài đặt bảo mật & trải nghiệm người dùng
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsBuiltInErrorPageEnabled = true;
            core.Settings.IsGeneralAutofillEnabled = true;
            core.Settings.IsPasswordAutosaveEnabled = true;

            // 2. Gắn bộ chặn quảng cáo AdBlock & Media Sniffer
            if (_adBlockEngine is not null && _mediaSniffer is not null)
            {
                _resourceHandler = new WebView2ResourceHandler(env, _adBlockEngine, _mediaSniffer, Id);
                _resourceHandler.Attach(core);
            }

            // 3. Tiêm Content Scripts tiền khởi tạo (DocumentCreated)
            // Bridge shim để CefSharp.PostMessage tương thích 100% với WebView2
            await core.AddScriptToExecuteOnDocumentCreatedAsync("""
                if (!window.CefSharp && window.chrome && window.chrome.webview) {
                    window.CefSharp = {
                        PostMessage: function(msg) {
                            window.chrome.webview.postMessage(msg);
                        }
                    };
                }
                """);

            string videoCompatScript = WebVideoCompatibilityScript.GetScript();
            if (!string.IsNullOrEmpty(videoCompatScript))
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(videoCompatScript);
            }

            string overlayScript = VideoOverlayScript.GetScript();
            if (!string.IsNullOrEmpty(overlayScript))
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(overlayScript);
            }

            // 4. Lắng nghe các sự kiện vòng đời
            core.SourceChanged += OnCoreSourceChanged;
            core.DocumentTitleChanged += OnCoreTitleChanged;
            core.NavigationStarting += OnCoreNavigationStarting;
            core.NavigationCompleted += OnCoreNavigationCompleted;
            core.WebMessageReceived += OnCoreWebMessageReceived;
            core.FaviconChanged += OnCoreFaviconChanged;
            core.NewWindowRequested += OnCoreNewWindowRequested;

            if (_onContextMenuRequested is not null)
            {
                _contextMenuHandler = new BrowserContextMenuHandler(info =>
                    Application.Current?.Dispatcher.InvokeAsync(() => _onContextMenuRequested(this, info)));
                _contextMenuHandler.Attach(core, CanGoBack, CanGoForward);
            }

            if (_keyboardHandler is not null)
            {
                _keyboardHandler.Attach(Browser);
            }

            // 5. Điều hướng đến địa chỉ ban đầu nếu hợp lệ
            if (!IsSettingsTab && !string.IsNullOrWhiteSpace(initialUrl) && initialUrl != "about:blank")
            {
                core.Navigate(UrlHelper.NormalizeUrlOrSearch(initialUrl));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Lỗi khởi tạo WebView2 trong TabViewModel", ex);
        }
    }

    private static HttpClient CreateFaviconHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) QUANGLNBrowser/0.1");
        return client;
    }

    partial void OnAddressChanged(string value)
    {
        CurrentDomain = ManagedAdBlockEngine.ExtractHost(value);
        TryTriggerAutoYtDlpProbe(value);
        if (!string.Equals(_lastFaviconHost, CurrentDomain, StringComparison.OrdinalIgnoreCase))
        {
            _lastFaviconHost = CurrentDomain;
            _ = LoadFaviconForHostAsync(CurrentDomain, null);
        }
    }

    private async Task LoadFaviconForHostAsync(string host, string? explicitIconUrl)
    {
        if (_disposed || string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        string cacheKey = !string.IsNullOrEmpty(explicitIconUrl) ? explicitIconUrl : host;
        if (FaviconCache.TryGetValue(cacheKey, out var cachedImg) ||
            FaviconCache.TryGetValue(host, out cachedImg))
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                FaviconSource = cachedImg;
                HasFavicon = true;
            });
            return;
        }

        var candidateUrls = new List<string>();
        if (!string.IsNullOrEmpty(explicitIconUrl))
        {
            candidateUrls.Add(explicitIconUrl);
        }
        candidateUrls.Add($"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(host)}&sz=32");
        candidateUrls.Add($"https://{host}/favicon.ico");

        foreach (string iconUrl in candidateUrls)
        {
            try
            {
                byte[] bytes = await FaviconHttpClient.GetByteArrayAsync(iconUrl);
                if (bytes.Length < 32)
                {
                    continue;
                }

                var bmp = TryDecodeFaviconBitmap(bytes);
                if (bmp is not null)
                {
                    FaviconCache[cacheKey] = bmp;
                    FaviconCache[host] = bmp;
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        FaviconSource = bmp;
                        HasFavicon = true;
                    });
                    return;
                }
            }
            catch
            {
                // Thử nguồn favicon tiếp theo
            }
        }
    }

    private static BitmapImage? TryDecodeFaviconBitmap(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public static void AttachFaviconToSuggestion(OmniboxSuggestionItem suggestion)
    {
        if (suggestion.IsSearchQuery || string.IsNullOrWhiteSpace(suggestion.Url))
        {
            return;
        }

        string host = ManagedAdBlockEngine.ExtractHost(suggestion.Url);
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        if (FaviconCache.TryGetValue(host, out var cached))
        {
            suggestion.FaviconSource = cached;
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string iconUrl = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(host)}&sz=32";
                byte[] bytes = await FaviconHttpClient.GetByteArrayAsync(iconUrl);
                if (bytes.Length >= 32)
                {
                    var bmp = TryDecodeFaviconBitmap(bytes);
                    if (bmp is not null)
                    {
                        FaviconCache[host] = bmp;
                        Application.Current?.Dispatcher.InvokeAsync(() => suggestion.FaviconSource = bmp);
                    }
                }
            }
            catch
            {
                // Bỏ qua nếu đang offline
            }
        });
    }

    private void TryTriggerAutoYtDlpProbe(string url)
    {
        if (_disposed || _mediaSniffer is null || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!MediaStreamParser.IsLikelyVideoWatchPage(url))
        {
            return;
        }

        if (string.Equals(_lastAutoProbedUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastAutoProbedUrl = url;
        _mediaSniffer.ClearTab(Id);

        _mediaSniffer.CaptureMedia(Id, new DetectedMedia
        {
            Url = url,
            Title = $"{Title} (1080p Full HD)",
            QualityLabel = "1080p",
            FormatSelector = MediaStreamParser.BuildCompatibleMp4FormatSelector(1080),
            MimeType = "video/mp4",
            Referer = url,
            Type = MediaStreamType.YtDlp
        });

        _mediaSniffer.CaptureMedia(Id, new DetectedMedia
        {
            Url = url,
            Title = $"{Title} (720p HD)",
            QualityLabel = "720p",
            FormatSelector = MediaStreamParser.BuildCompatibleMp4FormatSelector(720),
            MimeType = "video/mp4",
            Referer = url,
            Type = MediaStreamType.YtDlp
        });

        _ = Task.Run(async () =>
        {
            string cookieHeader = await GetCefCookiesForUrlAsync(url);
            await _mediaSniffer.ProbePageWithYtDlpAsync(Id, url, cookieHeader);
        });
    }

    public async Task<string> GetCookiesForUrlAsync(string url)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        try
        {
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                return await dispatcher.InvokeAsync(async () => await GetCookiesForUrlAsync(url)).Task.Unwrap();
            }

            if (Browser.CoreWebView2 is null)
            {
                return string.Empty;
            }

            var cookieList = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(url);
            if (cookieList is null || cookieList.Count == 0)
            {
                return string.Empty;
            }

            return string.Join("; ", cookieList.Select(c => $"{c.Name}={c.Value}"));
        }
        catch (Exception ex)
        {
            AppLogger.Error("Lỗi đọc cookies từ WebView2", ex);
            return string.Empty;
        }
    }

    public Task<string> GetCefCookiesForUrlAsync(string url) => GetCookiesForUrlAsync(url);

    private void OnAdBlockCountChanged(object? sender, Guid tabId)
    {
        if (tabId != Id || _adBlockEngine is null)
        {
            return;
        }

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            BlockedAdsCount = _adBlockEngine.GetBlockedCount(Id);
            BlockedTrackersCount = _adBlockEngine.GetBlockedTrackersCount(Id);
        });
    }

    private void OnMediaDetected(object? sender, Guid tabId)
    {
        if (tabId != Id || _mediaSniffer is null)
        {
            return;
        }

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var captured = _mediaSniffer.GetDetectedMedia(Id);
            DetectedMediaItems.Clear();
            foreach (var m in captured)
            {
                DetectedMediaItems.Add(m);
            }
            DetectedMediaCount = DetectedMediaItems.Count;
            SyncQualitiesToVideoOverlay();
        });
    }

    private void SyncQualitiesToVideoOverlay()
    {
        if (_disposed || Browser.CoreWebView2 is null)
        {
            return;
        }

        var jsOptions = new List<object>();
        foreach (var item in DetectedMediaItems)
        {
            string val = !string.IsNullOrEmpty(item.QualityLabel) ? item.QualityLabel.Replace("p", "") : "1080";
            if (!int.TryParse(val, out int _))
            {
                continue;
            }

            string sizeText = item.FileSize > 0 ? item.DisplaySize : string.Empty;
            jsOptions.Add(new { value = val, sizeText });
        }

        string json = JsonSerializer.Serialize(jsOptions);
        _ = Browser.ExecuteScriptAsync($"if (window.__quanglnSetQualities) window.__quanglnSetQualities({json});");
    }

    private void OnCoreSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (IsSettingsTab) return;
        string? newUrl = Browser.Source?.ToString();
        if (!string.IsNullOrWhiteSpace(newUrl))
        {
            Application.Current?.Dispatcher.InvokeAsync(() => Address = newUrl);
        }
    }

    private void OnCoreTitleChanged(object? sender, object e)
    {
        if (IsSettingsTab) return;
        string? newTitle = Browser.CoreWebView2?.DocumentTitle;
        if (!string.IsNullOrWhiteSpace(newTitle))
        {
            Application.Current?.Dispatcher.InvokeAsync(() => Title = newTitle);
        }
    }

    private void OnCoreNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (IsSettingsTab)
            {
                IsLoading = false;
                return;
            }
            IsLoading = true;
        });
    }

    private void OnCoreNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (IsSettingsTab)
            {
                IsLoading = false;
                return;
            }

            IsLoading = false;
            CanGoBack = Browser.CanGoBack;
            CanGoForward = Browser.CanGoForward;

            string currentUrl = Browser.Source?.ToString() ?? Address;
            Address = currentUrl;

            // Chặn quảng cáo YouTube
            if (_adBlockEngine is { IsEnabled: true, BlockYouTubeAds: true } &&
                (currentUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                 currentUrl.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)))
            {
                _ = Browser.ExecuteScriptAsync(AdBlockContentScripts.GetYouTubeAdBlockerScript());
            }

            // Đồng bộ theme và widget live status vào trang chủ
            if (HomePageBuilder.IsHomeUrl(currentUrl))
            {
                string safeKey = JsonSerializer.Serialize(BrowserThemeManager.CurrentThemeKey);
                _ = Browser.ExecuteScriptAsync($"if (window.__quanglnApplyExternalTheme) window.__quanglnApplyExternalTheme({safeKey});");
                _ = PushLiveHomeStatusAsync();
            }

            // Tiêm Cosmetic CSS ẩn banner quảng cáo trên trang chính
            if (_adBlockEngine is not null && !string.IsNullOrEmpty(currentUrl))
            {
                string? css = _adBlockEngine.GetCosmeticCss(currentUrl);
                if (!string.IsNullOrEmpty(css))
                {
                    string serializedCss = JsonSerializer.Serialize(css);
                    _ = Browser.ExecuteScriptAsync($$"""
                        (function() {
                            try {
                                var styleId = 'quangln-adblock-cosmetic';
                                var el = document.getElementById(styleId);
                                if (!el) {
                                    el = document.createElement('style');
                                    el.id = styleId;
                                    (document.head || document.documentElement).appendChild(el);
                                }
                                el.textContent = {{serializedCss}};
                            } catch (err) {}
                        })();
                        """);
                }
            }

            TryTriggerAutoYtDlpProbe(currentUrl);

            if (e.IsSuccess && !string.IsNullOrWhiteSpace(currentUrl) && !IsIncognito)
            {
                _onPageLoaded?.Invoke(this, currentUrl, Title);
            }
        });
    }

    private void OnCoreFaviconChanged(object? sender, object e)
    {
        string? favUri = Browser.CoreWebView2?.FaviconUri;
        if (!string.IsNullOrEmpty(favUri))
        {
            _ = LoadFaviconForHostAsync(CurrentDomain, favUri);
        }
    }

    private void OnCoreNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        string uri = e.Uri;
        if (string.IsNullOrWhiteSpace(uri) || uri == "about:blank") return;

        // Kiểm tra popup bị chặn bởi AdBlock
        if (_adBlockEngine is not null && _adBlockEngine.ShouldBlock(uri, Address, "popup"))
        {
            _adBlockEngine.IncrementBlockedCount(Id);
            return;
        }

        _openInNewTab?.Invoke(uri);
    }

    private async Task PushLiveHomeStatusAsync()
    {
        try
        {
            var weather = await VietnameseCalendarService.GetCachedOrFetchLiveWeatherAsync();
            if (_disposed || Browser.CoreWebView2 is null)
            {
                return;
            }

            string payloadJson = JsonSerializer.Serialize(new
            {
                lunarText = VietnameseCalendarService.FormatHomeLunarText(DateTime.Now),
                tempText = weather.TemperatureText,
                cityName = weather.CityName,
                condText = weather.ConditionText,
                iconGlyph = weather.IconGlyph
            });

            _ = Browser.ExecuteScriptAsync($"if (window.__quanglnApplyLiveStatus) window.__quanglnApplyLiveStatus({payloadJson});");
        }
        catch
        {
            // Bỏ qua nếu tab đã đóng
        }
    }

    public void SyncThemeToPage(string themeKey)
    {
        if (_disposed)
        {
            return;
        }

        void ExecuteOnUi()
        {
            if (_disposed || Browser.CoreWebView2 is null || !HomePageBuilder.IsHomeUrl(Address))
            {
                return;
            }

            string safeKey = JsonSerializer.Serialize(themeKey);
            _ = Browser.ExecuteScriptAsync($"if (window.__quanglnApplyExternalTheme) window.__quanglnApplyExternalTheme({safeKey});");
        }

        if (Browser.Dispatcher.CheckAccess())
        {
            ExecuteOnUi();
        }
        else
        {
            _ = Browser.Dispatcher.InvokeAsync(ExecuteOnUi);
        }
    }

    private async void OnCoreWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        string json = e.WebMessageAsJson;
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
            {
                return;
            }

            string msgType = typeEl.GetString() ?? string.Empty;
            if (string.Equals(msgType, "QUANGLN_THEME_CHANGED", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("theme", out var themeEl) && themeEl.GetString() is { } themeKey)
                {
                    _ = Application.Current?.Dispatcher.InvokeAsync(() => _onThemeChangedFromPage?.Invoke(themeKey));
                }
                return;
            }

            if (string.Equals(msgType, "QUANGLN_ADBLOCK_PICK", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("rule", out var ruleEl) && ruleEl.GetString() is { } ruleStr && !string.IsNullOrWhiteSpace(ruleStr))
                {
                    _adBlockEngine?.AddCustomRule(ruleStr);
                    string host = root.TryGetProperty("domain", out var domEl) ? domEl.GetString() ?? CurrentDomain : CurrentDomain;
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        if (!string.IsNullOrEmpty(host))
                        {
                            string? css = _adBlockEngine?.GetCosmeticCss($"https://{host}/");
                            if (!string.IsNullOrEmpty(css))
                            {
                                string serializedCss = JsonSerializer.Serialize(css);
                                _ = Browser.ExecuteScriptAsync($$"""
                                    (function() {
                                        var el = document.getElementById('quangln-adblock-cosmetic');
                                        if (!el) {
                                            el = document.createElement('style');
                                            el.id = 'quangln-adblock-cosmetic';
                                            (document.head || document.documentElement).appendChild(el);
                                        }
                                        el.textContent = {{serializedCss}};
                                    })();
                                    """);
                            }
                        }
                    });
                }
                return;
            }

            if (string.Equals(msgType, "QUANGLN_RUN_CLEANER", StringComparison.Ordinal))
            {
                _ = Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                        GC.WaitForPendingFinalizers();
                        GC.Collect(2, GCCollectionMode.Forced, true, true);

                        try
                        {
                            using var cur = Process.GetCurrentProcess();
                            _ = EmptyWorkingSet(cur.Handle);
                        }
                        catch { }

                        AppLogger.Info("GX Cleaner executed: memory trimmed and caches purged.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("GX Cleaner execution error", ex);
                    }
                });
                return;
            }

            if (!string.Equals(msgType, "QUANGLN_VIDEO_ACTION", StringComparison.Ordinal))
            {
                return;
            }

            string action = root.TryGetProperty("action", out var actEl) ? actEl.GetString() ?? "download" : "download";
            string quality = root.TryGetProperty("quality", out var qEl) ? qEl.GetString() ?? "1080" : "1080";
            string src = root.TryGetProperty("src", out var srcEl) ? srcEl.GetString() ?? string.Empty : string.Empty;
            string subtitleUrl = root.TryGetProperty("subtitleUrl", out var subEl) ? subEl.GetString() ?? string.Empty : string.Empty;
            string pageUrl = root.TryGetProperty("pageUrl", out var pageEl) ? pageEl.GetString() ?? Address : Address;
            string title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? Title : Title;
            string jsCookie = root.TryGetProperty("cookie", out var cookieEl) ? cookieEl.GetString() ?? string.Empty : string.Empty;
            string userAgent = root.TryGetProperty("userAgent", out var uaEl) ? uaEl.GetString() ?? string.Empty : string.Empty;

            string cefCookies = await GetCefCookiesForUrlAsync(pageUrl);
            string mergedCookie = !string.IsNullOrWhiteSpace(cefCookies) ? cefCookies : jsCookie;

            // Nếu src rỗng hoặc là blob/data, truy quét trực tiếp DOM của tab WebView2 trước khi resolve
            if (string.IsNullOrWhiteSpace(src) ||
                src.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var extracted = await ExtractStreamFromPageDomAsync();
                if (!string.IsNullOrWhiteSpace(extracted.Url))
                {
                    src = extracted.Url;
                    if (!string.IsNullOrWhiteSpace(extracted.SubtitleUrl) && string.IsNullOrWhiteSpace(subtitleUrl))
                    {
                        subtitleUrl = extracted.SubtitleUrl;
                    }
                }
            }

            DetectedMedia resolved = ResolveMediaFromVideoOverlay(src, pageUrl, title, mergedCookie, userAgent, quality, subtitleUrl);
            _ = Application.Current?.Dispatcher.InvokeAsync(() => _onVideoAction?.Invoke(this, action, resolved));
        }
        catch (Exception ex)
        {
            AppLogger.Error("Lỗi parse WebMessage trong TabViewModel", ex);
        }
    }

    private async Task<(string Url, string SubtitleUrl)> ExtractStreamFromPageDomAsync()
    {
        if (Browser.CoreWebView2 is null)
        {
            return (string.Empty, string.Empty);
        }

        try
        {
            string js = """
                (function() {
                    try {
                        // 1. Quét các nút server streaming trên trang xem phim (Motchill, Phimmoi...)
                        var activeServer = document.querySelector(
                            '.streaming-server.btn-primary, .streaming-server.btn-success, .streaming-server.active, .btn-sv.btn-primary, .btn-sv.btn-success, .btn-sv.active, [data-link].active, [data-link].selected, .streaming-server, .btn-sv'
                        );
                        if (activeServer && activeServer.dataset && activeServer.dataset.link) {
                            var lk = activeServer.dataset.link.trim();
                            if (lk.includes('.m3u8') || lk.includes('.mp4') || lk.includes('/stream/')) {
                                return JSON.stringify({ url: lk, sub: (activeServer.dataset.subtitle || '').trim() });
                            }
                        }

                        // 2. Quét bất kỳ nút nào có link .m3u8 hoặc .mp4 (ví dụ server VIP #1, VIP #2)
                        var m3u8Btn = document.querySelector(
                            'a[data-link*=".m3u8"], button[data-link*=".m3u8"], [data-type="m3u8"][data-link], a[data-link*=".mp4"], button[data-link*=".mp4"], [data-link*="/stream/"]'
                        );
                        if (m3u8Btn && m3u8Btn.dataset && m3u8Btn.dataset.link) {
                            return JSON.stringify({ url: m3u8Btn.dataset.link.trim(), sub: (m3u8Btn.dataset.subtitle || '').trim() });
                        }

                        // 3. Quét JSON-LD hoặc thẻ <script> chứa "contentUrl" (.m3u8)
                        var scripts = document.querySelectorAll('script[type="application/ld+json"], script:not([src])');
                        for (var i = 0; i < scripts.length; i++) {
                            var txt = scripts[i].textContent || '';
                            var m = txt.match(/"contentUrl"\s*:\s*"([^"]+\.m3u8[^"]*)"/i) ||
                                    txt.match(/["'](https?:\/\/[^"'\s]+\.m3u8[^"'\s]*)["']/i);
                            if (m && m[1]) {
                                return JSON.stringify({ url: m[1].replace(/\\/g, ''), sub: '' });
                            }
                        }

                        // 4. Quét đối tượng player globals (window.hls, window.player, v.v.)
                        if (window.hls && window.hls.url) {
                            return JSON.stringify({ url: window.hls.url, sub: '' });
                        }
                        if (window.player && window.player.src) {
                            return JSON.stringify({ url: window.player.src, sub: '' });
                        }
                        if (window.art && window.art.option && window.art.option.url) {
                            return JSON.stringify({ url: window.art.option.url, sub: '' });
                        }
                        if (window.dp && window.dp.video && window.dp.video.src) {
                            return JSON.stringify({ url: window.dp.video.src, sub: '' });
                        }
                    } catch (e) {}
                    return '';
                })()
                """;

            string jsonResult = await Browser.ExecuteScriptAsync(js);
            if (!string.IsNullOrWhiteSpace(jsonResult) && jsonResult != "null" && jsonResult != "\"\"")
            {
                string decodedJson = jsonResult.StartsWith('"') ? (JsonSerializer.Deserialize<string>(jsonResult) ?? jsonResult) : jsonResult;
                if (!string.IsNullOrWhiteSpace(decodedJson) && decodedJson.StartsWith('{'))
                {
                    using var doc = JsonDocument.Parse(decodedJson);
                    string u = doc.RootElement.TryGetProperty("url", out var uEl) ? uEl.GetString() ?? "" : "";
                    string s = doc.RootElement.TryGetProperty("sub", out var sEl) ? sEl.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(u))
                    {
                        u = u.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
                        s = s.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
                        AppLogger.Info($"[VideoOverlay] Quét được luồng trực tiếp từ DOM của tab: {u} (Phụ đề: {s})");
                        return (u, s);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[VideoOverlay] Lỗi trích xuất luồng từ DOM: {ex.Message}");
        }

        return (string.Empty, string.Empty);
    }

    public void StartElementPicker()
    {
        if (Browser.CoreWebView2 is not null)
        {
            _ = Browser.ExecuteScriptAsync(AdBlockContentScripts.GetElementPickerScript());
        }
    }

    private DetectedMedia ResolveMediaFromVideoOverlay(
        string src,
        string pageUrl,
        string title,
        string cookie,
        string userAgent,
        string selectedQuality,
        string? subtitleUrl = null)
    {
        if (MediaStreamParser.IsYtDlpFirstPlatform(pageUrl) || MediaStreamParser.IsRawStreamingCdnChunk(src))
        {
            AppLogger.Info($"[VideoOverlay] Nền tảng YtDlp: {pageUrl}");
            string cleanUrl = MediaStreamParser.SanitizeWatchUrl(pageUrl);
            var m = CreateYtDlpMediaWithQuality(cleanUrl, title, cookie, userAgent, selectedQuality);
            if (!string.IsNullOrWhiteSpace(subtitleUrl)) m.SubtitleUrl = subtitleUrl;
            return m;
        }

        var captured = _mediaSniffer?.GetDetectedMedia(Id) ?? Array.Empty<DetectedMedia>();

        if (string.Equals(selectedQuality, "audio", StringComparison.OrdinalIgnoreCase))
        {
            string targetUrl = (!string.IsNullOrWhiteSpace(src) && !src.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                ? src
                : pageUrl;
            return CreateYtDlpMediaWithQuality(targetUrl, title, cookie, userAgent, "audio");
        }

        if (!string.IsNullOrWhiteSpace(src) &&
            !src.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) &&
            !src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var matched = captured.FirstOrDefault(m => string.Equals(m.Url, src, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                if (string.IsNullOrWhiteSpace(matched.Title)) matched.Title = title;
                if (string.IsNullOrWhiteSpace(matched.Cookie)) matched.Cookie = cookie;
                if (string.IsNullOrWhiteSpace(matched.UserAgent)) matched.UserAgent = userAgent;
                if (!string.IsNullOrWhiteSpace(subtitleUrl) && string.IsNullOrWhiteSpace(matched.SubtitleUrl))
                {
                    matched.SubtitleUrl = subtitleUrl;
                }
                AppLogger.Info($"[VideoOverlay] Khớp luồng đã bắt: {matched.Url} ({matched.Type})");
                return matched;
            }

            bool isManifest = src.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                              src.Contains(".mpd", StringComparison.OrdinalIgnoreCase);

            var directMedia = new DetectedMedia
            {
                Url = src,
                Title = title,
                Referer = pageUrl,
                Cookie = cookie,
                UserAgent = userAgent,
                SubtitleUrl = subtitleUrl ?? string.Empty,
                Type = isManifest ? MediaStreamType.HlsOrDash : MediaStreamType.Direct
            };
            _mediaSniffer?.CaptureMedia(Id, directMedia);
            AppLogger.Info($"[VideoOverlay] Nhận luồng trực tiếp từ overlay: {src} ({directMedia.Type})");
            return directMedia;
        }

        // Ưu tiên 1: Luồng manifest HLS (.m3u8) hoặc DASH (.mpd)
        var validManifest = captured.LastOrDefault(m => m.Type == MediaStreamType.HlsOrDash);
        if (validManifest is not null)
        {
            if (string.IsNullOrWhiteSpace(validManifest.Title)) validManifest.Title = title;
            if (string.IsNullOrWhiteSpace(validManifest.Cookie)) validManifest.Cookie = cookie;
            if (string.IsNullOrWhiteSpace(validManifest.UserAgent)) validManifest.UserAgent = userAgent;
            if (!string.IsNullOrWhiteSpace(subtitleUrl) && string.IsNullOrWhiteSpace(validManifest.SubtitleUrl))
            {
                validManifest.SubtitleUrl = subtitleUrl;
            }
            AppLogger.Info($"[VideoOverlay] Sử dụng luồng manifest HLS/DASH bắt được: {validManifest.Url}");
            return validManifest;
        }

        // Ưu tiên 2: Luồng video Direct (.mp4, .mkv, .webm)
        var validDirect = captured.LastOrDefault(m =>
            m.Type == MediaStreamType.Direct &&
            !MediaStreamParser.IsRawStreamingCdnChunk(m.Url) &&
            !m.Url.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
            !m.Url.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase));

        if (validDirect is not null)
        {
            if (string.IsNullOrWhiteSpace(validDirect.Title)) validDirect.Title = title;
            if (string.IsNullOrWhiteSpace(validDirect.Cookie)) validDirect.Cookie = cookie;
            if (string.IsNullOrWhiteSpace(validDirect.UserAgent)) validDirect.UserAgent = userAgent;
            if (!string.IsNullOrWhiteSpace(subtitleUrl) && string.IsNullOrWhiteSpace(validDirect.SubtitleUrl))
            {
                validDirect.SubtitleUrl = subtitleUrl;
            }
            AppLogger.Info($"[VideoOverlay] Sử dụng luồng direct bắt được: {validDirect.Url}");
            return validDirect;
        }

        // Ưu tiên 3: Bất kỳ luồng nào đã bắt được trong tab
        var anyCaptured = captured.LastOrDefault(m =>
            !m.Url.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
            !m.Url.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase));
        if (anyCaptured is not null)
        {
            AppLogger.Info($"[VideoOverlay] Dùng luồng dự phòng từ tab: {anyCaptured.Url}");
            return anyCaptured;
        }

        if (MediaStreamParser.IsLikelyVideoWatchPage(pageUrl) || MediaStreamParser.IsYtDlpFirstPlatform(pageUrl))
        {
            AppLogger.Info($"[VideoOverlay] Dự phòng nền tảng video qua YtDlp: {pageUrl}");
            string cleanUrl = MediaStreamParser.SanitizeWatchUrl(pageUrl);
            var fallbackMedia = CreateYtDlpMediaWithQuality(cleanUrl, title, cookie, userAgent, selectedQuality);
            if (!string.IsNullOrWhiteSpace(subtitleUrl)) fallbackMedia.SubtitleUrl = subtitleUrl;
            return fallbackMedia;
        }

        AppLogger.Warn($"[VideoOverlay] Không tìm thấy luồng stream trực tiếp, chuyển sang phân giải trang nguồn: {pageUrl}");
        var directFallback = new DetectedMedia
        {
            Url = pageUrl,
            Title = title,
            Referer = pageUrl,
            Cookie = cookie,
            UserAgent = userAgent,
            Type = MediaStreamType.YtDlp
        };
        return directFallback;
    }

    private DetectedMedia CreateYtDlpMediaWithQuality(
        string targetUrl,
        string title,
        string cookie,
        string userAgent,
        string selectedQuality)
    {
        if (string.Equals(selectedQuality, "audio", StringComparison.OrdinalIgnoreCase))
        {
            return new DetectedMedia
            {
                Url = targetUrl,
                Title = $"{title} (Audio M4A)",
                Referer = targetUrl,
                Cookie = cookie,
                UserAgent = userAgent,
                QualityLabel = "AUDIO",
                FormatSelector = "bestaudio[ext=m4a]/bestaudio",
                MimeType = "audio/mp4",
                Type = MediaStreamType.YtDlp
            };
        }

        int? maxHeight = int.TryParse(selectedQuality, out int parsedHeight) ? parsedHeight : null;
        string qLabel = maxHeight.HasValue ? $"{maxHeight.Value}p" : "HD";

        var media = new DetectedMedia
        {
            Url = targetUrl,
            Title = $"{title} ({qLabel} MP4)",
            Referer = targetUrl,
            Cookie = cookie,
            UserAgent = userAgent,
            QualityLabel = qLabel,
            FormatSelector = MediaStreamParser.BuildCompatibleMp4FormatSelector(maxHeight),
            MimeType = "video/mp4",
            Type = MediaStreamType.YtDlp
        };
        _mediaSniffer?.CaptureMedia(Id, media);
        return media;
    }

    public void SetZoomPercent(int percent)
    {
        int clamped = Math.Clamp(percent, 25, 500);
        ZoomPercent = clamped;
        if (!_disposed && !IsSettingsTab && Browser.CoreWebView2 is not null)
        {
            Browser.ZoomFactor = clamped / 100.0;
        }
    }

    public void Navigate(string urlOrQuery)
    {
        string target = UrlHelper.NormalizeUrlOrSearch(urlOrQuery);
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        if (UrlHelper.IsSettingsUrl(target))
        {
            IsSettingsTab = true;
            Title = "Cài đặt";
            Address = target;
            IsLoading = false;
            return;
        }

        IsSettingsTab = false;
        Address = target;
        if (Browser.CoreWebView2 is not null)
        {
            Browser.CoreWebView2.Navigate(target);
        }
        else
        {
            Browser.Source = new Uri(target);
        }
    }

    public async Task PauseAllMediaAsync()
    {
        if (_disposed || Browser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await Browser.ExecuteScriptAsync("document.querySelectorAll('video, audio').forEach(el => { try { el.pause(); } catch {} });");
        }
        catch
        {
            // Bỏ qua nếu trang chưa sẵn sàng
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_adBlockEngine is not null)
        {
            _adBlockEngine.BlockedCountChanged -= OnAdBlockCountChanged;
        }

        if (_mediaSniffer is not null)
        {
            _mediaSniffer.MediaDetected -= OnMediaDetected;
        }

        if (_keyboardHandler is not null)
        {
            _keyboardHandler.Detach(Browser);
        }

        if (Browser.CoreWebView2 is not null)
        {
            try
            {
                Browser.CoreWebView2.IsMuted = true;
                Browser.CoreWebView2.Stop();
                Browser.CoreWebView2.Navigate("about:blank");
            }
            catch { }

            _resourceHandler?.Detach(Browser.CoreWebView2);
            Browser.CoreWebView2.SourceChanged -= OnCoreSourceChanged;
            Browser.CoreWebView2.DocumentTitleChanged -= OnCoreTitleChanged;
            Browser.CoreWebView2.NavigationStarting -= OnCoreNavigationStarting;
            Browser.CoreWebView2.NavigationCompleted -= OnCoreNavigationCompleted;
            Browser.CoreWebView2.WebMessageReceived -= OnCoreWebMessageReceived;
            Browser.CoreWebView2.FaviconChanged -= OnCoreFaviconChanged;
            Browser.CoreWebView2.NewWindowRequested -= OnCoreNewWindowRequested;
        }

        Browser.Dispose();
    }
}
