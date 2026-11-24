namespace QUANGLNBrowser.Core.Models;

/// <summary>
/// Tùy chọn VPN nâng cao kiểu Opera VPN, được lưu mã hóa trong vault.db theo từng hồ sơ.
/// </summary>
public sealed class VpnPreferences
{
    public VpnRegion SelectedRegion { get; set; } = VpnRegion.Optimal;
    public bool BypassSearchEngines { get; set; } = true;
    public bool AutoVpnInIncognito { get; set; } = true;
    public bool EnableKillSwitch { get; set; } = true;
    public bool EnableSplitTunneling { get; set; } = true;
    public bool EnableDoubleVpn { get; set; } = false;
    public string ThemeKey { get; set; } = "coccoc";
    public string? LastSelectedNodeId { get; set; }
    public List<string> CustomBypassDomains { get; set; } = new();

    // === Cài đặt Trình duyệt (Kiểu Cốc Cốc Settings — lưu mã hóa trong vault.db) ===
    public string DefaultSearchEngine { get; set; } = "google";
    public string StartupMode { get; set; } = "homepage";
    public string StartupCustomUrl { get; set; } = "https://www.google.com";
    public bool ShowHomeButton { get; set; } = true;
    public bool ShowLeftSidebar { get; set; } = true;
    public int DefaultZoomPercent { get; set; } = 100;
    public bool EnableAdBlock { get; set; } = true;
    public string AdBlockMode { get; set; } = "standard"; // "standard" (Tiêu chuẩn) | "strict" (Nâng cao)
    public bool AdBlockBlockPopups { get; set; } = true;
    public bool AdBlockBlockTrackers { get; set; } = true;
    public bool AdBlockBlockYouTubeAds { get; set; } = true;
    public bool AdBlockAllowAcceptableAds { get; set; } = true;
    public bool AdBlockEnableEasyList { get; set; } = true;
    public bool AdBlockEnableEasyPrivacy { get; set; } = true;
    public bool AdBlockEnableAbpvn { get; set; } = true;
    public bool AdBlockEnableAntiAnnoyance { get; set; } = true;
    public List<string> AdBlockCustomRules { get; set; } = new();
    public long AdBlockTotalAdsBlocked { get; set; }
    public long AdBlockTotalTrackersBlocked { get; set; }
    public string DownloadDirectory { get; set; } = string.Empty;
    public string TranslateTargetLanguage { get; set; } = "vi";
    public int AutoLockIdleMinutes { get; set; } = 5;

    // === Cấu hình Thanh bên (Sidebar kiểu Opera GX) ===
    public bool SidebarShowZalo { get; set; } = true;
    public bool SidebarShowMessenger { get; set; } = true;
    public bool SidebarShowDiscord { get; set; } = true;
    public bool SidebarShowTelegram { get; set; } = false;
    public bool SidebarShowAiChat { get; set; } = true;
    public bool SidebarShowBookmarks { get; set; } = true;
    public bool SidebarShowHistory { get; set; } = true;
    public bool SidebarShowDownloads { get; set; } = true;
}
