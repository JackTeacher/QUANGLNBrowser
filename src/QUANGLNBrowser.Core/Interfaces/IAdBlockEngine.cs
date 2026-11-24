namespace QUANGLNBrowser.Core.Interfaces;

/// <summary>
/// Giao diện bộ lọc quảng cáo 2 tầng chuẩn Cốc Cốc (Tiêu chuẩn / Nâng cao, EasyList + EasyPrivacy + ABPVN + Anti-Annoyance,
/// Bộ lọc tùy chỉnh Element Picker, Thống kê quảng cáo &amp; trình theo dõi trọn đời).
/// Tự động chọn NativeRustAdBlockEngine nếu có adblock_rust.dll, ngược lại dùng ManagedAdBlockEngine fallback.
/// </summary>
public interface IAdBlockEngine : IDisposable
{
    event EventHandler<Guid>? BlockedCountChanged;

    bool IsNativeEngine { get; }
    bool IsEnabled { get; set; }
    int TotalRulesCount { get; }

    /// <summary>
    /// Chế độ chặn kiểu Cốc Cốc: "standard" (Tiêu chuẩn) hoặc "strict" (Nâng cao).
    /// </summary>
    string BlockingMode { get; set; }

    bool BlockPopups { get; set; }
    bool BlockTrackers { get; set; }
    bool BlockYouTubeAds { get; set; }
    bool AllowAcceptableAds { get; set; }

    bool EnableEasyList { get; set; }
    bool EnableEasyPrivacy { get; set; }
    bool EnableAbpvn { get; set; }
    bool EnableAntiAnnoyance { get; set; }

    long TotalLifetimeAdsBlocked { get; set; }
    long TotalLifetimeTrackersBlocked { get; set; }
    DateTime? LastFilterUpdateUtc { get; }

    IReadOnlyList<string> CustomRules { get; }

    bool ShouldBlock(string url, string sourceUrl, string resourceType);
    bool IsTrackerUrl(string url);

    void IncrementBlockedCount(Guid tabId);
    void IncrementBlockedCount(Guid tabId, bool isTracker);
    int GetBlockedCount(Guid tabId);
    int GetBlockedTrackersCount(Guid tabId);
    void ResetTabCount(Guid tabId);

    bool IsDomainWhitelisted(string domain);
    Task SetDomainWhitelistAsync(string domain, bool whitelisted);
    void SetWhitelistedDomains(IEnumerable<string> domains);

    void SetCustomRules(IEnumerable<string> rules);
    void AddCustomRule(string rule);
    void RemoveCustomRule(string rule);

    string? GetCosmeticCss(string url);
    void LoadRawRules(IEnumerable<string> lines, bool resetExisting = false);
    Task LoadFiltersAsync(CancellationToken cancellationToken = default);
    Task LoadFiltersAsync(bool forceDownload, CancellationToken cancellationToken = default);
}
