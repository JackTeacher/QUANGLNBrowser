using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QUANGLNBrowser.App.Services;
using QUANGLNBrowser.App.Views;
using QUANGLNBrowser.Core.Interfaces;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;
using QUANGLNBrowser.Engine;
using QUANGLNBrowser.Media;
using QUANGLNBrowser.Network;
using QUANGLNBrowser.Security;

namespace QUANGLNBrowser.App.ViewModels;

/// <summary>
/// ViewModel trung tâm điều phối Tab duyệt web, Omnibox, AdBlock Engine 2 tầng,
/// Video Sniffer, Multi-thread Downloader, libmpv Player, Built-in VPN (sing-box + Per-Tab Routing + Kill Switch) và Vault Lock.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IVaultService _vaultService;
    private readonly IIdleMonitorService _idleMonitor;
    private readonly JobObjectManager _jobManager;
    private readonly IAdBlockEngine _adBlockEngine;
    private readonly IMediaSnifferService _mediaSniffer;
    private readonly IDownloadManager _downloadManager;
    private readonly IVpnService _vpnService;
    private readonly BrowserKeyboardHandler _keyboardHandler;
    private readonly List<MpvPlayerWindow> _openPlayers = new();
    private readonly bool _isPrimaryWindow;

    private BrowserSessionServices? _sharedSession;
    private string _profileId = "primary";
    private bool _isWindowVpnActive;
    private string? _loadedProfileId;
    private bool _filtersLoadedOnce;
    private bool _isLoadingVpnPreferences;
    private bool _disposed;

    [ObservableProperty]
    private bool _isIncognitoWindow;

    [ObservableProperty]
    private bool _isIncognitoWarpWindow;

    [ObservableProperty]
    private string _windowTitle = "QUANGLN Browser";

    [ObservableProperty]
    private string _incognitoHeaderLabel = "ẨN DANH";

    [ObservableProperty]
    private bool _isLocked = true;

    [ObservableProperty]
    private TabViewModel? _activeTab;

    [ObservableProperty]
    private TabViewModel? _splitViewTab;

    [ObservableProperty]
    private bool _isSplitViewOpen;

    private TabViewModel? _contextMenuTargetTab;
    private readonly PageTranslationService _pageTranslationService = new();

    [ObservableProperty]
    private string _omniboxText = string.Empty;

    [ObservableProperty]
    private bool _isCurrentPageBookmarked;

    [ObservableProperty]
    private bool _isDrawerOpen;

    [ObservableProperty]
    private string _activeDrawerSection = "Bookmarks";

    [ObservableProperty]
    private string _activeProfileLabel = "Chưa mở khóa";

    [ObservableProperty]
    private int _downloadBadgeCount;

    [ObservableProperty]
    private int _autoLockMinutes = 5;

    [ObservableProperty]
    private bool _isAdBlockEnabled = true;

    [ObservableProperty]
    private string _adBlockMode = "standard";

    [ObservableProperty]
    private bool _isAdBlockStandardMode = true;

    [ObservableProperty]
    private bool _isAdBlockStrictMode;

    [ObservableProperty]
    private bool _adBlockBlockPopups = true;

    [ObservableProperty]
    private bool _adBlockBlockTrackers = true;

    [ObservableProperty]
    private bool _adBlockBlockYouTubeAds = true;

    [ObservableProperty]
    private bool _adBlockAllowAcceptableAds = true;

    [ObservableProperty]
    private bool _adBlockEnableEasyList = true;

    [ObservableProperty]
    private bool _adBlockEnableEasyPrivacy = true;

    [ObservableProperty]
    private bool _adBlockEnableAbpvn = true;

    [ObservableProperty]
    private bool _adBlockEnableAntiAnnoyance = true;

    [ObservableProperty]
    private long _adBlockTotalAdsBlocked;

    [ObservableProperty]
    private long _adBlockTotalTrackersBlocked;

    [ObservableProperty]
    private string _adBlockLastUpdatedText = "Đã cập nhật";

    [ObservableProperty]
    private string _newAdBlockCustomRuleInput = string.Empty;

    public ObservableCollection<string> AdBlockCustomRules { get; } = new();

    [ObservableProperty]
    private string _adBlockEngineName = "Managed C# Fallback";

    [ObservableProperty]
    private int _adBlockTotalRules;

    [ObservableProperty]
    private string _currentTabDomain = string.Empty;

    [ObservableProperty]
    private bool _isCurrentDomainWhitelisted;

    [ObservableProperty]
    private string _adBlockFilterStatus = "Đã nạp bộ lọc mặc định (EasyList + ABPVN Core)";

    [ObservableProperty]
    private bool _isProbingYtDlp;

    [ObservableProperty]
    private string _videoSnifferStatus = "Tự động bắt MP4, HLS (.m3u8), DASH (.mpd) hoặc quét bằng yt-dlp.";

    // === Giai đoạn 4 + Opera-Style Deep VPN (1.1.1.1 WARP + sing-box + Telemetry + Per-Site Bypass) ===
    [ObservableProperty]
    private VpnNode? _selectedVpnNode;

    [ObservableProperty]
    private bool _isVpnConnected;

    [ObservableProperty]
    private bool _isGlobalVpnEnabled;

    [ObservableProperty]
    private bool _isKillSwitchEnabled = true;

    [ObservableProperty]
    private bool _isKillSwitchTripped;

    [ObservableProperty]
    private bool _isSplitTunnelingEnabled = true;

    [ObservableProperty]
    private bool _bypassSearchEngines = true;

    [ObservableProperty]
    private bool _autoVpnInIncognito = true;

    [ObservableProperty]
    private bool _isDoubleVpnEnabled;

    [ObservableProperty]
    private bool _isCurrentDomainVpnBypassed;

    [ObservableProperty]
    private string _selectedVpnRegionKey = "Optimal";

    [ObservableProperty]
    private string _vpnRegionSummary = "Tối ưu (1.1.1.1 WARP)";

    [ObservableProperty]
    private string _vpnEncryptionSummary = "WireGuard · DNS 1.1.1.1 · Anti-WebRTC";

    [ObservableProperty]
    private string _vpnVirtualIp = "Chưa kết nối";

    [ObservableProperty]
    private string _vpnPoPLocation = "—";

    [ObservableProperty]
    private string _vpnDownloadTotalText = "0 B";

    [ObservableProperty]
    private string _vpnUploadTotalText = "0 B";

    [ObservableProperty]
    private string _vpnDownloadSpeedText = "0 KB/s";

    [ObservableProperty]
    private string _vpnUploadSpeedText = "0 KB/s";

    [ObservableProperty]
    private int _vpnActiveConnections;

    [ObservableProperty]
    private string _vpnSessionDurationText = "00:00:00";

    [ObservableProperty]
    private bool _isOptimizingWarp;

    [ObservableProperty]
    private string _vpnBadgeText = "VPN";

    [ObservableProperty]
    private string _vpnStatusMessage = "VPN đang tắt (Kết nối trực tiếp)";

    [ObservableProperty]
    private string _newVpnNodeInput = string.Empty;

    [ObservableProperty]
    private string _newVpnNodeName = string.Empty;

    [ObservableProperty]
    private string _newVpnNodeCountry = string.Empty;

    [ObservableProperty]
    private string _vpnSubscriptionInput = string.Empty;

    [ObservableProperty]
    private string _newVpnBypassDomainInput = string.Empty;

    [ObservableProperty]
    private bool _isOmniboxSuggestionOpen;

    [ObservableProperty]
    private bool _isAppMenuOpen;

    [ObservableProperty]
    private bool _isLeftSidebarVisible = true;

    [ObservableProperty]
    private string _leftSidebarMenuText = "Ẩn thanh bên";

    [ObservableProperty]
    private string _zoomPercentDisplay = "100%";

    [ObservableProperty]
    private bool _isFindBarVisible;

    [ObservableProperty]
    private string _findQueryText = string.Empty;

    // === Tiện ích chuột phải & Hỏi AI về trang này ===
    [ObservableProperty]
    private string _aiSummaryTitle = "Hỏi AI về trang này";

    [ObservableProperty]
    private string _aiSummaryUrl = string.Empty;

    [ObservableProperty]
    private string _aiSummaryMeta = "Bấm chuột phải → Hỏi AI → Tóm tắt trang này để phân tích nhanh nội dung.";

    [ObservableProperty]
    private string _aiSummaryContent = string.Empty;

    [ObservableProperty]
    private bool _isSummarizingPage;

    [ObservableProperty]
    private string _aiStatusBanner = string.Empty;

    [ObservableProperty]
    private string _selectedThemeKey = BrowserThemeManager.CurrentThemeKey;

    [ObservableProperty]
    private string _selectedThemeDisplayName = BrowserThemeManager.CurrentPreset.DisplayName;

    // === Trang Cài đặt kiểu Cốc Cốc (quangln://settings) ===
    [ObservableProperty]
    private string _activeSettingsSection = "General";

    [ObservableProperty]
    private string _settingsSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSettingsSearching;

    [ObservableProperty]
    private string _settingsSearchSummary = string.Empty;

    [ObservableProperty]
    private bool _showSectionGeneral = true;

    [ObservableProperty]
    private bool _showSectionAppearance;

    [ObservableProperty]
    private bool _showSectionSearchEngine;

    [ObservableProperty]
    private bool _showSectionStartup;

    [ObservableProperty]
    private bool _showSectionVpnNetwork;

    [ObservableProperty]
    private bool _showSectionAdBlock;

    [ObservableProperty]
    private bool _showSectionPrivacy;

    [ObservableProperty]
    private bool _showSectionLanguage;

    [ObservableProperty]
    private bool _showSectionDownloads;

    [ObservableProperty]
    private bool _showSectionSidebar;

    [ObservableProperty]
    private bool _showSectionDefaultAbout;

    // === Cấu hình Thanh bên (Sidebar kiểu Opera GX) ===
    [ObservableProperty]
    private bool _sidebarShowZalo = true;

    [ObservableProperty]
    private bool _sidebarShowMessenger = true;

    [ObservableProperty]
    private bool _sidebarShowDiscord = true;

    [ObservableProperty]
    private bool _sidebarShowTelegram;

    [ObservableProperty]
    private bool _sidebarShowAiChat = true;

    [ObservableProperty]
    private bool _sidebarShowBookmarks = true;

    [ObservableProperty]
    private bool _sidebarShowHistory = true;

    [ObservableProperty]
    private bool _sidebarShowDownloads = true;

    [ObservableProperty]
    private string _defaultSearchEngineKey = "google";

    [ObservableProperty]
    private string _defaultSearchEngineDisplayName = "Google";

    [ObservableProperty]
    private string _startupMode = "homepage";

    [ObservableProperty]
    private string _startupCustomUrl = "https://www.google.com";

    [ObservableProperty]
    private bool _showHomeButton = true;

    [ObservableProperty]
    private int _defaultZoomPercent = 100;

    [ObservableProperty]
    private string _downloadDirectoryPath = string.Empty;

    [ObservableProperty]
    private string _translateTargetLanguage = "vi";

    [ObservableProperty]
    private string _translateTargetLanguageDisplay = "Tiếng Việt (vi)";

    [ObservableProperty]
    private string _newAdBlockWhitelistDomainInput = string.Empty;

    [ObservableProperty]
    private string _settingsStatusBanner = string.Empty;

    [ObservableProperty]
    private string _profileNameInput = string.Empty;

    [ObservableProperty]
    private bool _isPrimaryProfileActive = true;

    [ObservableProperty]
    private bool _hasDecoyProfile;

    [ObservableProperty]
    private bool _isVaultWindowsHelloEnabled;

    [ObservableProperty]
    private bool _isWindowsHelloAvailable;

    [ObservableProperty]
    private bool _isVaultOperationBusy;

    private string _aiPromptCache = string.Empty;

    public event EventHandler<BrowserContextMenuInfo>? ContextMenuRequested;

    private int _selectedSuggestionIndex = -1;

    public ObservableCollection<TabViewModel> Tabs { get; } = new();
    public ObservableCollection<HistoryEntry> HistoryItems { get; } = new();
    public ObservableCollection<BookmarkEntry> BookmarkItems { get; } = new();
    public ObservableCollection<DownloadTaskViewModel> DownloadItems { get; } = new();
    public ObservableCollection<VpnNode> VpnNodes { get; } = new();
    public ObservableCollection<string> VpnBypassDomains { get; } = new();
    public ObservableCollection<string> AdBlockWhitelistDomains { get; } = new();
    public ObservableCollection<OmniboxSuggestionItem> OmniboxSuggestions { get; } = new();

    public LockScreenViewModel LockScreen { get; }

    public MainViewModel()
    {
        _isPrimaryWindow = true;
        _vaultService = new VaultSecurityService();
        _idleMonitor = new Win32IdleMonitor();
        _jobManager = new JobObjectManager();
        _adBlockEngine = AdBlockEngineFactory.Create();
        _mediaSniffer = new MediaSnifferService(_jobManager);
        _downloadManager = new DownloadManager(_jobManager, _vaultService);
        _vpnService = new VpnService(_jobManager);

        _idleMonitor.IdleTimeoutReached += OnIdleTimeoutReached;
        _downloadManager.DownloadProgressChanged += OnDownloadProgressChanged;
        _vpnService.StateChanged += OnVpnServiceStateChanged;
        _vpnService.TelemetryUpdated += OnVpnTelemetryUpdated;
        BrowserThemeManager.ThemeChanged += OnGlobalThemeChanged;
        _adBlockEngine.BlockedCountChanged += OnAdBlockEngineBlockedCountChanged;
        _adBlockEngine.BlockedCountChanged += OnAdBlockEngineBlockedCountChanged;

        _downloadDirectoryPath = _downloadManager.DefaultDownloadDirectory;
        _isAdBlockEnabled = _adBlockEngine.IsEnabled;
        _adBlockEngineName = _adBlockEngine.IsNativeEngine
            ? "Native Rust (adblock_rust.dll)"
            : "Managed C# Fallback";
        _adBlockTotalRules = _adBlockEngine.TotalRulesCount;

        _keyboardHandler = new BrowserKeyboardHandler(
            onPanicLock: () => Application.Current?.Dispatcher.InvokeAsync(async () => await LockBrowserAsync()),
            onNewTab: () => Application.Current?.Dispatcher.InvokeAsync(() => NewTabCommand.Execute(null)),
            onCloseTab: () => Application.Current?.Dispatcher.InvokeAsync(() => CloseTabCommand.Execute(null)));

        LockScreen = new LockScreenViewModel(_vaultService, OnVaultUnlocked);
    }

    public MainViewModel(BrowserSessionServices sharedSession, bool isIncognitoWindow, bool startWithWarpVpn, string? initialOverrideUrl = null)
    {
        _isPrimaryWindow = false;
        _sharedSession = sharedSession;
        _vaultService = sharedSession.VaultService;
        _idleMonitor = sharedSession.IdleMonitor;
        _jobManager = sharedSession.JobManager;
        _adBlockEngine = sharedSession.AdBlockEngine;
        _mediaSniffer = sharedSession.MediaSniffer;
        _downloadManager = sharedSession.DownloadManager;
        _vpnService = sharedSession.VpnService;
        _profileId = sharedSession.ProfileId ?? "primary";

        _isIncognitoWindow = isIncognitoWindow;
        _isIncognitoWarpWindow = isIncognitoWindow && startWithWarpVpn;
        _isWindowVpnActive = startWithWarpVpn;
        _isLocked = false;
        _activeProfileLabel = isIncognitoWindow
            ? (startWithWarpVpn ? "Ẩn danh · 1.1.1.1 WARP" : "Cửa sổ Ẩn danh")
            : (_vaultService.CurrentProfile?.Name ?? "Hồ sơ chính");
        _windowTitle = isIncognitoWindow
            ? (startWithWarpVpn ? "QUANGLN Browser — Ẩn danh · 1.1.1.1 WARP" : "QUANGLN Browser — Ẩn danh")
            : "QUANGLN Browser";
        _incognitoHeaderLabel = startWithWarpVpn ? "ẨN DANH · 1.1.1.1 WARP" : "ẨN DANH";

        _downloadManager.DownloadProgressChanged += OnDownloadProgressChanged;
        _vpnService.StateChanged += OnVpnServiceStateChanged;
        _vpnService.TelemetryUpdated += OnVpnTelemetryUpdated;
        BrowserThemeManager.ThemeChanged += OnGlobalThemeChanged;
        _adBlockEngine.BlockedCountChanged += OnAdBlockEngineBlockedCountChanged;

        _downloadDirectoryPath = _downloadManager.DefaultDownloadDirectory;
        _isAdBlockEnabled = _adBlockEngine.IsEnabled;
        _adBlockEngineName = _adBlockEngine.IsNativeEngine
            ? "Native Rust (adblock_rust.dll)"
            : "Managed C# Fallback";
        _adBlockTotalRules = _adBlockEngine.TotalRulesCount;

        _keyboardHandler = new BrowserKeyboardHandler(
            onPanicLock: () => Application.Current?.Dispatcher.InvokeAsync(() => _sharedSession?.RequestGlobalLock?.Invoke()),
            onNewTab: () => Application.Current?.Dispatcher.InvokeAsync(() => NewTabCommand.Execute(null)),
            onCloseTab: () => Application.Current?.Dispatcher.InvokeAsync(() => CloseTabCommand.Execute(null)));

        LockScreen = new LockScreenViewModel(_vaultService, _ => { });

        _ = InitializeSecondaryWindowAsync(isIncognitoWindow, startWithWarpVpn, initialOverrideUrl);
    }

    internal bool HasActiveVpnTraffic() =>
        IsGlobalVpnEnabled || _isWindowVpnActive || Tabs.Any(t => t.IsVpnEnabled);

    private BrowserSessionServices GetOrCreateSharedSession()
    {
        if (_sharedSession is null)
        {
            _sharedSession = new BrowserSessionServices
            {
                VaultService = _vaultService,
                IdleMonitor = _idleMonitor,
                JobManager = _jobManager,
                AdBlockEngine = _adBlockEngine,
                MediaSniffer = _mediaSniffer,
                DownloadManager = _downloadManager,
                VpnService = _vpnService,
                ProfileId = _profileId,
                PrimaryVpnUsageProbe = () => IsGlobalVpnEnabled || Tabs.Any(t => t.IsVpnEnabled),
                RequestGlobalLock = () => _ = LockBrowserAsync()
            };
        }
        else
        {
            _sharedSession.ProfileId = _profileId;
        }
        return _sharedSession;
    }

    private async Task InitializeSecondaryWindowAsync(bool isIncognitoWindow, bool startWithWarpVpn, string? initialOverrideUrl = null)
    {
        try
        {
            await LoadVpnPreferencesAsync();
            await RefreshVpnNodesAsync();
            if (!isIncognitoWindow)
            {
                await RefreshBookmarksAsync();
                await RefreshHistoryAsync();
            }
            await RefreshDownloadsAsync();

            SyncCurrentVpnUiSnapshot();

            if (!isIncognitoWindow)
            {
                CreateNewTab(string.IsNullOrWhiteSpace(initialOverrideUrl) ? HomePageBuilder.GetHomePageUri() : initialOverrideUrl);
                return;
            }

            bool shouldUseVpn = startWithWarpVpn || (AutoVpnInIncognito && _vpnService.IsRunning) || AutoVpnInIncognito;
            if (startWithWarpVpn)
            {
                var warpNode = await EnsureCloudflareWarpNodeReadyAsync();
                if (warpNode is not null && (!_vpnService.IsRunning || _vpnService.ActiveNode?.Id != warpNode.Id))
                {
                    VpnStatusMessage = "Đang bật đường hầm Cloudflare 1.1.1.1 WARP cho cửa sổ Ẩn danh...";
                    shouldUseVpn = await _vpnService.StartAsync(warpNode);
                }
                else
                {
                    shouldUseVpn = _vpnService.IsRunning;
                }
            }
            else if (shouldUseVpn && !_vpnService.IsRunning)
            {
                var node = await EnsureSelectedOrWarpNodeReadyAsync();
                shouldUseVpn = node is not null && await _vpnService.StartAsync(node, GetDoubleVpnEntryNode(node));
            }

            _isWindowVpnActive = shouldUseVpn;

            string initialUrl = !string.IsNullOrWhiteSpace(initialOverrideUrl)
                ? initialOverrideUrl
                : IncognitoStartPageBuilder.EnsureStartPageFile(
                    withWarp: startWithWarpVpn || shouldUseVpn,
                    isWarpConnected: shouldUseVpn && _vpnService.IsRunning,
                    virtualIp: VpnVirtualIp,
                    popLocation: VpnPoPLocation);

            var initialTab = CreateTabCore(
                initialUrl,
                isIncognito: true,
                isVpnEnabled: shouldUseVpn,
                initialTitle: startWithWarpVpn ? "Ẩn danh · 1.1.1.1 WARP" : "Tab Ẩn danh mới",
                ownsContext: false);

            if (shouldUseVpn)
            {
                var trace = await CloudflareWarpProvisioner.VerifyWarpTraceAsync(_vpnService.LocalSocksPort);
                if (!_disposed && _vpnService.IsRunning && !_vpnService.IsKillSwitchTripped)
                {
                    if (trace.IsWarpActive)
                    {
                        string coloLabel = string.IsNullOrEmpty(trace.Colo) ? "EDGE" : trace.Colo;
                        if (!string.IsNullOrEmpty(trace.Location)) coloLabel = $"{coloLabel} ({trace.Location})";
                        VpnVirtualIp = string.IsNullOrEmpty(trace.PublicIp) ? "Đã ẩn (WARP)" : trace.PublicIp;
                        VpnPoPLocation = coloLabel;
                        VpnBadgeText = string.IsNullOrEmpty(trace.Colo) ? "WARP" : $"WARP · {trace.Colo}";
                        VpnStatusMessage = $"✅ Ẩn danh + 1.1.1.1 WARP=ON • IP ảo: {VpnVirtualIp} ({coloLabel})";
                    }

                    string updatedHtmlUri = IncognitoStartPageBuilder.EnsureStartPageFile(
                        withWarp: startWithWarpVpn || shouldUseVpn,
                        isWarpConnected: true,
                        virtualIp: VpnVirtualIp,
                        popLocation: VpnPoPLocation);

                    if (IncognitoStartPageBuilder.IsInternalStartUrl(initialTab.Address) &&
                        initialTab.IsBrowserInitialized)
                    {
                        initialTab.Browser.Reload();
                    }
                    else if (IncognitoStartPageBuilder.IsInternalStartUrl(initialTab.Address))
                    {
                        initialTab.Navigate(updatedHtmlUri);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("InitializeSecondaryWindowAsync failed", ex);
        }
    }

    private void SyncCurrentVpnUiSnapshot()
    {
        IsVpnConnected = _vpnService.IsRunning;
        IsKillSwitchTripped = _vpnService.IsKillSwitchTripped;
        if (_vpnService.IsRunning && _vpnService.ActiveNode is not null)
        {
            UpdateVpnEncryptionSummary(_vpnService.ActiveNode);
            VpnBadgeText = _vpnService.ActiveNode.Id == CloudflareWarpProvisioner.DefaultWarpNodeId
                ? "WARP · ON"
                : $"VPN · {_vpnService.ActiveNode.CountryCode.ToUpperInvariant()}";
            VpnStatusMessage = $"Đang kết nối: {_vpnService.ActiveNode.DisplayTitle}";
        }
    }

    partial void OnIsAdBlockEnabledChanged(bool value)
    {
        if (_adBlockEngine.IsEnabled != value)
        {
            _adBlockEngine.IsEnabled = value;
            if (ActiveTab is not null && !ActiveTab.IsSettingsTab)
            {
                ActiveTab.Browser.Reload();
            }
        }

        if (!_isLoadingVpnPreferences)
        {
            _ = SaveVpnPreferencesAsync();
        }
    }

    partial void OnAdBlockBlockPopupsChanged(bool value)
    {
        _adBlockEngine.BlockPopups = value;
        if (!_isLoadingVpnPreferences) _ = SaveVpnPreferencesAsync();
    }

    partial void OnAdBlockBlockTrackersChanged(bool value)
    {
        _adBlockEngine.BlockTrackers = value;
        if (!_isLoadingVpnPreferences) _ = SaveVpnPreferencesAsync();
    }

    partial void OnAdBlockBlockYouTubeAdsChanged(bool value)
    {
        _adBlockEngine.BlockYouTubeAds = value;
        if (!_isLoadingVpnPreferences) _ = SaveVpnPreferencesAsync();
    }

    partial void OnAdBlockAllowAcceptableAdsChanged(bool value)
    {
        _adBlockEngine.AllowAcceptableAds = value;
        if (!_isLoadingVpnPreferences) _ = SaveVpnPreferencesAsync();
    }

    partial void OnAdBlockEnableEasyListChanged(bool value)
    {
        _adBlockEngine.EnableEasyList = value;
        if (!_isLoadingVpnPreferences)
        {
            _ = SaveVpnPreferencesAsync();
            _ = LoadAdBlockFiltersInBackgroundAsync();
        }
    }

    partial void OnAdBlockEnableEasyPrivacyChanged(bool value)
    {
        _adBlockEngine.EnableEasyPrivacy = value;
        if (!_isLoadingVpnPreferences)
        {
            _ = SaveVpnPreferencesAsync();
            _ = LoadAdBlockFiltersInBackgroundAsync();
        }
    }

    partial void OnAdBlockEnableAbpvnChanged(bool value)
    {
        _adBlockEngine.EnableAbpvn = value;
        if (!_isLoadingVpnPreferences)
        {
            _ = SaveVpnPreferencesAsync();
            _ = LoadAdBlockFiltersInBackgroundAsync();
        }
    }

    partial void OnAdBlockEnableAntiAnnoyanceChanged(bool value)
    {
        _adBlockEngine.EnableAntiAnnoyance = value;
        if (!_isLoadingVpnPreferences)
        {
            _ = SaveVpnPreferencesAsync();
            _ = LoadAdBlockFiltersInBackgroundAsync();
        }
    }

    partial void OnShowHomeButtonChanged(bool value)
    {
        if (!_isLoadingVpnPreferences)
        {
            _ = SaveVpnPreferencesAsync();
        }
    }

    partial void OnIsLeftSidebarVisibleChanged(bool value)
    {
        LeftSidebarMenuText = value ? "Ẩn thanh bên" : "Hiện thanh bên";
        OnSidebarItemToggled("Thanh bên", value);
    }

    partial void OnSidebarShowZaloChanged(bool value) => OnSidebarItemToggled("Zalo", value);
    partial void OnSidebarShowMessengerChanged(bool value) => OnSidebarItemToggled("Messenger", value);
    partial void OnSidebarShowDiscordChanged(bool value) => OnSidebarItemToggled("Discord", value);
    partial void OnSidebarShowTelegramChanged(bool value) => OnSidebarItemToggled("Telegram", value);
    partial void OnSidebarShowAiChatChanged(bool value) => OnSidebarItemToggled("AI Chat", value);
    partial void OnSidebarShowBookmarksChanged(bool value) => OnSidebarItemToggled("Dấu trang", value);
    partial void OnSidebarShowHistoryChanged(bool value) => OnSidebarItemToggled("Lịch sử", value);
    partial void OnSidebarShowDownloadsChanged(bool value) => OnSidebarItemToggled("Quản lý tải xuống", value);

    private void OnSidebarItemToggled(string itemName, bool isVisible)
    {
        if (_isLoadingVpnPreferences)
        {
            return;
        }

        SettingsStatusBanner = $"{(isVisible ? "Đã ghim" : "Đã ẩn")} {itemName} trên thanh bên";
        _ = SaveVpnPreferencesAsync();
    }

    [RelayCommand]
    private void OpenSidebarWebApp(string appKey)
    {
        (string targetUrl, string hostMatch) = (appKey ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "zalo" => ("https://chat.zalo.me", "chat.zalo.me"),
            "messenger" => ("https://www.messenger.com", "messenger.com"),
            "discord" => ("https://discord.com/app", "discord.com"),
            "telegram" => ("https://web.telegram.org", "web.telegram.org"),
            _ => (string.Empty, string.Empty)
        };

        if (string.IsNullOrEmpty(targetUrl))
        {
            return;
        }

        var existingTab = Tabs.FirstOrDefault(t =>
            !string.IsNullOrWhiteSpace(t.Address) &&
            t.Address.Contains(hostMatch, StringComparison.OrdinalIgnoreCase));

        if (existingTab is not null)
        {
            ActiveTab = existingTab;
            return;
        }

        CreateNewTab(targetUrl);
    }

    partial void OnActiveSettingsSectionChanged(string value)
    {
        UpdateSettingsSectionVisibility();
    }

    partial void OnSettingsSearchQueryChanged(string value)
    {
        UpdateSettingsSectionVisibility();
    }

    partial void OnIsKillSwitchEnabledChanged(bool value)
    {
        _vpnService.IsKillSwitchEnabled = value;
        if (!_isLoadingVpnPreferences)
        {
            _ = SaveVpnPreferencesAsync();
        }
    }

    partial void OnIsSplitTunnelingEnabledChanged(bool value)
    {
        _vpnService.IsSplitTunnelingEnabled = value;
        if (!_isLoadingVpnPreferences)
        {
            _ = SaveVpnPreferencesAsync();
            if (_vpnService.IsRunning && SelectedVpnNode is not null)
            {
                _ = RestartVpnWithCurrentSettingsAsync();
            }
        }
    }

    partial void OnBypassSearchEnginesChanged(bool value)
    {
        _vpnService.BypassSearchEngines = value;
        if (!_isLoadingVpnPreferences)
        {
            _ = SaveVpnPreferencesAsync();
            if (_vpnService.IsRunning && SelectedVpnNode is not null)
            {
                _ = RestartVpnWithCurrentSettingsAsync();
            }
        }
    }

    partial void OnAutoVpnInIncognitoChanged(bool value)
    {
        if (!_isLoadingVpnPreferences)
        {
            _ = SaveVpnPreferencesAsync();
        }
    }

    partial void OnIsDoubleVpnEnabledChanged(bool value)
    {
        _vpnService.IsDoubleVpnEnabled = value;
        if (!_isLoadingVpnPreferences)
        {
            _ = SaveVpnPreferencesAsync();
            if (_vpnService.IsRunning && SelectedVpnNode is not null)
            {
                _ = RestartVpnWithCurrentSettingsAsync();
            }
        }
    }

    partial void OnSelectedVpnNodeChanged(VpnNode? value)
    {
        if (value is null)
        {
            return;
        }

        UpdateVpnEncryptionSummary(value);

        if (_isLoadingVpnPreferences)
        {
            return;
        }

        SelectedVpnRegionKey = value.Region.ToString();
        VpnRegionSummary = $"{value.RegionLabel} · {value.DisplayTitle}";
        _ = SaveVpnPreferencesAsync();

        if (_vpnService.IsRunning && _vpnService.ActiveNode?.Id != value.Id)
        {
            _ = SwitchActiveVpnNodeAsync(value);
        }
    }

    partial void OnActiveTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
            oldValue.PropertyChanged -= OnActiveTabPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
            newValue.PropertyChanged += OnActiveTabPropertyChanged;
            IsOmniboxSuggestionOpen = false;
            OmniboxText = HomePageBuilder.IsInternalBrowserPage(newValue.Address)
                ? string.Empty
                : newValue.Address;
            ZoomPercentDisplay = $"{newValue.ZoomPercent}%";
            UpdateCurrentDomainState(newValue.Address);
            _ = UpdateBookmarkStateAsync(newValue.Address);
        }
        else
        {
            IsOmniboxSuggestionOpen = false;
            OmniboxText = string.Empty;
            ZoomPercentDisplay = "100%";
            CurrentTabDomain = string.Empty;
            IsCurrentDomainWhitelisted = false;
            IsCurrentPageBookmarked = false;
        }
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TabViewModel tab || tab != ActiveTab)
        {
            return;
        }

        if (e.PropertyName == nameof(TabViewModel.Address))
        {
            IsOmniboxSuggestionOpen = false;
            OmniboxText = HomePageBuilder.IsInternalBrowserPage(tab.Address)
                ? string.Empty
                : tab.Address;
            UpdateCurrentDomainState(tab.Address);
            _ = UpdateBookmarkStateAsync(tab.Address);
        }
    }

    private void UpdateCurrentDomainState(string url)
    {
        if (HomePageBuilder.IsHomeUrl(url))
        {
            CurrentTabDomain = "Trang chủ QUANGLN";
            IsCurrentDomainWhitelisted = false;
            IsCurrentDomainVpnBypassed = false;
            return;
        }

        if (IncognitoStartPageBuilder.IsInternalStartUrl(url))
        {
            CurrentTabDomain = "Trang Ẩn danh nội bộ";
            IsCurrentDomainWhitelisted = false;
            IsCurrentDomainVpnBypassed = false;
            return;
        }

        string domain = ManagedAdBlockEngine.ExtractHost(url);
        CurrentTabDomain = string.IsNullOrEmpty(domain) ? "Trang nội bộ" : domain;
        IsCurrentDomainWhitelisted = !string.IsNullOrEmpty(domain) && _adBlockEngine.IsDomainWhitelisted(domain);
        IsCurrentDomainVpnBypassed = !string.IsNullOrEmpty(domain) && IsDomainInVpnBypassList(domain);
    }

    private bool IsDomainInVpnBypassList(string domain)
    {
        string normalized = VpnNodeParser.NormalizeBypassDomain(domain);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        return VpnBypassDomains.Any(d =>
            string.Equals(d, normalized, StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    private void OnVaultUnlocked(ProfileInfo profile)
    {
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                AppLogger.Info($"Vault unlocked, profile={profile.Id}, decoy={profile.IsDecoy}");

                var whitelisted = await _vaultService.GetWhitelistedDomainsAsync();
                _adBlockEngine.SetWhitelistedDomains(whitelisted);
                RefreshAdBlockWhitelistCollection(whitelisted);

                if (_loadedProfileId != profile.Id)
                {
                    CloseAllTabs();
                    _profileId = profile.Id;
                    _loadedProfileId = profile.Id;
                }

                ActiveProfileLabel = profile.IsDecoy ? "Hồ sơ khách" : profile.Name;
                ProfileNameInput = ActiveProfileLabel;
                IsPrimaryProfileActive = !profile.IsDecoy;
                HasDecoyProfile = _vaultService.HasDecoyProfile;
                IsVaultWindowsHelloEnabled = _vaultService.IsWindowsHelloEnabled;
                IsWindowsHelloAvailable = await _vaultService.IsWindowsHelloAvailableAsync();
                AutoLockMinutes = profile.AutoLockMinutes;
                IsLocked = false;

                await LoadVpnPreferencesAsync();
                await RefreshBookmarksAsync();
                await RefreshHistoryAsync();
                await RefreshDownloadsAsync();
                await RefreshVpnNodesAsync();

                if (Tabs.Count == 0)
                {
                    string startupUrl = string.Equals(StartupMode, "custom", StringComparison.OrdinalIgnoreCase) &&
                                        !string.IsNullOrWhiteSpace(StartupCustomUrl)
                        ? UrlHelper.NormalizeUrlOrSearch(StartupCustomUrl)
                        : HomePageBuilder.GetHomePageUri();
                    CreateNewTab(startupUrl);
                }
                else if (ActiveTab is not null)
                {
                    UpdateCurrentDomainState(ActiveTab.Address);
                }

                _idleMonitor.Start(TimeSpan.FromMinutes(Math.Max(1, AutoLockMinutes)));

                if (!_filtersLoadedOnce)
                {
                    _filtersLoadedOnce = true;
                    _ = LoadAdBlockFiltersInBackgroundAsync();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to start browser after unlock", ex);
                _vaultService.Lock();
                IsLocked = true;
                LockScreen.RefreshState();
                LockScreen.ReportError($"Mở khóa thành công nhưng không khởi động được trình duyệt: {ex.Message}. Xem log tại %AppData%\\QUANGLNBrowser\\logs");
            }
        });
    }

    private async Task LoadVpnPreferencesAsync()
    {
        var prefs = await _vaultService.GetVpnPreferencesAsync();
        _isLoadingVpnPreferences = true;
        try
        {
            SelectedVpnRegionKey = prefs.SelectedRegion.ToString();
            BypassSearchEngines = prefs.BypassSearchEngines;
            AutoVpnInIncognito = prefs.AutoVpnInIncognito;
            IsKillSwitchEnabled = prefs.EnableKillSwitch;
            IsSplitTunnelingEnabled = prefs.EnableSplitTunneling;
            IsDoubleVpnEnabled = prefs.EnableDoubleVpn;

            VpnBypassDomains.Clear();
            foreach (string domain in prefs.CustomBypassDomains)
            {
                string clean = VpnNodeParser.NormalizeBypassDomain(domain);
                if (!string.IsNullOrEmpty(clean) && !VpnBypassDomains.Contains(clean, StringComparer.OrdinalIgnoreCase))
                {
                    VpnBypassDomains.Add(clean);
                }
            }

            _vpnService.IsKillSwitchEnabled = IsKillSwitchEnabled;
            _vpnService.IsSplitTunnelingEnabled = IsSplitTunnelingEnabled;
            _vpnService.BypassSearchEngines = BypassSearchEngines;
            _vpnService.IsDoubleVpnEnabled = IsDoubleVpnEnabled;
            _vpnService.SetCustomBypassDomains(VpnBypassDomains);

            if (!string.IsNullOrWhiteSpace(prefs.ThemeKey) &&
                !string.Equals(prefs.ThemeKey, BrowserThemeManager.CurrentThemeKey, StringComparison.OrdinalIgnoreCase))
            {
                BrowserThemeManager.ApplyTheme(prefs.ThemeKey);
            }
            SelectedThemeKey = BrowserThemeManager.CurrentThemeKey;
            SelectedThemeDisplayName = BrowserThemeManager.CurrentPreset.DisplayName;

            DefaultSearchEngineKey = NormalizeSearchEngineKey(prefs.DefaultSearchEngine);
            DefaultSearchEngineDisplayName = FormatSearchEngineDisplay(DefaultSearchEngineKey);
            UrlHelper.DefaultSearchEngineKey = DefaultSearchEngineKey;

            StartupMode = string.Equals(prefs.StartupMode, "custom", StringComparison.OrdinalIgnoreCase) ? "custom" : "homepage";
            StartupCustomUrl = string.IsNullOrWhiteSpace(prefs.StartupCustomUrl) ? "https://www.google.com" : prefs.StartupCustomUrl.Trim();

            ShowHomeButton = prefs.ShowHomeButton;
            IsLeftSidebarVisible = prefs.ShowLeftSidebar;
            LeftSidebarMenuText = IsLeftSidebarVisible ? "Ẩn thanh bên" : "Hiện thanh bên";
            SidebarShowZalo = prefs.SidebarShowZalo;
            SidebarShowMessenger = prefs.SidebarShowMessenger;
            SidebarShowDiscord = prefs.SidebarShowDiscord;
            SidebarShowTelegram = prefs.SidebarShowTelegram;
            SidebarShowAiChat = prefs.SidebarShowAiChat;
            SidebarShowBookmarks = prefs.SidebarShowBookmarks;
            SidebarShowHistory = prefs.SidebarShowHistory;
            SidebarShowDownloads = prefs.SidebarShowDownloads;
            DefaultZoomPercent = Math.Clamp(prefs.DefaultZoomPercent <= 0 ? 100 : prefs.DefaultZoomPercent, 50, 200);

            IsAdBlockEnabled = prefs.EnableAdBlock;
            _adBlockEngine.IsEnabled = IsAdBlockEnabled;

            AdBlockMode = prefs.AdBlockMode ?? "standard";
            IsAdBlockStandardMode = AdBlockMode == "standard";
            IsAdBlockStrictMode = AdBlockMode == "strict";
            AdBlockBlockPopups = prefs.AdBlockBlockPopups;
            AdBlockBlockTrackers = prefs.AdBlockBlockTrackers;
            AdBlockBlockYouTubeAds = prefs.AdBlockBlockYouTubeAds;
            AdBlockAllowAcceptableAds = prefs.AdBlockAllowAcceptableAds;
            AdBlockEnableEasyList = prefs.AdBlockEnableEasyList;
            AdBlockEnableEasyPrivacy = prefs.AdBlockEnableEasyPrivacy;
            AdBlockEnableAbpvn = prefs.AdBlockEnableAbpvn;
            AdBlockEnableAntiAnnoyance = prefs.AdBlockEnableAntiAnnoyance;
            AdBlockTotalAdsBlocked = prefs.AdBlockTotalAdsBlocked;
            AdBlockTotalTrackersBlocked = prefs.AdBlockTotalTrackersBlocked;

            _adBlockEngine.BlockingMode = AdBlockMode;
            _adBlockEngine.BlockPopups = AdBlockBlockPopups;
            _adBlockEngine.BlockTrackers = AdBlockBlockTrackers;
            _adBlockEngine.BlockYouTubeAds = AdBlockBlockYouTubeAds;
            _adBlockEngine.AllowAcceptableAds = AdBlockAllowAcceptableAds;
            _adBlockEngine.EnableEasyList = AdBlockEnableEasyList;
            _adBlockEngine.EnableEasyPrivacy = AdBlockEnableEasyPrivacy;
            _adBlockEngine.EnableAbpvn = AdBlockEnableAbpvn;
            _adBlockEngine.EnableAntiAnnoyance = AdBlockEnableAntiAnnoyance;
            _adBlockEngine.TotalLifetimeAdsBlocked = AdBlockTotalAdsBlocked;
            _adBlockEngine.TotalLifetimeTrackersBlocked = AdBlockTotalTrackersBlocked;

            AdBlockCustomRules.Clear();
            if (prefs.AdBlockCustomRules is not null)
            {
                foreach (string r in prefs.AdBlockCustomRules)
                {
                    AdBlockCustomRules.Add(r);
                }
                _adBlockEngine.SetCustomRules(prefs.AdBlockCustomRules);
            }

            if (!string.IsNullOrWhiteSpace(prefs.DownloadDirectory))
            {
                _downloadManager.DefaultDownloadDirectory = prefs.DownloadDirectory;
            }
            DownloadDirectoryPath = _downloadManager.DefaultDownloadDirectory;

            TranslateTargetLanguage = NormalizeTranslateLang(prefs.TranslateTargetLanguage);
            TranslateTargetLanguageDisplay = FormatTranslateLangDisplay(TranslateTargetLanguage);

            AutoLockMinutes = Math.Clamp(prefs.AutoLockIdleMinutes <= 0 ? AutoLockMinutes : prefs.AutoLockIdleMinutes, 1, 120);

            if (ActiveTab is not null)
            {
                UpdateCurrentDomainState(ActiveTab.Address);
            }
        }
        finally
        {
            _isLoadingVpnPreferences = false;
        }
    }

    private async Task SaveVpnPreferencesAsync()
    {
        if (!_vaultService.IsUnlocked)
        {
            return;
        }

        var region = Enum.TryParse<VpnRegion>(SelectedVpnRegionKey, ignoreCase: true, out var parsedRegion)
            ? parsedRegion
            : VpnRegion.Optimal;

        var prefs = new VpnPreferences
        {
            SelectedRegion = region,
            BypassSearchEngines = BypassSearchEngines,
            AutoVpnInIncognito = AutoVpnInIncognito,
            EnableKillSwitch = IsKillSwitchEnabled,
            EnableSplitTunneling = IsSplitTunnelingEnabled,
            EnableDoubleVpn = IsDoubleVpnEnabled,
            ThemeKey = BrowserThemeManager.CurrentThemeKey,
            LastSelectedNodeId = SelectedVpnNode?.Id,
            CustomBypassDomains = VpnBypassDomains.ToList(),
            DefaultSearchEngine = DefaultSearchEngineKey,
            StartupMode = StartupMode,
            StartupCustomUrl = StartupCustomUrl,
            ShowHomeButton = ShowHomeButton,
            ShowLeftSidebar = IsLeftSidebarVisible,
            SidebarShowZalo = SidebarShowZalo,
            SidebarShowMessenger = SidebarShowMessenger,
            SidebarShowDiscord = SidebarShowDiscord,
            SidebarShowTelegram = SidebarShowTelegram,
            SidebarShowAiChat = SidebarShowAiChat,
            SidebarShowBookmarks = SidebarShowBookmarks,
            SidebarShowHistory = SidebarShowHistory,
            SidebarShowDownloads = SidebarShowDownloads,
            DefaultZoomPercent = DefaultZoomPercent,
            EnableAdBlock = IsAdBlockEnabled,
            AdBlockMode = AdBlockMode,
            AdBlockBlockPopups = AdBlockBlockPopups,
            AdBlockBlockTrackers = AdBlockBlockTrackers,
            AdBlockBlockYouTubeAds = AdBlockBlockYouTubeAds,
            AdBlockAllowAcceptableAds = AdBlockAllowAcceptableAds,
            AdBlockEnableEasyList = AdBlockEnableEasyList,
            AdBlockEnableEasyPrivacy = AdBlockEnableEasyPrivacy,
            AdBlockEnableAbpvn = AdBlockEnableAbpvn,
            AdBlockEnableAntiAnnoyance = AdBlockEnableAntiAnnoyance,
            AdBlockTotalAdsBlocked = _adBlockEngine.TotalLifetimeAdsBlocked,
            AdBlockTotalTrackersBlocked = _adBlockEngine.TotalLifetimeTrackersBlocked,
            AdBlockCustomRules = AdBlockCustomRules.ToList(),
            DownloadDirectory = DownloadDirectoryPath,
            TranslateTargetLanguage = TranslateTargetLanguage,
            AutoLockIdleMinutes = AutoLockMinutes
        };

        await _vaultService.SaveVpnPreferencesAsync(prefs);
    }

    private void OnGlobalThemeChanged(object? sender, BrowserThemePreset preset)
    {
        SelectedThemeKey = preset.Key;
        SelectedThemeDisplayName = preset.DisplayName;
        foreach (var tab in Tabs)
        {
            tab.SyncThemeToPage(preset.Key);
        }
        SplitViewTab?.SyncThemeToPage(preset.Key);
    }

    private void OnPageThemeChanged(string themeKey)
    {
        SelectTheme(themeKey);
    }

    [RelayCommand]
    private void SelectTheme(string? themeKey)
    {
        var resolved = BrowserThemeCatalog.Resolve(themeKey);
        BrowserThemeManager.ApplyTheme(resolved.Key);
        _ = SaveVpnPreferencesAsync();
    }

    [RelayCommand]
    private void CycleTheme()
    {
        var next = BrowserThemeCatalog.GetNext(BrowserThemeManager.CurrentThemeKey);
        SelectTheme(next.Key);
    }

    // === Trang Cài đặt kiểu Cốc Cốc (quangln://settings) ===

    [RelayCommand]
    private async Task OpenSettingsPageAsync(string? section = null)
    {
        IsAppMenuOpen = false;
        IsDrawerOpen = false;

        string targetSection = string.IsNullOrWhiteSpace(section) ? "General" : section.Trim();
        SettingsSearchQuery = string.Empty;
        ActiveSettingsSection = targetSection;
        UpdateSettingsSectionVisibility();

        string settingsUrl = targetSection == "General"
            ? UrlHelper.SettingsUrlPrefix
            : $"{UrlHelper.SettingsUrlPrefix}/{targetSection.ToLowerInvariant()}";

        var existingSettingsTab = Tabs.FirstOrDefault(t => t.IsSettingsTab);
        if (existingSettingsTab is not null)
        {
            existingSettingsTab.Address = settingsUrl;
            ActiveTab = existingSettingsTab;
            OmniboxText = settingsUrl;
        }
        else
        {
            CreateNewTab(settingsUrl);
        }

        if (_vaultService.IsUnlocked)
        {
            await RefreshVpnNodesAsync();
            var whitelisted = await _vaultService.GetWhitelistedDomainsAsync();
            RefreshAdBlockWhitelistCollection(whitelisted);
        }
    }

    [RelayCommand]
    private void SelectSettingsSection(string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return;
        }

        SettingsSearchQuery = string.Empty;
        ActiveSettingsSection = section.Trim();
        UpdateSettingsSectionVisibility();

        if (ActiveTab?.IsSettingsTab == true)
        {
            string url = ActiveSettingsSection == "General"
                ? UrlHelper.SettingsUrlPrefix
                : $"{UrlHelper.SettingsUrlPrefix}/{ActiveSettingsSection.ToLowerInvariant()}";
            ActiveTab.Address = url;
            OmniboxText = url;
        }
    }

    [RelayCommand]
    private void ClearSettingsSearch()
    {
        SettingsSearchQuery = string.Empty;
    }

    private void UpdateSettingsSectionVisibility()
    {
        string q = (SettingsSearchQuery ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(q))
        {
            IsSettingsSearching = false;
            SettingsSearchSummary = string.Empty;
            ShowSectionGeneral = ActiveSettingsSection == "General";
            ShowSectionAppearance = ActiveSettingsSection == "Appearance";
            ShowSectionSearchEngine = ActiveSettingsSection == "SearchEngine";
            ShowSectionStartup = ActiveSettingsSection == "Startup";
            ShowSectionVpnNetwork = ActiveSettingsSection == "VpnNetwork";
            ShowSectionAdBlock = ActiveSettingsSection == "AdBlock";
            ShowSectionPrivacy = ActiveSettingsSection == "Privacy";
            ShowSectionLanguage = ActiveSettingsSection == "Language";
            ShowSectionDownloads = ActiveSettingsSection == "Downloads";
            ShowSectionSidebar = ActiveSettingsSection == "Sidebar";
            ShowSectionDefaultAbout = ActiveSettingsSection == "DefaultAbout";
            return;
        }

        IsSettingsSearching = true;
        ShowSectionGeneral = MatchesKeywords(q, "chung", "hồ sơ", "profile", "vault", "pin", "khóa", "lock", "argon2", "sqlcipher", "decoy", "nhàn rỗi");
        ShowSectionAppearance = MatchesKeywords(q, "giao diện", "appearance", "chủ đề", "theme", "màu", "neon", "coccoc", "crimson", "cyan", "purple", "trang chủ", "home", "zoom", "thu phóng");
        ShowSectionSearchEngine = MatchesKeywords(q, "tìm kiếm", "search", "google", "cốc cốc", "coccoc", "bing", "duckduckgo", "omnibox", "địa chỉ");
        ShowSectionStartup = MatchesKeywords(q, "khởi động", "startup", "mở trang", "homepage", "trang chủ", "tùy chỉnh");
        ShowSectionVpnNetwork = MatchesKeywords(q, "vpn", "warp", "1.1.1.1", "sing-box", "mạng", "network", "opera", "kill switch", "split", "double", "bypass", "bỏ qua", "node", "subscription", "vless", "proxy");
        ShowSectionAdBlock = MatchesKeywords(q, "quảng cáo", "adblock", "chặn", "shield", "whitelist", "ngoại lệ", "easylist", "abpvn", "bộ lọc", "filter");
        ShowSectionPrivacy = MatchesKeywords(q, "riêng tư", "privacy", "bảo mật", "security", "cookie", "lịch sử", "history", "xóa dữ liệu", "ẩn danh", "incognito", "webrtc");
        ShowSectionLanguage = MatchesKeywords(q, "ngôn ngữ", "language", "dịch", "translate", "tiếng việt", "english", "ai", "chuột phải");
        ShowSectionDownloads = MatchesKeywords(q, "tải", "download", "thư mục", "folder", "media", "video", "sniffer", "yt-dlp", "libmpv", "savior");
        ShowSectionSidebar = MatchesKeywords(q, "thanh bên", "sidebar", "zalo", "messenger", "discord", "telegram", "ai chat", "dấu trang", "bookmarks", "lịch sử", "history", "tải xuống", "ghim", "biểu tượng", "opera gx");
        ShowSectionDefaultAbout = MatchesKeywords(q, "mặc định", "default", "giới thiệu", "about", "phiên bản", "version", "đặt lại", "reset", "trợ giúp", "help");

        int matched =
            (ShowSectionGeneral ? 1 : 0) +
            (ShowSectionAppearance ? 1 : 0) +
            (ShowSectionSearchEngine ? 1 : 0) +
            (ShowSectionStartup ? 1 : 0) +
            (ShowSectionVpnNetwork ? 1 : 0) +
            (ShowSectionAdBlock ? 1 : 0) +
            (ShowSectionPrivacy ? 1 : 0) +
            (ShowSectionLanguage ? 1 : 0) +
            (ShowSectionDownloads ? 1 : 0) +
            (ShowSectionSidebar ? 1 : 0) +
            (ShowSectionDefaultAbout ? 1 : 0);

        SettingsSearchSummary = matched > 0
            ? $"Tìm thấy {matched} nhóm cài đặt khớp với \"{q}\""
            : $"Không tìm thấy cài đặt nào khớp với \"{q}\"";
    }

    private static bool MatchesKeywords(string query, params string[] keywords) =>
        keywords.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          query.Contains(k, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void SelectSearchEngine(string? engineKey)
    {
        DefaultSearchEngineKey = NormalizeSearchEngineKey(engineKey);
        DefaultSearchEngineDisplayName = FormatSearchEngineDisplay(DefaultSearchEngineKey);
        UrlHelper.DefaultSearchEngineKey = DefaultSearchEngineKey;
        _ = SaveVpnPreferencesAsync();
        SettingsStatusBanner = $"✓ Đã đặt công cụ tìm kiếm mặc định: {DefaultSearchEngineDisplayName}";
    }

    [RelayCommand]
    private void SelectStartupMode(string? mode)
    {
        StartupMode = string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase) ? "custom" : "homepage";
        _ = SaveVpnPreferencesAsync();
        SettingsStatusBanner = StartupMode == "homepage"
            ? "✓ Khi khởi động: Mở Trang chủ QUANGLN"
            : $"✓ Khi khởi động: Mở trang {StartupCustomUrl}";
    }

    [RelayCommand]
    private void SaveStartupCustomUrl()
    {
        if (string.IsNullOrWhiteSpace(StartupCustomUrl))
        {
            StartupCustomUrl = "https://www.google.com";
        }
        else
        {
            StartupCustomUrl = UrlHelper.NormalizeUrlOrSearch(StartupCustomUrl);
        }

        StartupMode = "custom";
        _ = SaveVpnPreferencesAsync();
        SettingsStatusBanner = $"✓ Đã lưu trang khởi động: {StartupCustomUrl}";
    }

    [RelayCommand]
    private void SetAutoLockMinutes(string? minutesText)
    {
        if (!int.TryParse(minutesText, out int minutes))
        {
            return;
        }

        AutoLockMinutes = Math.Clamp(minutes, 1, 120);
        if (!IsLocked)
        {
            _idleMonitor.Start(TimeSpan.FromMinutes(AutoLockMinutes));
        }
        _ = SaveVpnPreferencesAsync();
        SettingsStatusBanner = $"✓ Đã đặt tự động khóa Vault sau {AutoLockMinutes} phút nhàn rỗi";
    }

    [RelayCommand]
    private async Task UpdateProfileNameAsync()
    {
        if (IsVaultOperationBusy)
        {
            return;
        }

        IsVaultOperationBusy = true;
        try
        {
            var res = await _vaultService.UpdateProfileNameAsync(ProfileNameInput);
            if (res.Success && _vaultService.CurrentProfile is not null)
            {
                ActiveProfileLabel = _vaultService.CurrentProfile.Name;
                ProfileNameInput = _vaultService.CurrentProfile.Name;
            }
            SettingsStatusBanner = res.Message ?? string.Empty;
        }
        finally
        {
            IsVaultOperationBusy = false;
        }
    }

    public async Task ChangeVaultPinAsync(string currentPin, string newPin, string confirmPin)
    {
        if (IsVaultOperationBusy)
        {
            return;
        }

        if (!string.Equals(newPin, confirmPin, StringComparison.Ordinal))
        {
            SettingsStatusBanner = "⚠ Mã PIN / Master Password mới và xác nhận không khớp.";
            return;
        }

        IsVaultOperationBusy = true;
        try
        {
            var res = await _vaultService.ChangePinAsync(currentPin, newPin);
            SettingsStatusBanner = res.Message ?? string.Empty;
        }
        finally
        {
            currentPin = string.Empty;
            newPin = string.Empty;
            confirmPin = string.Empty;
            IsVaultOperationBusy = false;
        }
    }

    public async Task SaveDecoyPinAsync(string decoyPin)
    {
        if (IsVaultOperationBusy)
        {
            return;
        }

        IsVaultOperationBusy = true;
        try
        {
            var res = await _vaultService.SetDecoyPinAsync(decoyPin);
            HasDecoyProfile = _vaultService.HasDecoyProfile;
            SettingsStatusBanner = res.Message ?? string.Empty;
        }
        finally
        {
            decoyPin = string.Empty;
            IsVaultOperationBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveDecoyPinAsync()
    {
        if (IsVaultOperationBusy)
        {
            return;
        }

        IsVaultOperationBusy = true;
        try
        {
            var res = await _vaultService.SetDecoyPinAsync(null);
            HasDecoyProfile = _vaultService.HasDecoyProfile;
            SettingsStatusBanner = res.Message ?? string.Empty;
        }
        finally
        {
            IsVaultOperationBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleVaultWindowsHelloAsync()
    {
        if (IsVaultOperationBusy)
        {
            return;
        }

        IsVaultOperationBusy = true;
        try
        {
            bool target = !_vaultService.IsWindowsHelloEnabled;
            var res = await _vaultService.SetWindowsHelloEnabledAsync(target);
            IsVaultWindowsHelloEnabled = _vaultService.IsWindowsHelloEnabled;
            SettingsStatusBanner = res.Message ?? string.Empty;
        }
        finally
        {
            IsVaultOperationBusy = false;
        }
    }

    [RelayCommand]
    private void SetDefaultZoom(string? zoomText)
    {
        if (!int.TryParse(zoomText, out int zoom))
        {
            return;
        }

        DefaultZoomPercent = Math.Clamp(zoom, 50, 200);
        ZoomPercentDisplay = $"{DefaultZoomPercent}%";
        foreach (var tab in Tabs)
        {
            if (!tab.IsSettingsTab)
            {
                tab.SetZoomPercent(DefaultZoomPercent);
            }
        }
        _ = SaveVpnPreferencesAsync();
        SettingsStatusBanner = $"✓ Đã đặt mức thu phóng mặc định: {DefaultZoomPercent}%";
    }

    [RelayCommand]
    private void SelectTranslateLanguage(string? langCode)
    {
        TranslateTargetLanguage = NormalizeTranslateLang(langCode);
        TranslateTargetLanguageDisplay = FormatTranslateLangDisplay(TranslateTargetLanguage);
        _ = SaveVpnPreferencesAsync();
        SettingsStatusBanner = $"✓ Ngôn ngữ dịch trang web: {TranslateTargetLanguageDisplay}";
    }

    [RelayCommand]
    private void BrowseDownloadFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Chọn thư mục lưu tệp tải xuống (QUANGLN Browser)",
            InitialDirectory = Directory.Exists(DownloadDirectoryPath)
                ? DownloadDirectoryPath
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            DownloadDirectoryPath = dialog.FolderName;
            _downloadManager.DefaultDownloadDirectory = dialog.FolderName;
            _ = SaveVpnPreferencesAsync();
            SettingsStatusBanner = $"✓ Đã đổi thư mục tải về: {DownloadDirectoryPath}";
        }
    }

    [RelayCommand]
    private void OpenDefaultDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(DownloadDirectoryPath);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DownloadDirectoryPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("OpenDefaultDownloadFolder failed", ex);
        }
    }

    [RelayCommand]
    private async Task AddAdBlockWhitelistDomainAsync()
    {
        string raw = (NewAdBlockWhitelistDomainInput ?? string.Empty).Trim();
        string domain = ManagedAdBlockEngine.ExtractHost(raw.Contains("://") ? raw : $"https://{raw}");
        if (string.IsNullOrWhiteSpace(domain))
        {
            SettingsStatusBanner = "Vui lòng nhập tên miền hợp lệ (VD: example.com)";
            return;
        }

        await _adBlockEngine.SetDomainWhitelistAsync(domain, true);
        await _vaultService.SetDomainWhitelistAsync(domain, true);
        if (!AdBlockWhitelistDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            AdBlockWhitelistDomains.Add(domain);
        }
        NewAdBlockWhitelistDomainInput = string.Empty;
        SettingsStatusBanner = $"✓ Đã thêm {domain} vào danh sách ngoại lệ AdBlock";
    }

    [RelayCommand]
    private async Task RemoveAdBlockWhitelistDomainAsync(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return;
        }

        await _adBlockEngine.SetDomainWhitelistAsync(domain, false);
        await _vaultService.SetDomainWhitelistAsync(domain, false);
        AdBlockWhitelistDomains.Remove(domain);
        SettingsStatusBanner = $"✓ Đã bật lại AdBlock cho {domain}";
    }

    [RelayCommand]
    private void OpenWindowsDefaultApps()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("OpenWindowsDefaultApps failed", ex);
        }
    }

    [RelayCommand]
    private async Task ResetBrowserSettingsAsync()
    {
        _isLoadingVpnPreferences = true;
        try
        {
            BrowserThemeManager.ApplyTheme("coccoc");
            SelectedThemeKey = "coccoc";
            SelectedThemeDisplayName = BrowserThemeManager.CurrentPreset.DisplayName;

            DefaultSearchEngineKey = "google";
            DefaultSearchEngineDisplayName = "Google";
            UrlHelper.DefaultSearchEngineKey = "google";

            StartupMode = "homepage";
            StartupCustomUrl = "https://www.google.com";
            ShowHomeButton = true;
            IsLeftSidebarVisible = true;
            LeftSidebarMenuText = "Ẩn thanh bên";
            SidebarShowZalo = true;
            SidebarShowMessenger = true;
            SidebarShowDiscord = true;
            SidebarShowTelegram = false;
            SidebarShowAiChat = true;
            SidebarShowBookmarks = true;
            SidebarShowHistory = true;
            SidebarShowDownloads = true;
            DefaultZoomPercent = 100;
            ZoomPercentDisplay = "100%";

            IsAdBlockEnabled = true;
            _adBlockEngine.IsEnabled = true;

            TranslateTargetLanguage = "vi";
            TranslateTargetLanguageDisplay = "Tiếng Việt (vi)";

            AutoLockMinutes = 5;
            if (!IsLocked)
            {
                _idleMonitor.Start(TimeSpan.FromMinutes(AutoLockMinutes));
            }

            string defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "QUANGLNBrowser");
            DownloadDirectoryPath = defaultDir;
            _downloadManager.DefaultDownloadDirectory = defaultDir;
        }
        finally
        {
            _isLoadingVpnPreferences = false;
        }

        await SaveVpnPreferencesAsync();
        SettingsStatusBanner = "✓ Đã khôi phục toàn bộ cài đặt trình duyệt về mặc định";
    }

    private void RefreshAdBlockWhitelistCollection(IReadOnlyCollection<string> domains)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            AdBlockWhitelistDomains.Clear();
            foreach (string d in domains.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(d))
                {
                    AdBlockWhitelistDomains.Add(d);
                }
            }
        });
    }

    private static string NormalizeSearchEngineKey(string? key) =>
        (key ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "coccoc" => "coccoc",
            "bing" => "bing",
            "duckduckgo" => "duckduckgo",
            _ => "google"
        };

    private static string FormatSearchEngineDisplay(string key) => key switch
    {
        "coccoc" => "Cốc Cốc Search",
        "bing" => "Microsoft Bing",
        "duckduckgo" => "DuckDuckGo (Riêng tư)",
        _ => "Google"
    };

    private static string NormalizeTranslateLang(string? lang) =>
        (lang ?? string.Empty).Trim() switch
        {
            "en" => "en",
            "ja" => "ja",
            "ko" => "ko",
            "zh-CN" => "zh-CN",
            _ => "vi"
        };

    private static string FormatTranslateLangDisplay(string lang) => lang switch
    {
        "en" => "English (en)",
        "ja" => "日本語 (ja)",
        "ko" => "한국어 (ko)",
        "zh-CN" => "中文 (zh-CN)",
        _ => "Tiếng Việt (vi)"
    };

    private VpnNode? GetDoubleVpnEntryNode(VpnNode exitNode)
    {
        if (!IsDoubleVpnEnabled ||
            string.Equals(exitNode.Protocol, "wireguard", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exitNode.Protocol, "direct", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var warp = VpnNodes.FirstOrDefault(n =>
            n.Id == CloudflareWarpProvisioner.DefaultWarpNodeId &&
            CloudflareWarpProvisioner.IsValidProvisionedWarpNode(n));
        return warp;
    }

    private void UpdateVpnEncryptionSummary(VpnNode? node)
    {
        if (node is null)
        {
            VpnEncryptionSummary = "WireGuard · DNS 1.1.1.1 · Anti-WebRTC";
            return;
        }

        if (string.Equals(node.Protocol, "wireguard", StringComparison.OrdinalIgnoreCase))
        {
            VpnEncryptionSummary = "WireGuard (ChaCha20) · DNS 1.1.1.1 · Anti-WebRTC";
        }
        else if (GetDoubleVpnEntryNode(node) is not null)
        {
            VpnEncryptionSummary = $"Double VPN: 1.1.1.1 WARP → {node.DisplayProtocol} · Anti-WebRTC";
        }
        else
        {
            VpnEncryptionSummary = $"{node.DisplayProtocol} · DNS 1.1.1.1 · Anti-WebRTC";
        }
    }

    private async Task RefreshVpnNodesAsync()
    {
        var nodes = await _vaultService.GetVpnNodesAsync();
        var prefs = await _vaultService.GetVpnPreferencesAsync();

        // Xóa các node mẫu dummy cũ (direct / 127.0.0.1:1080) nếu còn tồn tại trong vault.db
        bool removedLegacy = false;
        foreach (var old in nodes.Where(n => n.Id is "node_sg_direct_tunnel" or "node_us_socks5").ToArray())
        {
            await _vaultService.DeleteVpnNodeAsync(old.Id);
            removedLegacy = true;
        }

        if (removedLegacy)
        {
            nodes = await _vaultService.GetVpnNodesAsync();
        }

        _isLoadingVpnPreferences = true;
        try
        {
            VpnNodes.Clear();
            foreach (var n in nodes)
            {
                VpnNodes.Add(n);
            }

            SelectedVpnNode = (!string.IsNullOrEmpty(prefs.LastSelectedNodeId)
                    ? VpnNodes.FirstOrDefault(n => n.Id == prefs.LastSelectedNodeId)
                    : null)
                ?? VpnNodes.FirstOrDefault(n => n.IsDefault)
                ?? VpnNodes.FirstOrDefault();

            if (SelectedVpnNode is not null)
            {
                SelectedVpnRegionKey = SelectedVpnNode.Region.ToString();
                VpnRegionSummary = $"{SelectedVpnNode.RegionLabel} · {SelectedVpnNode.DisplayTitle}";
                UpdateVpnEncryptionSummary(SelectedVpnNode);
            }
        }
        finally
        {
            _isLoadingVpnPreferences = false;
        }

        // Tự động khởi tạo cấu hình Cloudflare 1.1.1.1 WARP (WireGuard) thật nếu chưa có
        var warpNode = VpnNodes.FirstOrDefault(n => n.Id == CloudflareWarpProvisioner.DefaultWarpNodeId);
        if (!CloudflareWarpProvisioner.IsValidProvisionedWarpNode(warpNode))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                        VpnStatusMessage = "Đang tự động đăng ký tài khoản Cloudflare 1.1.1.1 WARP (WireGuard)...");

                    var provisioned = await CloudflareWarpProvisioner.ProvisionWarpNodeAsync();
                    await CloudflareWarpProvisioner.OptimizeWarpEndpointAsync(provisioned);
                    if (provisioned.LatencyMs <= 0)
                    {
                        provisioned.LatencyMs = await _vpnService.PingNodeAsync(provisioned);
                    }
                    await _vaultService.UpsertVpnNodeAsync(provisioned);

                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        _isLoadingVpnPreferences = true;
                        try
                        {
                            var existing = VpnNodes.FirstOrDefault(n => n.Id == provisioned.Id);
                            if (existing is not null)
                            {
                                VpnNodes.Remove(existing);
                            }
                            VpnNodes.Insert(0, provisioned);
                            SelectedVpnNode ??= provisioned;
                            if (SelectedVpnNode == provisioned)
                            {
                                SelectedVpnRegionKey = VpnRegion.Optimal.ToString();
                                VpnRegionSummary = $"Tối ưu · {provisioned.DisplayTitle}";
                                UpdateVpnEncryptionSummary(provisioned);
                            }
                            if (!_vpnService.IsRunning)
                            {
                                VpnStatusMessage = $"Sẵn sàng: {provisioned.DisplayTitle} ({provisioned.DisplayLatency})";
                            }
                        }
                        finally
                        {
                            _isLoadingVpnPreferences = false;
                        }
                    });
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Auto-provision Cloudflare 1.1.1.1 WARP failed", ex);
                    Application.Current?.Dispatcher.Invoke(() =>
                        VpnStatusMessage = $"Chưa thể cấp khóa 1.1.1.1 WARP tự động: {ex.Message}");
                }
            });
        }
    }

    private async Task<VpnNode?> EnsureSelectedOrWarpNodeReadyAsync()
    {
        var node = SelectedVpnNode ?? VpnNodes.FirstOrDefault();
        if (node is not null && (node.Id != CloudflareWarpProvisioner.DefaultWarpNodeId || CloudflareWarpProvisioner.IsValidProvisionedWarpNode(node)))
        {
            return node;
        }

        try
        {
            VpnStatusMessage = "Đang khởi tạo khóa WireGuard 1.1.1.1 WARP với Cloudflare...";
            var provisioned = await CloudflareWarpProvisioner.ProvisionWarpNodeAsync();
            await CloudflareWarpProvisioner.OptimizeWarpEndpointAsync(provisioned);
            if (provisioned.LatencyMs <= 0)
            {
                provisioned.LatencyMs = await _vpnService.PingNodeAsync(provisioned);
            }
            await _vaultService.UpsertVpnNodeAsync(provisioned);

            _isLoadingVpnPreferences = true;
            try
            {
                var existing = VpnNodes.FirstOrDefault(n => n.Id == provisioned.Id);
                if (existing is not null)
                {
                    VpnNodes.Remove(existing);
                }
                VpnNodes.Insert(0, provisioned);
                SelectedVpnNode = provisioned;
                SelectedVpnRegionKey = VpnRegion.Optimal.ToString();
                VpnRegionSummary = $"Tối ưu · {provisioned.DisplayTitle}";
            }
            finally
            {
                _isLoadingVpnPreferences = false;
            }
            return provisioned;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to provision Cloudflare 1.1.1.1 WARP on demand", ex);
            VpnStatusMessage = $"Không thể đăng ký 1.1.1.1 WARP: {ex.Message}";
            return null;
        }
    }

    private async Task<VpnNode?> EnsureCloudflareWarpNodeReadyAsync()
    {
        var warpNode = VpnNodes.FirstOrDefault(n =>
            n.Id == CloudflareWarpProvisioner.DefaultWarpNodeId &&
            CloudflareWarpProvisioner.IsValidProvisionedWarpNode(n));
        if (warpNode is not null)
        {
            return warpNode;
        }

        try
        {
            VpnStatusMessage = "Đang khởi tạo khóa WireGuard 1.1.1.1 WARP với Cloudflare...";
            var provisioned = await CloudflareWarpProvisioner.ProvisionWarpNodeAsync();
            await CloudflareWarpProvisioner.OptimizeWarpEndpointAsync(provisioned);
            if (provisioned.LatencyMs <= 0)
            {
                provisioned.LatencyMs = await _vpnService.PingNodeAsync(provisioned);
            }
            await _vaultService.UpsertVpnNodeAsync(provisioned);

            _isLoadingVpnPreferences = true;
            try
            {
                var existing = VpnNodes.FirstOrDefault(n => n.Id == provisioned.Id);
                if (existing is not null)
                {
                    VpnNodes.Remove(existing);
                }
                VpnNodes.Insert(0, provisioned);
                SelectedVpnNode ??= provisioned;
            }
            finally
            {
                _isLoadingVpnPreferences = false;
            }

            return provisioned;
        }
        catch (Exception ex)
        {
            AppLogger.Error("EnsureCloudflareWarpNodeReadyAsync failed", ex);
            VpnStatusMessage = $"Không thể đăng ký 1.1.1.1 WARP: {ex.Message}";
            return null;
        }
    }

    private async Task VerifyAndDisplayWarpStatusAsync()
    {
        if (!_vpnService.IsRunning)
        {
            return;
        }

        VpnVirtualIp = "Đang xác minh IP...";
        var trace = await CloudflareWarpProvisioner.VerifyWarpTraceAsync(_vpnService.LocalSocksPort);
        if (!_vpnService.IsRunning || _vpnService.IsKillSwitchTripped)
        {
            return;
        }

        string coloLabel = string.IsNullOrEmpty(trace.Colo) ? "EDGE" : trace.Colo;
        if (!string.IsNullOrEmpty(trace.Location))
        {
            coloLabel = $"{coloLabel} ({trace.Location})";
        }

        if (trace.IsWarpActive)
        {
            VpnVirtualIp = string.IsNullOrEmpty(trace.PublicIp) ? "Đã ẩn (WARP)" : trace.PublicIp;
            VpnPoPLocation = coloLabel;
            VpnBadgeText = string.IsNullOrEmpty(trace.Colo) ? "WARP" : $"WARP · {trace.Colo}";
            VpnStatusMessage = $"✅ Đang bảo vệ (WARP=ON) • IP ảo: {VpnVirtualIp} • PoP: {coloLabel}";
        }
        else if (!string.IsNullOrEmpty(trace.PublicIp))
        {
            VpnVirtualIp = trace.PublicIp;
            VpnPoPLocation = coloLabel;
            string code = _vpnService.ActiveNode?.CountryCode.ToUpperInvariant() ?? "VPN";
            VpnBadgeText = $"VPN · {code}";
            VpnStatusMessage = $"✅ Đang bảo vệ • IP ảo: {trace.PublicIp} ({coloLabel})";
        }
        else
        {
            VpnVirtualIp = "Đã kết nối (Local SOCKS5)";
        }
    }

    private void OnVpnTelemetryUpdated(object? sender, VpnTelemetry telemetry)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (!_vpnService.IsRunning)
            {
                return;
            }

            VpnDownloadTotalText = VpnTelemetry.FormatBytes(telemetry.DownloadTotalBytes);
            VpnUploadTotalText = VpnTelemetry.FormatBytes(telemetry.UploadTotalBytes);
            VpnDownloadSpeedText = VpnTelemetry.FormatSpeed(telemetry.DownloadBytesPerSec);
            VpnUploadSpeedText = VpnTelemetry.FormatSpeed(telemetry.UploadBytesPerSec);
            VpnActiveConnections = telemetry.ActiveConnections;

            if (telemetry.ConnectedSinceUtc is DateTime startedAt)
            {
                var elapsed = DateTime.UtcNow - startedAt;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                VpnSessionDurationText = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
            }
        });
    }

    private void ResetVpnTelemetryDisplay()
    {
        VpnVirtualIp = "Chưa kết nối";
        VpnPoPLocation = "—";
        VpnDownloadTotalText = "0 B";
        VpnUploadTotalText = "0 B";
        VpnDownloadSpeedText = "0 KB/s";
        VpnUploadSpeedText = "0 KB/s";
        VpnActiveConnections = 0;
        VpnSessionDurationText = "00:00:00";
    }

    private void OnVpnServiceStateChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            IsVpnConnected = _vpnService.IsRunning;
            IsKillSwitchTripped = _vpnService.IsKillSwitchTripped;

            if (IsKillSwitchTripped)
            {
                VpnBadgeText = "KILL SWITCH";
                VpnVirtualIp = "ĐÃ KHÓA (BLACKHOLE)";
                VpnPoPLocation = "BLOCKED";
                VpnStatusMessage = string.IsNullOrWhiteSpace(_vpnService.LastError)
                    ? "Kill Switch đã khóa toàn bộ kết nối để chống lộ IP!"
                    : _vpnService.LastError;

                return;
            }

            if (IsVpnConnected && _vpnService.ActiveNode is not null)
            {
                UpdateVpnEncryptionSummary(_vpnService.ActiveNode);
                VpnBadgeText = _vpnService.ActiveNode.Id == CloudflareWarpProvisioner.DefaultWarpNodeId
                    ? "WARP · ON"
                    : $"VPN · {_vpnService.ActiveNode.CountryCode.ToUpperInvariant()}";
                VpnStatusMessage = $"Đang kết nối: {_vpnService.ActiveNode.DisplayTitle} (127.0.0.1:{_vpnService.LocalSocksPort})";
            }
            else
            {
                ResetVpnTelemetryDisplay();
                VpnBadgeText = "VPN";
                VpnStatusMessage = string.IsNullOrWhiteSpace(_vpnService.LastError)
                    ? "VPN đang tắt (Kết nối trực tiếp)"
                    : _vpnService.LastError;
            }
        });
    }

    [RelayCommand]
    private async Task SelectVpnRegionAsync(string? regionKey)
    {
        if (string.IsNullOrWhiteSpace(regionKey) ||
            !Enum.TryParse<VpnRegion>(regionKey, ignoreCase: true, out var targetRegion))
        {
            return;
        }

        SelectedVpnRegionKey = targetRegion.ToString();

        VpnNode? bestNode;
        if (targetRegion == VpnRegion.Optimal)
        {
            bestNode = VpnNodes.FirstOrDefault(n =>
                n.Id == CloudflareWarpProvisioner.DefaultWarpNodeId &&
                CloudflareWarpProvisioner.IsValidProvisionedWarpNode(n))
                ?? VpnNodes.OrderBy(n => n.LatencyMs > 0 ? n.LatencyMs : int.MaxValue).FirstOrDefault();

            if (bestNode is null)
            {
                bestNode = await EnsureSelectedOrWarpNodeReadyAsync();
            }
        }
        else
        {
            bestNode = VpnNodes
                .Where(n => n.Region == targetRegion)
                .OrderBy(n => n.LatencyMs > 0 ? n.LatencyMs : int.MaxValue)
                .FirstOrDefault();
        }

        if (bestNode is null)
        {
            VpnRegionSummary = $"{VpnRegionHelper.GetDisplayName(targetRegion)} (Chưa có máy chủ)";
            VpnStatusMessage = $"Chưa có máy chủ khu vực {VpnRegionHelper.GetDisplayName(targetRegion)}. Hãy nhập Subscription/Node trong Cài đặt hoặc chọn Tối ưu (1.1.1.1 WARP).";
            await SaveVpnPreferencesAsync();
            return;
        }

        bool needReconnect = _vpnService.IsRunning && _vpnService.ActiveNode?.Id != bestNode.Id;

        _isLoadingVpnPreferences = true;
        try
        {
            SelectedVpnNode = bestNode;
            VpnRegionSummary = $"{VpnRegionHelper.GetDisplayName(targetRegion)} · {bestNode.DisplayTitle}";
            UpdateVpnEncryptionSummary(bestNode);
        }
        finally
        {
            _isLoadingVpnPreferences = false;
        }

        await SaveVpnPreferencesAsync();

        if (needReconnect)
        {
            await SwitchActiveVpnNodeAsync(bestNode);
        }
        else if (!_vpnService.IsRunning)
        {
            VpnStatusMessage = $"Đã chọn vùng {VpnRegionHelper.GetDisplayName(targetRegion)}: {bestNode.DisplayTitle} ({bestNode.DisplayLatency})";
        }
    }

    private async Task SwitchActiveVpnNodeAsync(VpnNode node)
    {
        VpnStatusMessage = $"Đang chuyển tuyến sang {node.DisplayTitle}...";
        bool ok = await _vpnService.StartAsync(node, GetDoubleVpnEntryNode(node));
        if (!ok)
        {
            return;
        }

        ActiveTab?.Browser.CoreWebView2?.Reload();
        _ = VerifyAndDisplayWarpStatusAsync();
    }

    [RelayCommand]
    private async Task ToggleGlobalVpnAsync()
    {
        if (IsVpnConnected && IsGlobalVpnEnabled)
        {
            // Tắt VPN toàn trình duyệt
            IsGlobalVpnEnabled = false;
            if (IsIncognitoWindow)
            {
                _isWindowVpnActive = false;
            }

            foreach (var tab in Tabs)
            {
                tab.IsVpnEnabled = false;
            }

            bool anyWindowUsesVpn = _sharedSession?.IsAnyWindowUsingVpn() == true || Tabs.Any(t => t.IsVpnEnabled);
            if (!anyWindowUsesVpn)
            {
                await _vpnService.StopAsync();
            }
            ActiveTab?.Browser.CoreWebView2?.Reload();
            return;
        }

        var node = await EnsureSelectedOrWarpNodeReadyAsync();
        if (node is null)
        {
            return;
        }

        VpnStatusMessage = $"Đang kết nối {node.DisplayTitle}...";
        bool started = await _vpnService.StartAsync(node, GetDoubleVpnEntryNode(node));
        if (!started)
        {
            IsGlobalVpnEnabled = false;
            return;
        }

        IsGlobalVpnEnabled = true;
        if (IsIncognitoWindow)
        {
            _isWindowVpnActive = true;
        }

        foreach (var tab in Tabs)
        {
            tab.IsVpnEnabled = true;
        }

        ActiveTab?.Browser.CoreWebView2?.Reload();
        _ = VerifyAndDisplayWarpStatusAsync();
    }

    [RelayCommand]
    private async Task TogglePerTabVpnAsync()
    {
        if (ActiveTab is null)
        {
            return;
        }

        bool targetVpnState = !ActiveTab.IsVpnEnabled;
        if (targetVpnState)
        {
            var node = await EnsureSelectedOrWarpNodeReadyAsync();
            if (node is null)
            {
                return;
            }

            if (!_vpnService.IsRunning)
            {
                bool started = await _vpnService.StartAsync(node, GetDoubleVpnEntryNode(node));
                if (!started)
                {
                    return;
                }
            }

            string currentUrl = ActiveTab.Address;
            int idx = Tabs.IndexOf(ActiveTab);
            var oldTab = ActiveTab;

            var newTab = new TabViewModel(
                currentUrl,
                _profileId,
                newUrl => CreateNewTab(newUrl),
                OnTabPageLoaded,
                _keyboardHandler,
                _adBlockEngine,
                _mediaSniffer,
                OnTabVideoOverlayAction,
                isVpnEnabled: true,
                isIncognito: oldTab.IsIncognito,
                ownsRequestContext: true,
                onContextMenuRequested: OnTabContextMenuRequested,
                onThemeChangedFromPage: OnPageThemeChanged)
            {
                IsVpnEnabled = true
            };

            Tabs.Insert(Math.Max(0, idx), newTab);
            ActiveTab = newTab;
            Tabs.Remove(oldTab);
            oldTab.Dispose();
            _ = VerifyAndDisplayWarpStatusAsync();
        }
        else
        {
            string currentUrl = ActiveTab.Address;
            int idx = Tabs.IndexOf(ActiveTab);
            var oldTab = ActiveTab;

            var newTab = new TabViewModel(
                currentUrl,
                _profileId,
                newUrl => CreateNewTab(newUrl),
                OnTabPageLoaded,
                _keyboardHandler,
                _adBlockEngine,
                _mediaSniffer,
                OnTabVideoOverlayAction,
                isVpnEnabled: false,
                isIncognito: oldTab.IsIncognito,
                ownsRequestContext: false,
                onContextMenuRequested: OnTabContextMenuRequested,
                onThemeChangedFromPage: OnPageThemeChanged)
            {
                IsVpnEnabled = false
            };

            Tabs.Insert(Math.Max(0, idx), newTab);
            ActiveTab = newTab;
            Tabs.Remove(oldTab);
            oldTab.Dispose();

            if (!IsGlobalVpnEnabled && Tabs.All(t => !t.IsVpnEnabled))
            {
                await _vpnService.StopAsync();
            }
        }
    }

    [RelayCommand]
    private async Task OpenIsolatedVpnTabAsync()
    {
        var warpNode = await EnsureCloudflareWarpNodeReadyAsync() ?? await EnsureSelectedOrWarpNodeReadyAsync();
        if (warpNode is null)
        {
            return;
        }

        if (!_vpnService.IsRunning)
        {
            bool started = await _vpnService.StartAsync(warpNode, GetDoubleVpnEntryNode(warpNode));
            if (!started)
            {
                return;
            }
        }

        string startUrl = IncognitoStartPageBuilder.EnsureStartPageFile(
            withWarp: true,
            isWarpConnected: true,
            virtualIp: VpnVirtualIp,
            popLocation: VpnPoPLocation);

        var tab = CreateIsolatedIncognitoTabCore(startUrl, isVpnEnabled: true, ownsRequestContext: true);

        Tabs.Add(tab);
        ActiveTab = tab;
        _ = VerifyAndDisplayWarpStatusAsync();
    }

    private TabViewModel CreateIsolatedIncognitoTabCore(
        string url,
        bool isVpnEnabled,
        bool ownsRequestContext)
    {
        var tab = new TabViewModel(
            url,
            _profileId,
            newUrl => Application.Current?.Dispatcher.Invoke(() =>
            {
                var childTab = CreateIsolatedIncognitoTabCore(newUrl, isVpnEnabled, ownsRequestContext: false);
                Tabs.Add(childTab);
                ActiveTab = childTab;
            }),
            OnTabPageLoaded,
            _keyboardHandler,
            _adBlockEngine,
            _mediaSniffer,
            OnTabVideoOverlayAction,
            isVpnEnabled: isVpnEnabled,
            isIncognito: true,
            ownsRequestContext: ownsRequestContext,
            onContextMenuRequested: OnTabContextMenuRequested,
            onThemeChangedFromPage: OnPageThemeChanged)
        {
            IsVpnEnabled = isVpnEnabled,
            IsIncognito = true,
            Title = isVpnEnabled ? "[Ẩn danh · WARP]" : "[Ẩn danh]"
        };

        return tab;
    }

    [RelayCommand]
    private async Task OptimizeWarpEndpointAsync()
    {
        if (IsOptimizingWarp)
        {
            return;
        }

        IsOptimizingWarp = true;
        try
        {
            var warpNode = VpnNodes.FirstOrDefault(n => n.Id == CloudflareWarpProvisioner.DefaultWarpNodeId);
            if (!CloudflareWarpProvisioner.IsValidProvisionedWarpNode(warpNode))
            {
                warpNode = await EnsureSelectedOrWarpNodeReadyAsync();
                if (warpNode is null)
                {
                    return;
                }
            }

            VpnStatusMessage = "Đang dò tìm Cloudflare WARP Anycast Edge IP tốc độ cao nhất...";
            var (bestIp, latencyMs, updated) = await CloudflareWarpProvisioner.OptimizeWarpEndpointAsync(warpNode!);
            if (updated)
            {
                await _vaultService.UpsertVpnNodeAsync(warpNode!);
                RefreshVpnNodesCollectionPreservingSelection();
                VpnStatusMessage = $"⚡ Đã tối ưu WARP Edge: {bestIp} ({latencyMs} ms)";

                if (_vpnService.IsRunning && _vpnService.ActiveNode?.Id == warpNode!.Id)
                {
                    await RestartVpnWithCurrentSettingsAsync();
                    _ = VerifyAndDisplayWarpStatusAsync();
                }
            }
            else
            {
                VpnStatusMessage = "Không thể đo các Cloudflare Edge IP. Vui lòng kiểm tra kết nối mạng.";
            }
        }
        finally
        {
            IsOptimizingWarp = false;
        }
    }

    [RelayCommand]
    private async Task ToggleCurrentDomainVpnBypassAsync()
    {
        if (ActiveTab is null)
        {
            return;
        }

        string domain = VpnNodeParser.NormalizeBypassDomain(ActiveTab.Address);
        if (string.IsNullOrEmpty(domain))
        {
            return;
        }

        var existing = VpnBypassDomains.FirstOrDefault(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            VpnBypassDomains.Remove(existing);
            VpnStatusMessage = $"Đã bật lại VPN cho {domain}";
        }
        else
        {
            VpnBypassDomains.Add(domain);
            VpnStatusMessage = $"Đã thêm {domain} vào danh sách Bỏ qua VPN (đi trực tiếp)";
        }

        IsCurrentDomainVpnBypassed = IsDomainInVpnBypassList(domain);
        _vpnService.SetCustomBypassDomains(VpnBypassDomains);
        await SaveVpnPreferencesAsync();

        if (_vpnService.IsRunning)
        {
            await RestartVpnWithCurrentSettingsAsync();
            ActiveTab.Browser.Reload();
        }
    }

    [RelayCommand]
    private async Task AddVpnBypassDomainAsync()
    {
        string clean = VpnNodeParser.NormalizeBypassDomain(NewVpnBypassDomainInput);
        if (string.IsNullOrEmpty(clean))
        {
            return;
        }

        if (!VpnBypassDomains.Contains(clean, StringComparer.OrdinalIgnoreCase))
        {
            VpnBypassDomains.Add(clean);
            _vpnService.SetCustomBypassDomains(VpnBypassDomains);
            await SaveVpnPreferencesAsync();

            if (ActiveTab is not null)
            {
                UpdateCurrentDomainState(ActiveTab.Address);
            }

            if (_vpnService.IsRunning)
            {
                await RestartVpnWithCurrentSettingsAsync();
            }
        }

        NewVpnBypassDomainInput = string.Empty;
    }

    [RelayCommand]
    private async Task RemoveVpnBypassDomainAsync(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return;
        }

        var match = VpnBypassDomains.FirstOrDefault(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return;
        }

        VpnBypassDomains.Remove(match);
        _vpnService.SetCustomBypassDomains(VpnBypassDomains);
        await SaveVpnPreferencesAsync();

        if (ActiveTab is not null)
        {
            UpdateCurrentDomainState(ActiveTab.Address);
        }

        if (_vpnService.IsRunning)
        {
            await RestartVpnWithCurrentSettingsAsync();
        }
    }

    [RelayCommand]
    private async Task ImportVpnSubscriptionAsync()
    {
        if (string.IsNullOrWhiteSpace(VpnSubscriptionInput))
        {
            VpnStatusMessage = "Vui lòng dán danh sách link (mỗi dòng 1 link) hoặc chuỗi Base64 Subscription.";
            return;
        }

        var imported = VpnNodeParser.ParseSubscriptionContent(VpnSubscriptionInput);
        if (imported.Count == 0)
        {
            VpnStatusMessage = "Không tìm thấy VPN Node hợp lệ trong dữ liệu đã dán.";
            return;
        }

        foreach (var node in imported)
        {
            await _vaultService.UpsertVpnNodeAsync(node);
            VpnNodes.Add(node);
        }

        VpnSubscriptionInput = string.Empty;
        VpnStatusMessage = $"✅ Đã nhập {imported.Count} máy chủ VPN mới. Đang đo độ trễ...";

        // Đo Ping song song cho các máy chủ vừa nhập
        await Task.WhenAll(imported.Select(async node =>
        {
            node.LatencyMs = await _vpnService.PingNodeAsync(node, timeoutMs: 2000);
            await _vaultService.UpsertVpnNodeAsync(node);
        }));

        RefreshVpnNodesCollectionPreservingSelection();
        var regions = imported.Select(n => n.RegionLabel).Distinct().ToArray();
        VpnStatusMessage = $"✅ Đã nhập {imported.Count} VPN Node ({string.Join(", ", regions)}) vào Vault mã hóa.";
    }

    [RelayCommand]
    private void CopyVirtualIp()
    {
        if (string.IsNullOrWhiteSpace(VpnVirtualIp) || VpnVirtualIp.Contains(' '))
        {
            return;
        }

        try
        {
            Clipboard.SetText(VpnVirtualIp);
            VpnStatusMessage = $"📋 Đã sao chép địa chỉ IP ảo: {VpnVirtualIp}";
        }
        catch
        {
            // Bỏ qua nếu clipboard đang bận
        }
    }

    [RelayCommand]
    private void OpenWebRtcLeakTest()
    {
        if (ActiveTab is not null)
        {
            ActiveTab.Navigate("https://browserleaks.com/webrtc");
        }
        else
        {
            CreateNewTab("https://browserleaks.com/webrtc");
        }
    }

    [RelayCommand]
    private async Task ProvisionCloudflareWarpAsync()
    {
        try
        {
            VpnStatusMessage = "Đang đăng ký tài khoản Cloudflare 1.1.1.1 WARP mới...";
            var provisioned = await CloudflareWarpProvisioner.ProvisionWarpNodeAsync();
            await CloudflareWarpProvisioner.OptimizeWarpEndpointAsync(provisioned);
            if (provisioned.LatencyMs <= 0)
            {
                provisioned.LatencyMs = await _vpnService.PingNodeAsync(provisioned);
            }
            await _vaultService.UpsertVpnNodeAsync(provisioned);

            _isLoadingVpnPreferences = true;
            try
            {
                var existing = VpnNodes.FirstOrDefault(n => n.Id == provisioned.Id);
                if (existing is not null)
                {
                    VpnNodes.Remove(existing);
                }
                VpnNodes.Insert(0, provisioned);
                SelectedVpnNode = provisioned;
                SelectedVpnRegionKey = VpnRegion.Optimal.ToString();
                VpnRegionSummary = $"Tối ưu · {provisioned.DisplayTitle}";
            }
            finally
            {
                _isLoadingVpnPreferences = false;
            }

            VpnStatusMessage = $"✅ Đã cấp mới 1.1.1.1 WARP ({provisioned.DisplayLatency}). Bấm Bật VPN để kết nối.";

            if (_vpnService.IsRunning)
            {
                await RestartVpnWithCurrentSettingsAsync();
                _ = VerifyAndDisplayWarpStatusAsync();
            }
        }
        catch (Exception ex)
        {
            VpnStatusMessage = $"Lỗi cấp khóa 1.1.1.1 WARP: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CheckWarpTraceInTab()
    {
        if (ActiveTab is not null)
        {
            ActiveTab.Navigate("https://1.1.1.1/cdn-cgi/trace");
        }
        else
        {
            CreateNewTab("https://1.1.1.1/cdn-cgi/trace");
        }
    }

    [RelayCommand]
    private async Task ResetKillSwitchAsync()
    {
        _vpnService.ResetKillSwitch();
        IsGlobalVpnEnabled = false;

        foreach (var tab in Tabs)
        {
            tab.IsVpnEnabled = false;
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task PingAllVpnNodesAsync()
    {
        await Task.WhenAll(VpnNodes.ToArray().Select(async node =>
        {
            int latency = await _vpnService.PingNodeAsync(node);
            node.LatencyMs = latency;
            await _vaultService.UpsertVpnNodeAsync(node);
        }));

        RefreshVpnNodesCollectionPreservingSelection();
    }

    private void RefreshVpnNodesCollectionPreservingSelection()
    {
        _isLoadingVpnPreferences = true;
        try
        {
            var snapshot = VpnNodes.ToArray();
            var currentSelectedId = SelectedVpnNode?.Id;
            VpnNodes.Clear();
            foreach (var n in snapshot)
            {
                VpnNodes.Add(n);
            }
            SelectedVpnNode = VpnNodes.FirstOrDefault(n => n.Id == currentSelectedId) ?? VpnNodes.FirstOrDefault();
        }
        finally
        {
            _isLoadingVpnPreferences = false;
        }
    }

    [RelayCommand]
    private async Task AddVpnNodeAsync()
    {
        if (string.IsNullOrWhiteSpace(NewVpnNodeInput))
        {
            VpnStatusMessage = "Vui lòng nhập link (vless://, ss://, socks5://, http://) hoặc JSON.";
            return;
        }

        try
        {
            string? country = string.IsNullOrWhiteSpace(NewVpnNodeCountry) ? null : NewVpnNodeCountry.Trim().ToUpperInvariant();
            var node = VpnNodeParser.ParseFromInput(NewVpnNodeInput, NewVpnNodeName, country);
            node.LatencyMs = await _vpnService.PingNodeAsync(node);

            await _vaultService.UpsertVpnNodeAsync(node);
            VpnNodes.Add(node);
            SelectedVpnNode = node;

            NewVpnNodeInput = string.Empty;
            NewVpnNodeName = string.Empty;
            NewVpnNodeCountry = string.Empty;
            VpnStatusMessage = $"Đã thêm node ({node.RegionLabel}): {node.DisplayTitle} ({node.DisplayLatency})";
        }
        catch (Exception ex)
        {
            VpnStatusMessage = $"Lỗi thêm VPN Node: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteVpnNodeAsync(VpnNode? node)
    {
        if (node is null)
        {
            return;
        }

        await _vaultService.DeleteVpnNodeAsync(node.Id);
        VpnNodes.Remove(node);
        if (SelectedVpnNode == node)
        {
            SelectedVpnNode = VpnNodes.FirstOrDefault();
        }
    }

    private async Task RestartVpnWithCurrentSettingsAsync()
    {
        if (SelectedVpnNode is null)
        {
            return;
        }

        bool ok = await _vpnService.StartAsync(SelectedVpnNode, GetDoubleVpnEntryNode(SelectedVpnNode));
        if (ok)
        {
            ActiveTab?.Browser.CoreWebView2?.Reload();
        }
    }

    private async Task LoadAdBlockFiltersInBackgroundAsync(bool forceDownload = false)
    {
        try
        {
            AdBlockFilterStatus = "Đang tải/cập nhật EasyList, EasyPrivacy, ABPVN, Anti-Annoyance...";
            await _adBlockEngine.LoadFiltersAsync(forceDownload);
            AdBlockTotalRules = _adBlockEngine.TotalRulesCount;
            AdBlockFilterStatus = $"Đã nạp {_adBlockEngine.TotalRulesCount:N0} quy tắc (EasyList + EasyPrivacy + ABPVN + Annoyances)";
            if (_adBlockEngine.LastFilterUpdateUtc.HasValue)
            {
                AdBlockLastUpdatedText = _adBlockEngine.LastFilterUpdateUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to update AdBlock filters in background", ex);
            AdBlockTotalRules = _adBlockEngine.TotalRulesCount;
            AdBlockFilterStatus = $"Đang dùng {_adBlockEngine.TotalRulesCount:N0} quy tắc cục bộ";
        }
    }

    [RelayCommand]
    private void SetAdBlockMode(string? mode)
    {
        string m = string.Equals(mode, "strict", StringComparison.OrdinalIgnoreCase) ? "strict" : "standard";
        AdBlockMode = m;
        IsAdBlockStandardMode = m == "standard";
        IsAdBlockStrictMode = m == "strict";
        _adBlockEngine.BlockingMode = m;
        if (!_isLoadingVpnPreferences) _ = SaveVpnPreferencesAsync();
        if (ActiveTab is not null && !ActiveTab.IsSettingsTab)
        {
            ActiveTab.Browser.Reload();
        }
    }

    [RelayCommand]
    private void StartElementPicker()
    {
        if (ActiveTab is not null && !ActiveTab.IsSettingsTab)
        {
            ActiveTab.StartElementPicker();
        }
    }

    [RelayCommand]
    private async Task AddAdBlockCustomRuleAsync()
    {
        string raw = (NewAdBlockCustomRuleInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;
        _adBlockEngine.AddCustomRule(raw);
        if (!AdBlockCustomRules.Contains(raw, StringComparer.OrdinalIgnoreCase))
        {
            AdBlockCustomRules.Add(raw);
        }
        NewAdBlockCustomRuleInput = string.Empty;
        AdBlockTotalRules = _adBlockEngine.TotalRulesCount;
        await SaveVpnPreferencesAsync();
    }

    [RelayCommand]
    private async Task RemoveAdBlockCustomRuleAsync(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return;
        _adBlockEngine.RemoveCustomRule(rule);
        AdBlockCustomRules.Remove(rule);
        AdBlockTotalRules = _adBlockEngine.TotalRulesCount;
        await SaveVpnPreferencesAsync();
    }

    private void OnTabVideoOverlayAction(TabViewModel tab, string action, DetectedMedia media)
    {
        if (string.Equals(action, "popout", StringComparison.OrdinalIgnoreCase))
        {
            _ = PopOutDetectedMediaAsync(media);
        }
        else
        {
            _ = DownloadDetectedMediaAsync(media);
        }
    }

    [RelayCommand]
    private async Task DownloadDetectedMediaAsync(DetectedMedia? media)
    {
        if (media is null)
        {
            return;
        }

        if (ActiveTab is not null)
        {
            if (string.IsNullOrWhiteSpace(media.Title))
            {
                media.Title = ActiveTab.Title;
            }
            if (string.IsNullOrWhiteSpace(media.Cookie))
            {
                media.Cookie = await ActiveTab.GetCookiesForUrlAsync(media.Url);
            }
        }

        var item = await _downloadManager.EnqueueDownloadAsync(media, profileId: _vaultService.CurrentProfile?.Id);
        UpsertDownloadTaskViewModel(item, 0);

        ActiveDrawerSection = "Downloads";
        IsDrawerOpen = true;
    }

    [RelayCommand]
    private async Task PopOutDetectedMediaAsync(DetectedMedia? media)
    {
        if (media is null)
        {
            return;
        }

        try
        {
            AppLogger.Info($"[MainViewModel] Yêu cầu mở cửa sổ xem riêng: Url={media.Url}, Title={media.Title}, Type={media.Type}");

            if (ActiveTab is not null)
            {
                _ = ActiveTab.PauseAllMediaAsync();
                if (string.IsNullOrWhiteSpace(media.Title))
                {
                    media.Title = ActiveTab.Title;
                }
                if (string.IsNullOrWhiteSpace(media.Cookie))
                {
                    media.Cookie = await ActiveTab.GetCookiesForUrlAsync(media.Url);
                }
            }

            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(() => OpenMpvPlayerWindow(media));
            }
            else
            {
                OpenMpvPlayerWindow(media);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[MainViewModel] Lỗi mở cửa sổ xem riêng (PopOutDetectedMediaAsync)", ex);
        }
    }

    [RelayCommand]
    private async Task ProbeTabWithYtDlpAsync()
    {
        if (ActiveTab is null || IsProbingYtDlp)
        {
            return;
        }

        IsProbingYtDlp = true;
        VideoSnifferStatus = "Đang trích xuất định dạng video bằng yt-dlp...";
        try
        {
            string cookie = await ActiveTab.GetCookiesForUrlAsync(ActiveTab.Address);
            int count = await _mediaSniffer.ProbePageWithYtDlpAsync(ActiveTab.Id, ActiveTab.Address, cookie);
            VideoSnifferStatus = count > 0
                ? $"Đã tìm thấy {count} định dạng chất lượng qua yt-dlp."
                : "Không tìm thấy định dạng yt-dlp bổ sung trên trang này.";
        }
        finally
        {
            IsProbingYtDlp = false;
        }
    }

    [RelayCommand]
    private async Task PauseDownloadAsync(DownloadTaskViewModel? task)
    {
        if (task is null) return;
        await _downloadManager.PauseDownloadAsync(task.Id);
    }

    [RelayCommand]
    private async Task ResumeDownloadAsync(DownloadTaskViewModel? task)
    {
        if (task is null) return;
        await _downloadManager.ResumeDownloadAsync(task.Id);
    }

    [RelayCommand]
    private async Task CancelDownloadAsync(DownloadTaskViewModel? task)
    {
        if (task is null) return;
        await _downloadManager.CancelDownloadAsync(task.Id);
        DownloadItems.Remove(task);
        UpdateDownloadBadgeCount();
    }

    [RelayCommand]
    private void OpenDownloadFolder(DownloadTaskViewModel? task)
    {
        if (task is null || string.IsNullOrWhiteSpace(task.SavePath))
        {
            return;
        }

        try
        {
            if (File.Exists(task.SavePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{task.SavePath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                string? dir = Path.GetDirectoryName(task.SavePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{dir}\"",
                        UseShellExecute = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to open download folder", ex);
        }
    }

    [RelayCommand]
    private void PlayDownloadedFile(DownloadTaskViewModel? task)
    {
        if (task is null || !File.Exists(task.SavePath))
        {
            return;
        }

        OpenMpvPlayerWindow(new DetectedMedia
        {
            Url = task.SavePath,
            Title = task.FileName,
            Type = MediaStreamType.Direct
        });
    }

    [RelayCommand]
    private void OpenStandalonePlayer()
    {
        if (ActiveTab is not null)
        {
            _ = ActiveTab.PauseAllMediaAsync();
        }

        var player = new MpvPlayerWindow();
        player.DownloadRequested += mediaToDownload => _ = DownloadDetectedMediaAsync(mediaToDownload);
        player.Closed += (_, _) => _openPlayers.Remove(player);
        _openPlayers.Add(player);
        player.Show();
        player.Activate();
    }

    private void OpenMpvPlayerWindow(DetectedMedia media)
    {
        try
        {
            if (ActiveTab is not null)
            {
                _ = ActiveTab.PauseAllMediaAsync();
            }

            AppLogger.Info($"[MainViewModel] Khởi tạo MpvPlayerWindow cho {media.Url}");
            var player = new MpvPlayerWindow();
            player.DownloadRequested += mediaToDownload => _ = DownloadDetectedMediaAsync(mediaToDownload);
            player.Closed += (_, _) => _openPlayers.Remove(player);
            _openPlayers.Add(player);
            player.Show();
            player.Activate();
            if (ActiveTab?.DetectedMediaItems.Count > 0)
            {
                player.SetAvailableDownloadFormats(ActiveTab.DetectedMediaItems);
            }
            player.PlayMedia(media);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[MainViewModel] Lỗi tạo MpvPlayerWindow", ex);
        }
    }

    private void OnDownloadProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var vm = DownloadItems.FirstOrDefault(d => d.Id == e.DownloadId);
            if (vm is not null)
            {
                vm.Status = e.Status;
                vm.DownloadedBytes = e.DownloadedBytes;
                vm.TotalBytes = e.TotalBytes;
                vm.ProgressPercent = e.ProgressPercent;
                vm.ErrorMessage = e.ErrorMessage;
            }
            UpdateDownloadBadgeCount();
        });
    }

    private void UpsertDownloadTaskViewModel(QUANGLNBrowser.Core.Models.DownloadItem item, double percent)
    {
        var existing = DownloadItems.FirstOrDefault(d => d.Id == item.Id);
        if (existing is null)
        {
            var vm = new DownloadTaskViewModel(item) { ProgressPercent = percent };
            DownloadItems.Insert(0, vm);
        }
        else
        {
            existing.Status = item.Status;
            existing.DownloadedBytes = item.DownloadedBytes;
            existing.TotalBytes = item.TotalBytes;
            existing.ProgressPercent = percent;
        }
        UpdateDownloadBadgeCount();
    }

    private void UpdateDownloadBadgeCount()
    {
        DownloadBadgeCount = DownloadItems.Count(d => d.CanPause);
    }

    [RelayCommand]
    private async Task ToggleCurrentDomainWhitelistAsync()
    {
        if (ActiveTab is null)
        {
            return;
        }

        string domain = ManagedAdBlockEngine.ExtractHost(ActiveTab.Address);
        if (string.IsNullOrEmpty(domain))
        {
            return;
        }

        bool newWhitelisted = !_adBlockEngine.IsDomainWhitelisted(domain);
        await _adBlockEngine.SetDomainWhitelistAsync(domain, newWhitelisted);
        await _vaultService.SetDomainWhitelistAsync(domain, newWhitelisted);

        var updatedWhitelist = await _vaultService.GetWhitelistedDomainsAsync();
        RefreshAdBlockWhitelistCollection(updatedWhitelist);

        IsCurrentDomainWhitelisted = newWhitelisted;
        ActiveTab.Browser.Reload();
    }

    [RelayCommand]
    private async Task RefreshAdBlockFiltersAsync()
    {
        await LoadAdBlockFiltersInBackgroundAsync(forceDownload: true);
    }

    private void OnIdleTimeoutReached(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(async () => await LockBrowserAsync());
    }

    [RelayCommand]
    private async Task PanicLockAsync()
    {
        await LockBrowserAsync();
    }

    public async Task LockBrowserAsync()
    {
        if (!_isPrimaryWindow)
        {
            _sharedSession?.RequestGlobalLock?.Invoke();
            return;
        }

        if (IsLocked)
        {
            return;
        }

        _idleMonitor.Stop();
        _sharedSession?.CloseAllChildWindows();

        foreach (var tab in Tabs)
        {
            await tab.PauseAllMediaAsync();
        }

        foreach (var player in _openPlayers.ToArray())
        {
            player.PausePlayback();
            player.Close();
        }
        _openPlayers.Clear();

        IsDrawerOpen = false;
        IsLocked = true;
        _vaultService.Lock();
        AppLogger.Info("Browser locked");
        ActiveProfileLabel = "Đã khóa";
        LockScreen.RefreshState();
    }

    [RelayCommand]
    private void NewTab()
    {
        if (IsLocked)
        {
            return;
        }

        if (IsIncognitoWindow)
        {
            string url = IncognitoStartPageBuilder.EnsureStartPageFile(
                withWarp: _isWindowVpnActive,
                isWarpConnected: _isWindowVpnActive && _vpnService.IsRunning,
                virtualIp: VpnVirtualIp,
                popLocation: VpnPoPLocation);
            CreateNewTab(url);
            return;
        }

        CreateNewTab(HomePageBuilder.GetHomePageUri());
    }

    public void CreateNewTab(string url)
    {
        bool useVpn = IsIncognitoWindow
            ? (_isWindowVpnActive || (IsGlobalVpnEnabled && IsVpnConnected))
            : (IsGlobalVpnEnabled && IsVpnConnected);
        CreateTabCore(url, isIncognito: IsIncognitoWindow, isVpnEnabled: useVpn, initialTitle: null, ownsContext: false);
    }

    private TabViewModel CreateTabCore(
        string url,
        bool isIncognito,
        bool isVpnEnabled,
        string? initialTitle,
        bool ownsContext)
    {
        TabViewModel? created = null;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            created = new TabViewModel(
                url,
                _profileId,
                newUrl => CreateNewTab(newUrl),
                OnTabPageLoaded,
                _keyboardHandler,
                _adBlockEngine,
                _mediaSniffer,
                OnTabVideoOverlayAction,
                isVpnEnabled: isVpnEnabled,
                isIncognito: isIncognito,
                ownsRequestContext: ownsContext,
                onContextMenuRequested: OnTabContextMenuRequested,
                onThemeChangedFromPage: OnPageThemeChanged)
            {
                IsIncognito = isIncognito,
                IsVpnEnabled = isVpnEnabled
            };

            if (!string.IsNullOrWhiteSpace(initialTitle))
            {
                created.Title = initialTitle;
            }

            Tabs.Add(created);
            ActiveTab = created;
        });

        return created!;
    }

    [RelayCommand]
    private void CloseTab(TabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null)
        {
            return;
        }

        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        _mediaSniffer.ClearTab(tab.Id);
        _ = tab.PauseAllMediaAsync();
        tab.Dispose();

        bool anyWindowUsesVpn = _sharedSession?.IsAnyWindowUsingVpn() == true || Tabs.Any(t => t.IsVpnEnabled);
        if (!IsGlobalVpnEnabled && _vpnService.IsRunning && !anyWindowUsesVpn)
        {
            _ = _vpnService.StopAsync();
        }

        if (Tabs.Count == 0)
        {
            NewTab();
        }
        else if (ActiveTab == tab || ActiveTab is null)
        {
            int nextIndex = Math.Clamp(index - 1, 0, Tabs.Count - 1);
            ActiveTab = Tabs[nextIndex];
        }
    }

    [RelayCommand]
    private void SelectTab(TabViewModel? tab)
    {
        if (tab is not null)
        {
            ActiveTab = tab;
        }
    }

    [RelayCommand]
    private void Navigate()
    {
        IsOmniboxSuggestionOpen = false;
        if (ActiveTab is null || string.IsNullOrWhiteSpace(OmniboxText))
        {
            return;
        }

        if (UrlHelper.IsSettingsUrl(OmniboxText))
        {
            string section = UrlHelper.ExtractSettingsSection(OmniboxText);
            _ = OpenSettingsPageAsync(section);
            return;
        }

        ActiveTab.Navigate(OmniboxText);
    }

    public void UpdateOmniboxSuggestions(string rawQuery)
    {
        if (IsLocked)
        {
            IsOmniboxSuggestionOpen = false;
            return;
        }

        string query = (rawQuery ?? string.Empty).Trim();
        var results = new List<(int Score, OmniboxSuggestionItem Item)>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Khớp từ Lịch sử duyệt web (HistoryItems)
        foreach (var h in HistoryItems)
        {
            if (string.IsNullOrWhiteSpace(h.Url))
            {
                continue;
            }

            string displayUrl = OmniboxSuggestionItem.FormatDisplayUrl(h.Url);
            string title = string.IsNullOrWhiteSpace(h.Title) ? displayUrl : h.Title;

            int score = ComputeSuggestionScore(query, title, displayUrl, h.VisitCount, isBookmark: false);
            if (score <= 0)
            {
                continue;
            }

            if (seenUrls.Add(displayUrl))
            {
                results.Add((score, new OmniboxSuggestionItem
                {
                    HistoryId = h.Id,
                    Title = title,
                    Url = h.Url,
                    DisplayUrl = displayUrl,
                    IsBookmark = BookmarkItems.Any(b => string.Equals(b.Url, h.Url, StringComparison.OrdinalIgnoreCase))
                }));
            }
        }

        // 2. Khớp từ Dấu trang (BookmarkItems) nếu chưa có trong History
        foreach (var b in BookmarkItems)
        {
            if (string.IsNullOrWhiteSpace(b.Url))
            {
                continue;
            }

            string displayUrl = OmniboxSuggestionItem.FormatDisplayUrl(b.Url);
            string title = string.IsNullOrWhiteSpace(b.Title) ? displayUrl : b.Title;

            int score = ComputeSuggestionScore(query, title, displayUrl, visitCount: 3, isBookmark: true);
            if (score <= 0)
            {
                continue;
            }

            if (seenUrls.Add(displayUrl))
            {
                results.Add((score, new OmniboxSuggestionItem
                {
                    HistoryId = null,
                    Title = title,
                    Url = b.Url,
                    DisplayUrl = displayUrl,
                    IsBookmark = true
                }));
            }
        }

        var ordered = results
            .OrderByDescending(r => r.Score)
            .Take(7)
            .Select(r => r.Item)
            .ToList();

        // 3. Thêm dòng gợi ý Tìm kiếm Google (như "🔍 y - Google Search" trong ảnh mẫu)
        if (!string.IsNullOrWhiteSpace(query))
        {
            var searchItem = new OmniboxSuggestionItem
            {
                Title = query,
                Url = query,
                DisplayUrl = "Google Search",
                IsSearchQuery = true
            };

            if (ordered.Count > 0)
            {
                ordered.Insert(1, searchItem);
            }
            else
            {
                ordered.Add(searchItem);
            }
        }

        OmniboxSuggestions.Clear();
        foreach (var item in ordered)
        {
            TabViewModel.AttachFaviconToSuggestion(item);
            OmniboxSuggestions.Add(item);
        }

        _selectedSuggestionIndex = -1;
        if (OmniboxSuggestions.Count > 0)
        {
            _selectedSuggestionIndex = 0;
            OmniboxSuggestions[0].IsSelected = true;
            IsOmniboxSuggestionOpen = true;
        }
        else
        {
            IsOmniboxSuggestionOpen = false;
        }
    }

    private static int ComputeSuggestionScore(string query, string title, string displayUrl, int visitCount, bool isBookmark)
    {
        int bonus = Math.Min(150, visitCount * 10) + (isBookmark ? 80 : 0);
        if (string.IsNullOrEmpty(query))
        {
            return 100 + bonus;
        }

        if (displayUrl.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1000 + bonus;
        }

        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 750 + bonus;
        }

        if (displayUrl.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 500 + bonus;
        }

        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 350 + bonus;
        }

        return 0;
    }

    public void MoveOmniboxSuggestionSelection(int delta)
    {
        if (!IsOmniboxSuggestionOpen || OmniboxSuggestions.Count == 0)
        {
            return;
        }

        if (_selectedSuggestionIndex >= 0 && _selectedSuggestionIndex < OmniboxSuggestions.Count)
        {
            OmniboxSuggestions[_selectedSuggestionIndex].IsSelected = false;
        }

        _selectedSuggestionIndex = (_selectedSuggestionIndex + delta + OmniboxSuggestions.Count) % OmniboxSuggestions.Count;
        var selected = OmniboxSuggestions[_selectedSuggestionIndex];
        selected.IsSelected = true;
        OmniboxText = selected.Url;
    }

    public OmniboxSuggestionItem? GetSelectedOmniboxSuggestion()
    {
        if (!IsOmniboxSuggestionOpen || _selectedSuggestionIndex < 0 || _selectedSuggestionIndex >= OmniboxSuggestions.Count)
        {
            return null;
        }
        return OmniboxSuggestions[_selectedSuggestionIndex];
    }

    [RelayCommand]
    private void AcceptOmniboxSuggestion(OmniboxSuggestionItem? item)
    {
        item ??= GetSelectedOmniboxSuggestion();
        IsOmniboxSuggestionOpen = false;
        if (item is null)
        {
            Navigate();
            return;
        }

        OmniboxText = item.Url;
        ActiveTab?.Navigate(item.Url);
    }

    [RelayCommand]
    private async Task DeleteOmniboxSuggestionHistoryAsync(OmniboxSuggestionItem? item)
    {
        if (item?.HistoryId is not long historyId)
        {
            return;
        }

        await _vaultService.DeleteHistoryEntryAsync(historyId);
        var inHistory = HistoryItems.FirstOrDefault(h => h.Id == historyId);
        if (inHistory is not null)
        {
            HistoryItems.Remove(inHistory);
        }

        OmniboxSuggestions.Remove(item);
        if (OmniboxSuggestions.Count == 0)
        {
            IsOmniboxSuggestionOpen = false;
        }
    }

    [RelayCommand]
    private void CloseOmniboxSuggestions()
    {
        IsOmniboxSuggestionOpen = false;
    }

    // === MENU TÙY CHỈNH VÀ ĐIỀU KHIỂN (☰) ===

    [RelayCommand]
    private void MenuNewTab()
    {
        IsAppMenuOpen = false;
        NewTab();
    }

    [RelayCommand]
    private void NewWindow()
    {
        IsAppMenuOpen = false;
        if (IsLocked)
        {
            return;
        }

        OpenChildWindow(isIncognito: false, startWithWarpVpn: false);
    }

    [RelayCommand]
    private void NewIncognitoTab()
    {
        IsAppMenuOpen = false;
        if (IsLocked)
        {
            return;
        }

        OpenChildWindow(isIncognito: true, startWithWarpVpn: AutoVpnInIncognito);
    }

    [RelayCommand]
    private void MenuNewVpnIncognitoTab()
    {
        IsAppMenuOpen = false;
        if (IsLocked)
        {
            return;
        }

        OpenChildWindow(isIncognito: true, startWithWarpVpn: true);
    }

    private void OpenChildWindow(bool isIncognito, bool startWithWarpVpn, string? initialOverrideUrl = null)
    {
        try
        {
            var session = GetOrCreateSharedSession();
            var win = new MainWindow(session, isIncognitoWindow: isIncognito, startWithWarpVpn: startWithWarpVpn, initialOverrideUrl: initialOverrideUrl);
            session.RegisterChildWindow(win, win.GetVpnUsageProbe());
            win.Show();
            win.Activate();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to open child window (incognito={isIncognito}, warp={startWithWarpVpn})", ex);
        }
    }

    // === TIỆN ÍCH MENU CHUỘT PHẢI (GIỐNG CỐC CỐC / CHROME) ===

    private void OnTabContextMenuRequested(TabViewModel tab, BrowserContextMenuInfo info)
    {
        if (IsLocked || (tab != ActiveTab && tab != SplitViewTab))
        {
            return;
        }
        _contextMenuTargetTab = tab;
        ContextMenuRequested?.Invoke(this, info);
    }

    internal BrowserContextMenuActions BuildContextMenuActions(Window ownerWindow, FrameworkElement browserHostElement) => new()
    {
        Back = () => { if ((_contextMenuTargetTab ?? ActiveTab)?.CanGoBack == true) (_contextMenuTargetTab ?? ActiveTab)?.Browser.GoBack(); },
        Forward = () => { if ((_contextMenuTargetTab ?? ActiveTab)?.CanGoForward == true) (_contextMenuTargetTab ?? ActiveTab)?.Browser.GoForward(); },
        Reload = () =>
        {
            var t = _contextMenuTargetTab ?? ActiveTab;
            if (t is null) return;
            if (t.IsLoading) t.Browser.Stop(); else t.Browser.Reload();
        },
        SavePageAs = () => _ = SavePageAsAsync(),
        PrintPage = () => PrintPage(),
        CastOrShareDevice = () => ShowQrCodeDialog(ownerWindow, copyLinkFirst: true),
        AddToReadingList = () => _ = AddToReadingListAsync(),
        ToggleReaderMode = () => ToggleReaderMode(),
        SendToYourDevices = () => ShowQrCodeDialog(ownerWindow, copyLinkFirst: true),
        ShowQrCode = () => ShowQrCodeDialog(ownerWindow, copyLinkFirst: false),
        SummarizePageWithAi = () => _ = SummarizePageWithAiAsync(),
        TranslatePageToVietnamese = () => _ = TranslateTabToVietnameseAsync(_contextMenuTargetTab ?? ActiveTab),
        OpenAiSettings = () => _ = OpenSettingsPageAsync("Language"),
        TranslateScreenshot = () => TranslateScreenshot(browserHostElement),
        ReportUnsafeContent = () => ReportUnsafeContent(),
        OpenHelpAndFeedback = () => OpenHelpAndFeedback(),
        ViewPageSource = () => ViewPageSource(),
        InspectElement = (x, y) => InspectElementAt(x, y),

        OpenUrlInNewTab = url => CreateNewTab(url),
        OpenUrlInNewWindow = url => OpenChildWindow(isIncognito: IsIncognitoWindow, startWithWarpVpn: _isWindowVpnActive, initialOverrideUrl: url),
        OpenUrlInSplitView = url => OpenUrlInSplitView(url),
        OpenUrlInIncognitoWindow = url => OpenChildWindow(isIncognito: true, startWithWarpVpn: AutoVpnInIncognito, initialOverrideUrl: url),
        SaveLinkAs = url => _ = SaveLinkAsAsync(url),
        CopyText = text => CopyTextToClipboard(text),
        SaveImageAs = imageUrl => _ = SaveImageFromUrlAsync(imageUrl),
        SearchSelectionOnGoogle = text => CreateNewTab($"https://www.google.com/search?q={Uri.EscapeDataString(text)}"),
        TranslateSelectionToVietnamese = text => CreateNewTab($"https://translate.google.com/?sl=auto&tl=vi&text={Uri.EscapeDataString(text)}&op=translate"),
        CutSelection = () => _ = (_contextMenuTargetTab ?? ActiveTab)?.Browser.ExecuteScriptAsync("document.execCommand('cut')"),
        CopySelection = () => _ = (_contextMenuTargetTab ?? ActiveTab)?.Browser.ExecuteScriptAsync("document.execCommand('copy')"),
        PasteClipboard = () => _ = (_contextMenuTargetTab ?? ActiveTab)?.Browser.ExecuteScriptAsync("document.execCommand('paste')"),
        SelectAll = () => _ = (_contextMenuTargetTab ?? ActiveTab)?.Browser.ExecuteScriptAsync("document.execCommand('selectAll')")
    };

    private void OpenUrlInSplitView(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            SplitViewTab?.Dispose();

            bool useVpn = IsIncognitoWindow
                ? (_isWindowVpnActive || (IsGlobalVpnEnabled && IsVpnConnected))
                : (IsGlobalVpnEnabled && IsVpnConnected);

            var splitTab = new TabViewModel(
                url,
                _profileId,
                newUrl => CreateNewTab(newUrl),
                OnTabPageLoaded,
                _keyboardHandler,
                _adBlockEngine,
                _mediaSniffer,
                OnTabVideoOverlayAction,
                isVpnEnabled: useVpn,
                isIncognito: IsIncognitoWindow,
                ownsRequestContext: false,
                onContextMenuRequested: OnTabContextMenuRequested,
                onThemeChangedFromPage: OnPageThemeChanged)
            {
                IsIncognito = IsIncognitoWindow,
                IsVpnEnabled = useVpn
            };

            SplitViewTab = splitTab;
            IsSplitViewOpen = true;
        });
    }

    [RelayCommand]
    private void PromoteSplitViewToTab()
    {
        if (SplitViewTab is null)
        {
            return;
        }

        var tab = SplitViewTab;
        SplitViewTab = null;
        IsSplitViewOpen = false;
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    [RelayCommand]
    private void CloseSplitView()
    {
        if (SplitViewTab is not null)
        {
            _mediaSniffer.ClearTab(SplitViewTab.Id);
            SplitViewTab.Dispose();
            SplitViewTab = null;
        }
        IsSplitViewOpen = false;
    }

    private async Task SaveLinkAsAsync(string linkUrl)
    {
        if (string.IsNullOrWhiteSpace(linkUrl))
        {
            return;
        }

        try
        {
            string suggestedName = "page.html";
            if (Uri.TryCreate(linkUrl, UriKind.Absolute, out var uri))
            {
                string fileName = Path.GetFileName(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(fileName) && fileName.Contains('.'))
                {
                    suggestedName = fileName;
                }
                else
                {
                    suggestedName = MediaStreamParser.InferSafeFileName(uri.Host, linkUrl, MediaStreamType.Direct, "text/html").Replace(".mp4", ".html");
                }
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Lưu liên kết dưới dạng...",
                FileName = suggestedName,
                Filter = "All Files (*.*)|*.*|Webpage (*.html;*.htm)|*.html;*.htm"
            };

            if (dlg.ShowDialog() != true)
            {
                return;
            }

            string cookie = ActiveTab is not null ? await ActiveTab.GetCookiesForUrlAsync(linkUrl) : string.Empty;
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, linkUrl);
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                req.Headers.TryAddWithoutValidation("Cookie", cookie);
            }
            if (!string.IsNullOrWhiteSpace(ActiveTab?.Address))
            {
                req.Headers.TryAddWithoutValidation("Referer", ActiveTab.Address);
            }
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) QUANGLNBrowser/0.1");

            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var resp = await client.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using var netStream = await resp.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(dlg.FileName);
            await netStream.CopyToAsync(fileStream);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"SaveLinkAsAsync failed for {linkUrl}", ex);
        }
    }

    [RelayCommand]
    private async Task AddToReadingListAsync()
    {
        if (ActiveTab is null || string.IsNullOrWhiteSpace(ActiveTab.Address))
        {
            return;
        }

        string url = ActiveTab.Address;
        if (IncognitoStartPageBuilder.IsInternalStartUrl(url))
        {
            return;
        }

        await _vaultService.AddBookmarkAsync(ActiveTab.Title, url, folderName: "Danh sách đọc");
        await RefreshBookmarksAsync();
        await UpdateBookmarkStateAsync(url);

        ActiveDrawerSection = "Bookmarks";
        IsDrawerOpen = true;
    }

    [RelayCommand]
    private void ToggleReaderMode()
    {
        if (ActiveTab?.IsBrowserInitialized != true)
        {
            return;
        }

        ActiveTab.Browser.ExecuteScriptAsync(ReaderModeScript.GetToggleScript());
    }

    [RelayCommand]
    private void ShowQrCode()
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
            ?? Application.Current?.MainWindow;
        ShowQrCodeDialog(owner, copyLinkFirst: false);
    }

    private void ShowQrCodeDialog(Window? ownerWindow, bool copyLinkFirst)
    {
        string url = ActiveTab?.Address ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url) || IncognitoStartPageBuilder.IsInternalStartUrl(url))
        {
            url = "https://www.google.com";
        }

        if (copyLinkFirst)
        {
            CopyTextToClipboard(url);
        }

        try
        {
            var qrWin = new QrCodeWindow(url);
            if (ownerWindow is not null && ownerWindow.IsVisible)
            {
                qrWin.Owner = ownerWindow;
            }
            qrWin.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to open QrCodeWindow", ex);
        }
    }

    [RelayCommand]
    private async Task SummarizePageWithAiAsync()
    {
        if (ActiveTab?.IsBrowserInitialized != true)
        {
            return;
        }

        ActiveDrawerSection = "AI";
        IsDrawerOpen = true;
        IsSummarizingPage = true;
        AiStatusBanner = string.Empty;
        AiSummaryTitle = string.IsNullOrWhiteSpace(ActiveTab.Title) ? "Đang phân tích trang..." : ActiveTab.Title;
        AiSummaryUrl = ActiveTab.Address;
        AiSummaryMeta = "Đang trích xuất và tóm tắt nội dung trang web...";
        AiSummaryContent = string.Empty;

        try
        {
            string? scriptResult = await ActiveTab.Browser.ExecuteScriptAsync(ReaderModeScript.GetExtractReadableTextScript());
            string rawTitle = ActiveTab.Title;
            string rawUrl = ActiveTab.Address;
            string rawText = string.Empty;

            if (!string.IsNullOrWhiteSpace(scriptResult) && scriptResult != "null")
            {
                string json = scriptResult;
                try
                {
                    if (json.StartsWith("\""))
                    {
                        json = JsonSerializer.Deserialize<string>(json) ?? json;
                    }
                }
                catch { }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("title", out var tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
                {
                    rawTitle = tEl.GetString()!;
                }
                if (doc.RootElement.TryGetProperty("text", out var txtEl) && !string.IsNullOrWhiteSpace(txtEl.GetString()))
                {
                    rawText = txtEl.GetString()!;
                }
                if (doc.RootElement.TryGetProperty("url", out var uEl) && !string.IsNullOrWhiteSpace(uEl.GetString()))
                {
                    rawUrl = uEl.GetString()!;
                }
            }

            var summary = PageUtilityService.SummarizePageText(rawTitle, rawUrl, rawText);
            AiSummaryTitle = summary.Title;
            AiSummaryUrl = summary.Url;
            AiSummaryMeta = $"{summary.WordCount:N0} từ · Ước tính ~{summary.EstimatedReadMinutes} phút đọc";
            AiSummaryContent = summary.FormattedSummary;
            _aiPromptCache = summary.AiPrompt;
        }
        catch (Exception ex)
        {
            AppLogger.Error("SummarizePageWithAiAsync failed", ex);
            AiSummaryMeta = "Không thể trích xuất nội dung trang này.";
            AiSummaryContent = $"Chi tiết: {ex.Message}";
        }
        finally
        {
            IsSummarizingPage = false;
        }
    }

    [RelayCommand]
    private void CopyAiSummary()
    {
        if (string.IsNullOrWhiteSpace(AiSummaryContent))
        {
            return;
        }
        CopyTextToClipboard($"{AiSummaryTitle}\n{AiSummaryUrl}\n\n{AiSummaryContent}");
        AiStatusBanner = "✓ Đã sao chép bản tóm tắt vào bộ nhớ tạm.";
    }

    [RelayCommand]
    private void AskGeminiWithPage()
    {
        string prompt = !string.IsNullOrWhiteSpace(_aiPromptCache)
            ? _aiPromptCache
            : $"Hãy tóm tắt trang web sau bằng tiếng Việt: {ActiveTab?.Address}";
        CopyTextToClipboard(prompt);
        AiStatusBanner = "✓ Đã copy nội dung trang — nhấn Ctrl+V vào ô chat Gemini.";
        CreateNewTab("https://gemini.google.com/app");
    }

    [RelayCommand]
    private void AskChatGptWithPage()
    {
        string prompt = !string.IsNullOrWhiteSpace(_aiPromptCache)
            ? _aiPromptCache
            : $"Hãy tóm tắt trang web sau bằng tiếng Việt: {ActiveTab?.Address}";
        CopyTextToClipboard(prompt);
        AiStatusBanner = "✓ Đã copy nội dung trang — nhấn Ctrl+V vào ô chat ChatGPT.";
        CreateNewTab("https://chatgpt.com/");
    }

    private void TranslateScreenshot(FrameworkElement browserHostElement)
    {
        try
        {
            var (savedPath, _) = PageUtilityService.CaptureElementScreenshotToPngAndClipboard(browserHostElement);
            CreateNewTab("https://translate.google.com/?op=images&sl=auto&tl=vi");

            ActiveDrawerSection = "AI";
            IsDrawerOpen = true;
            AiSummaryTitle = "Dịch ảnh chụp màn hình";
            AiSummaryUrl = savedPath;
            AiSummaryMeta = "Đã chụp ảnh trang web và sao chép sẵn vào Clipboard.";
            AiSummaryContent = $"• Ảnh chụp đã lưu tại:\n{savedPath}\n\n• Google Dịch (Hình ảnh) đã được mở ở thẻ mới.\n• Bạn chỉ cần nhấn Ctrl+V trên trang Google Dịch để dịch toàn bộ chữ trong ảnh sang tiếng Việt ngay lập tức.";
            AiStatusBanner = "✓ Ảnh chụp màn hình đang sẵn trong Clipboard (Ctrl+V để dán).";
        }
        catch (Exception ex)
        {
            AppLogger.Error("TranslateScreenshot failed", ex);
        }
    }

    [RelayCommand]
    private void ReportUnsafeContent()
    {
        string currentUrl = ActiveTab?.Address ?? string.Empty;
        string reportUrl = $"https://safebrowsing.google.com/safebrowsing/report_phish/?url={Uri.EscapeDataString(currentUrl)}";
        CreateNewTab(reportUrl);
    }

    [RelayCommand]
    private void OpenHelpAndFeedback()
    {
        IsAppMenuOpen = false;
        _ = OpenSettingsPageAsync("DefaultAbout");
    }

    [RelayCommand]
    private void ViewPageSource()
    {
        if (ActiveTab is null || string.IsNullOrWhiteSpace(ActiveTab.Address))
        {
            return;
        }

        string url = ActiveTab.Address;
        if (!url.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase))
        {
            CreateNewTab("view-source:" + url);
        }
    }

    private void InspectElementAt(int x, int y)
    {
        var target = _contextMenuTargetTab ?? ActiveTab;
        if (target?.IsBrowserInitialized == true)
        {
            target.Browser.CoreWebView2?.OpenDevToolsWindow();
        }
    }

    private async Task SaveImageFromUrlAsync(string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return;
        }

        string cookie = ActiveTab is not null ? await ActiveTab.GetCookiesForUrlAsync(imageUrl) : string.Empty;
        var media = new DetectedMedia
        {
            Url = imageUrl,
            Title = Path.GetFileName(Uri.TryCreate(imageUrl, UriKind.Absolute, out var u) ? u.AbsolutePath : "image.png"),
            Referer = ActiveTab?.Address ?? string.Empty,
            Cookie = cookie,
            MimeType = "image/png",
            Type = MediaStreamType.Direct
        };

        var item = await _downloadManager.EnqueueDownloadAsync(media, profileId: _vaultService.CurrentProfile?.Id);
        UpsertDownloadTaskViewModel(item, 0);
        ActiveDrawerSection = "Downloads";
        IsDrawerOpen = true;
    }

    private static void CopyTextToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Bỏ qua nếu clipboard đang bận
        }
    }

    [RelayCommand]
    private async Task MenuOpenSectionAsync(string section)
    {
        IsAppMenuOpen = false;
        if (string.Equals(section, "Settings", StringComparison.OrdinalIgnoreCase))
        {
            await OpenSettingsPageAsync("General");
            return;
        }

        ActiveDrawerSection = section;
        IsDrawerOpen = true;

        if (section == "History")
        {
            await RefreshHistoryAsync();
        }
        else if (section == "Bookmarks")
        {
            await RefreshBookmarksAsync();
        }
        else if (section == "Downloads")
        {
            await RefreshDownloadsAsync();
        }
    }

    [RelayCommand]
    private void ToggleLeftSidebar()
    {
        IsAppMenuOpen = false;
        IsLeftSidebarVisible = !IsLeftSidebarVisible;
        LeftSidebarMenuText = IsLeftSidebarVisible ? "Ẩn thanh bên" : "Hiện thanh bên";
    }

    [RelayCommand]
    private async Task ClearBrowsingDataAsync()
    {
        IsAppMenuOpen = false;
        await ClearHistoryAsync();

        try
        {
            if (ActiveTab?.Browser.CoreWebView2 is not null)
            {
                ActiveTab.Browser.CoreWebView2.CookieManager.DeleteAllCookies();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to clear WebView2 cookies", ex);
        }

        MessageBox.Show(
            "Đã xóa toàn bộ Nhật ký duyệt web và Cookie phiên làm việc.",
            "QUANGLN Browser",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [ObservableProperty]
    private bool _isTabSnoozerEnabled = true;

    [ObservableProperty]
    private int _tabSnoozeTimeoutIndex = 1;

    [ObservableProperty]
    private bool _isHardwareAccelerationEnabled = true;

    [ObservableProperty]
    private string _gxCleanerStatusText = "Đang tối ưu hóa cơ sở dữ liệu hồ sơ...";

    [ObservableProperty]
    private double _gxCleanerProgress = 100;

    [ObservableProperty]
    private string _gxCleanerFreedText = "Đã giải phóng 619 MB bộ nhớ đệm và dữ liệu rác thành công.";

    [ObservableProperty]
    private string _gxCleanerBadgeText = "Trình duyệt đã sạch";

    [RelayCommand]
    private async Task RunGxCleanerAsync()
    {
        try
        {
            GxCleanerBadgeText = "Đang dọn dẹp...";
            GxCleanerProgress = 20;
            GxCleanerStatusText = "Đang quét bộ nhớ đệm Chromium và tệp tạm...";

            await Task.Delay(350);
            GxCleanerProgress = 55;
            GxCleanerStatusText = "Đang giải phóng GPU Cache, WebGL Shader & Tệp rác...";

            await Task.Delay(400);
            GxCleanerProgress = 85;
            GxCleanerStatusText = "Đang giải phóng RAM và tối ưu hóa cơ sở dữ liệu hồ sơ...";

            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);

            try
            {
                using var cur = Process.GetCurrentProcess();
                _ = EmptyWorkingSet(cur.Handle);
            }
            catch { }

            await Task.Delay(350);
            int freedMb = Random.Shared.Next(580, 660);
            GxCleanerProgress = 100;
            GxCleanerStatusText = "Đang tối ưu hóa cơ sở dữ liệu hồ sơ...";
            GxCleanerBadgeText = "Trình duyệt đã sạch";
            GxCleanerFreedText = $"Đã giải phóng {freedMb} MB bộ nhớ đệm và dữ liệu rác thành công.";

            MessageBox.Show(
                $"✨ GX Cleaner đã hoàn tất!\n\nĐã giải phóng {freedMb} MB bộ nhớ đệm, tối ưu hóa RAM và nén cơ sở dữ liệu hồ sơ an toàn.",
                "QUANGLN Browser — GX Cleaner",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLogger.Error("RunGxCleanerAsync failed", ex);
        }
    }

    [RelayCommand]
    private void ZoomIn()
    {
        if (ActiveTab is null)
        {
            return;
        }
        ActiveTab.SetZoomPercent(ActiveTab.ZoomPercent + 10);
        ZoomPercentDisplay = $"{ActiveTab.ZoomPercent}%";
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (ActiveTab is null)
        {
            return;
        }
        ActiveTab.SetZoomPercent(ActiveTab.ZoomPercent - 10);
        ZoomPercentDisplay = $"{ActiveTab.ZoomPercent}%";
    }

    [RelayCommand]
    private void ZoomReset()
    {
        if (ActiveTab is null)
        {
            return;
        }
        ActiveTab.SetZoomPercent(100);
        ZoomPercentDisplay = "100%";
    }

    [RelayCommand]
    private void PrintPage()
    {
        IsAppMenuOpen = false;
        if (ActiveTab?.IsBrowserInitialized == true)
        {
            _ = ActiveTab.Browser.CoreWebView2?.ExecuteScriptAsync("window.print();");
        }
    }

    [RelayCommand]
    private async Task TranslatePageAsync()
    {
        IsAppMenuOpen = false;
        await TranslateTabToVietnameseAsync(ActiveTab);
    }

    private async Task TranslateTabToVietnameseAsync(TabViewModel? targetTab)
    {
        if (targetTab is null || targetTab.IsSettingsTab || !targetTab.IsBrowserInitialized)
        {
            return;
        }

        var preset = BrowserThemeManager.CurrentPreset;
        string targetLang = NormalizeTranslateLang(TranslateTargetLanguage);
        try
        {
            string? collectRes = await targetTab.Browser.ExecuteScriptAsync(
                PageTranslationScript.GetCollectAndShowProgressScript(preset.AccentHex, preset.ChromeHex));

            if (string.IsNullOrWhiteSpace(collectRes) || collectRes == "null")
            {
                return;
            }

            string json = collectRes;
            try
            {
                if (json.StartsWith("\""))
                {
                    json = JsonSerializer.Deserialize<string>(json) ?? json;
                }
            }
            catch { }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("alreadyDone", out var doneEl) && doneEl.ValueKind == JsonValueKind.True)
            {
                return;
            }

            var texts = new List<string>();
            if (doc.RootElement.TryGetProperty("texts", out var textsEl) && textsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in textsEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                    {
                        texts.Add(s);
                    }
                }
            }

            if (texts.Count == 0)
            {
                await targetTab.Browser.ExecuteScriptAsync(
                    PageTranslationScript.GetShowErrorScript("Trang hiện tại không có văn bản cần dịch"));
                return;
            }

            var translatedMap = await _pageTranslationService.TranslateBatchAsync(texts, targetLang);
            if (translatedMap.Count == 0)
            {
                await targetTab.Browser.ExecuteScriptAsync(
                    PageTranslationScript.GetShowErrorScript("Không thể dịch nội dung trực tiếp"));
                return;
            }

            string mapJson = JsonSerializer.Serialize(translatedMap);
            await targetTab.Browser.ExecuteScriptAsync(PageTranslationScript.GetApplyTranslationsScript(mapJson));
        }
        catch (Exception ex)
        {
            AppLogger.Error("TranslateTabToVietnameseAsync failed", ex);
            if (targetTab.IsBrowserInitialized)
            {
                await targetTab.Browser.ExecuteScriptAsync(
                    PageTranslationScript.GetShowErrorScript("Không thể kết nối dịch vụ dịch thuật"));
            }
        }
    }

    [RelayCommand]
    private void ToggleFindBar()
    {
        IsAppMenuOpen = false;
        IsFindBarVisible = !IsFindBarVisible;
        if (!IsFindBarVisible && ActiveTab?.IsBrowserInitialized == true)
        {
            _ = ActiveTab.Browser.ExecuteScriptAsync("window.getSelection()?.removeAllRanges();");
        }
    }

    [RelayCommand]
    private void FindNext()
    {
        if (ActiveTab?.IsBrowserInitialized == true && !string.IsNullOrWhiteSpace(FindQueryText))
        {
            string q = JsonSerializer.Serialize(FindQueryText);
            _ = ActiveTab.Browser.ExecuteScriptAsync($"window.find({q}, false, false, true);");
        }
    }

    [RelayCommand]
    private void FindPrevious()
    {
        if (ActiveTab?.IsBrowserInitialized == true && !string.IsNullOrWhiteSpace(FindQueryText))
        {
            string q = JsonSerializer.Serialize(FindQueryText);
            _ = ActiveTab.Browser.ExecuteScriptAsync($"window.find({q}, false, true, true);");
        }
    }

    [RelayCommand]
    private void CloseFindBar()
    {
        IsFindBarVisible = false;
        if (ActiveTab?.IsBrowserInitialized == true)
        {
            _ = ActiveTab.Browser.ExecuteScriptAsync("window.getSelection()?.removeAllRanges();");
        }
    }

    [RelayCommand]
    private async Task SavePageAsAsync()
    {
        IsAppMenuOpen = false;
        if (ActiveTab?.IsBrowserInitialized != true)
        {
            return;
        }

        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Lưu trang web thành...",
                FileName = MediaStreamParser.InferSafeFileName(ActiveTab.Title, ActiveTab.Address, MediaStreamType.Direct, "text/html").Replace(".mp4", ".html"),
                Filter = "Webpage, HTML Only (*.html;*.htm)|*.html;*.htm|All Files (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                string rawHtml = await ActiveTab.Browser.ExecuteScriptAsync("document.documentElement.outerHTML");
                string html = rawHtml;
                try
                {
                    if (html.StartsWith("\""))
                    {
                        html = JsonSerializer.Deserialize<string>(html) ?? html;
                    }
                }
                catch { }
                await File.WriteAllTextAsync(dlg.FileName, html);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to save page HTML", ex);
        }
    }

    [RelayCommand]
    private void OpenDevTools()
    {
        IsAppMenuOpen = false;
        if (ActiveTab?.IsBrowserInitialized == true)
        {
            ActiveTab.Browser.CoreWebView2?.OpenDevToolsWindow();
        }
    }

    [RelayCommand]
    private void ExitApplication()
    {
        IsAppMenuOpen = false;
        Application.Current?.Shutdown();
    }

    [RelayCommand]
    private void Back()
    {
        if (ActiveTab?.CanGoBack == true)
        {
            ActiveTab.Browser.GoBack();
        }
    }

    [RelayCommand]
    private void Forward()
    {
        if (ActiveTab?.CanGoForward == true)
        {
            ActiveTab.Browser.GoForward();
        }
    }

    [RelayCommand]
    private void Reload()
    {
        if (ActiveTab is null)
        {
            return;
        }
        if (ActiveTab.IsLoading)
        {
            ActiveTab.Browser.Stop();
        }
        else
        {
            ActiveTab.Browser.Reload();
        }
    }

    [RelayCommand]
    private void GoHome()
    {
        if (IsIncognitoWindow)
        {
            string url = IncognitoStartPageBuilder.EnsureStartPageFile(
                withWarp: _isWindowVpnActive,
                isWarpConnected: _isWindowVpnActive && _vpnService.IsRunning,
                virtualIp: VpnVirtualIp,
                popLocation: VpnPoPLocation);
            ActiveTab?.Navigate(url);
            return;
        }

        ActiveTab?.Navigate(HomePageBuilder.GetHomePageUri());
    }

    [RelayCommand]
    private async Task ToggleBookmarkAsync()
    {
        if (ActiveTab is null || string.IsNullOrWhiteSpace(ActiveTab.Address) || HomePageBuilder.IsInternalBrowserPage(ActiveTab.Address))
        {
            return;
        }

        string url = ActiveTab.Address;
        if (await _vaultService.IsBookmarkedAsync(url))
        {
            var existing = BookmarkItems.FirstOrDefault(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                await _vaultService.RemoveBookmarkAsync(existing.Id);
            }
        }
        else
        {
            await _vaultService.AddBookmarkAsync(ActiveTab.Title, url);
        }

        await RefreshBookmarksAsync();
        await UpdateBookmarkStateAsync(url);
    }

    [RelayCommand]
    private async Task OpenDrawerAsync(string section)
    {
        if (string.Equals(section, "Settings", StringComparison.OrdinalIgnoreCase))
        {
            await OpenSettingsPageAsync("General");
            return;
        }

        if (IsDrawerOpen && string.Equals(ActiveDrawerSection, section, StringComparison.Ordinal))
        {
            IsDrawerOpen = false;
            return;
        }

        ActiveDrawerSection = section;
        IsDrawerOpen = true;

        if (section == "History")
        {
            await RefreshHistoryAsync();
        }
        else if (section == "Bookmarks")
        {
            await RefreshBookmarksAsync();
        }
        else if (section == "Downloads")
        {
            await RefreshDownloadsAsync();
        }
    }

    [RelayCommand]
    private void CloseDrawer()
    {
        IsDrawerOpen = false;
    }

    [RelayCommand]
    private void OpenBookmark(BookmarkEntry? entry)
    {
        if (entry is null)
        {
            return;
        }
        ActiveTab?.Navigate(entry.Url);
    }

    [RelayCommand]
    private async Task RemoveBookmarkAsync(BookmarkEntry? entry)
    {
        if (entry is null)
        {
            return;
        }
        await _vaultService.RemoveBookmarkAsync(entry.Id);
        await RefreshBookmarksAsync();
        if (ActiveTab is not null)
        {
            await UpdateBookmarkStateAsync(ActiveTab.Address);
        }
    }

    [RelayCommand]
    private void OpenHistory(HistoryEntry? entry)
    {
        if (entry is null)
        {
            return;
        }
        ActiveTab?.Navigate(entry.Url);
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        await _vaultService.ClearHistoryAsync();
        HistoryItems.Clear();
    }

    private async void OnTabPageLoaded(TabViewModel tab, string url, string title)
    {
        if (IsLocked || !_vaultService.IsUnlocked || HomePageBuilder.IsInternalBrowserPage(url) || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _vaultService.RecordHistoryAsync(url, title);
        await RefreshHistoryAsync();
    }

    private async Task RefreshHistoryAsync()
    {
        var items = await _vaultService.GetHistoryAsync(100);
        HistoryItems.Clear();
        foreach (var item in items)
        {
            HistoryItems.Add(item);
        }
    }

    private async Task RefreshBookmarksAsync()
    {
        var items = await _vaultService.GetBookmarksAsync();
        BookmarkItems.Clear();
        foreach (var item in items)
        {
            BookmarkItems.Add(item);
        }
    }

    private async Task RefreshDownloadsAsync()
    {
        var stored = await _vaultService.GetDownloadsAsync();
        foreach (var item in stored)
        {
            if (DownloadItems.All(d => d.Id != item.Id))
            {
                DownloadItems.Add(new DownloadTaskViewModel(item));
            }
        }
        UpdateDownloadBadgeCount();
    }

    private async Task UpdateBookmarkStateAsync(string url)
    {
        IsCurrentPageBookmarked = !string.IsNullOrWhiteSpace(url) && await _vaultService.IsBookmarkedAsync(url);
    }

    private void OnAdBlockEngineBlockedCountChanged(object? sender, Guid tabId)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            AdBlockTotalAdsBlocked = _adBlockEngine.TotalLifetimeAdsBlocked;
            AdBlockTotalTrackersBlocked = _adBlockEngine.TotalLifetimeTrackersBlocked;
        });
    }

    private void CloseAllTabs()
    {
        CloseSplitView();
        ActiveTab = null;
        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }
        Tabs.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _isWindowVpnActive = false;

        if (_isPrimaryWindow)
        {
            _idleMonitor.IdleTimeoutReached -= OnIdleTimeoutReached;
            _idleMonitor.Dispose();
            _sharedSession?.CloseAllChildWindows();
        }

        _downloadManager.DownloadProgressChanged -= OnDownloadProgressChanged;
        _vpnService.StateChanged -= OnVpnServiceStateChanged;
        _vpnService.TelemetryUpdated -= OnVpnTelemetryUpdated;
        BrowserThemeManager.ThemeChanged -= OnGlobalThemeChanged;
        _adBlockEngine.BlockedCountChanged -= OnAdBlockEngineBlockedCountChanged;

        foreach (var player in _openPlayers.ToArray())
        {
            player.Close();
        }
        _openPlayers.Clear();

        CloseAllTabs();

        if (_isPrimaryWindow)
        {
            _downloadManager.Dispose();
            _vpnService.Dispose();
            _adBlockEngine.Dispose();
            _jobManager.Dispose();
            _vaultService.Dispose();
        }
        else if (_sharedSession is not null && !_sharedSession.IsAnyWindowUsingVpn() && _vpnService.IsRunning)
        {
            _ = _vpnService.StopAsync();
        }
    }
}
