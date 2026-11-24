using System.Runtime.InteropServices;
using QUANGLNBrowser.Core.Interfaces;

namespace QUANGLNBrowser.Core.Services;

/// <summary>
/// Lớp P/Invoke tích hợp adblock_rust.dll (x64) tại AppContext.BaseDirectory, kết hợp ManagedAdBlockEngine
/// để quản lý Whitelist, bộ đếm theo tab, Cosmetic CSS và fallback an toàn.
/// </summary>
public sealed class NativeRustAdBlockEngine : IAdBlockEngine
{
    private const string DllName = "adblock_rust.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr adblock_engine_create(string rulesText);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool adblock_engine_should_block(
        IntPtr enginePtr,
        string url,
        string sourceUrl,
        string resourceType);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void adblock_engine_destroy(IntPtr enginePtr);

    private readonly ManagedAdBlockEngine _managedCompanion;
    private readonly object _nativeLock = new();
    private IntPtr _nativeHandle;
    private bool _disposed;

    public event EventHandler<Guid>? BlockedCountChanged
    {
        add => _managedCompanion.BlockedCountChanged += value;
        remove => _managedCompanion.BlockedCountChanged -= value;
    }

    public bool IsNativeEngine => _nativeHandle != IntPtr.Zero;

    public bool IsEnabled
    {
        get => _managedCompanion.IsEnabled;
        set => _managedCompanion.IsEnabled = value;
    }

    public int TotalRulesCount => _managedCompanion.TotalRulesCount;

    public string BlockingMode
    {
        get => _managedCompanion.BlockingMode;
        set => _managedCompanion.BlockingMode = value;
    }

    public bool BlockPopups
    {
        get => _managedCompanion.BlockPopups;
        set => _managedCompanion.BlockPopups = value;
    }

    public bool BlockTrackers
    {
        get => _managedCompanion.BlockTrackers;
        set => _managedCompanion.BlockTrackers = value;
    }

    public bool BlockYouTubeAds
    {
        get => _managedCompanion.BlockYouTubeAds;
        set => _managedCompanion.BlockYouTubeAds = value;
    }

    public bool AllowAcceptableAds
    {
        get => _managedCompanion.AllowAcceptableAds;
        set => _managedCompanion.AllowAcceptableAds = value;
    }

    public bool EnableEasyList
    {
        get => _managedCompanion.EnableEasyList;
        set => _managedCompanion.EnableEasyList = value;
    }

    public bool EnableEasyPrivacy
    {
        get => _managedCompanion.EnableEasyPrivacy;
        set => _managedCompanion.EnableEasyPrivacy = value;
    }

    public bool EnableAbpvn
    {
        get => _managedCompanion.EnableAbpvn;
        set => _managedCompanion.EnableAbpvn = value;
    }

    public bool EnableAntiAnnoyance
    {
        get => _managedCompanion.EnableAntiAnnoyance;
        set => _managedCompanion.EnableAntiAnnoyance = value;
    }

    public long TotalLifetimeAdsBlocked
    {
        get => _managedCompanion.TotalLifetimeAdsBlocked;
        set => _managedCompanion.TotalLifetimeAdsBlocked = value;
    }

    public long TotalLifetimeTrackersBlocked
    {
        get => _managedCompanion.TotalLifetimeTrackersBlocked;
        set => _managedCompanion.TotalLifetimeTrackersBlocked = value;
    }

    public DateTime? LastFilterUpdateUtc => _managedCompanion.LastFilterUpdateUtc;
    public IReadOnlyList<string> CustomRules => _managedCompanion.CustomRules;

    public NativeRustAdBlockEngine(AdBlockFilterListUpdater? updater = null)
    {
        _managedCompanion = new ManagedAdBlockEngine(updater, loadBaseline: true);
        TryInitializeNativeHandle(string.Empty);
    }

    public static bool IsNativeDllPresent() =>
        File.Exists(AppPaths.GetNativeDllPath(DllName));

    private void TryInitializeNativeHandle(string rulesPayload)
    {
        if (!IsNativeDllPresent())
        {
            return;
        }

        lock (_nativeLock)
        {
            try
            {
                IntPtr newHandle = adblock_engine_create(rulesPayload);
                if (newHandle != IntPtr.Zero)
                {
                    if (_nativeHandle != IntPtr.Zero)
                    {
                        adblock_engine_destroy(_nativeHandle);
                    }
                    _nativeHandle = newHandle;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                AppLogger.Error("adblock_rust.dll load failed; falling back to ManagedAdBlockEngine", ex);
                _nativeHandle = IntPtr.Zero;
            }
        }
    }

    public bool ShouldBlock(string url, string sourceUrl, string resourceType)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string sourceHost = ManagedAdBlockEngine.ExtractHost(sourceUrl);
        if (!string.IsNullOrEmpty(sourceHost) && _managedCompanion.IsDomainWhitelisted(sourceHost))
        {
            return false;
        }

        string requestHost = ManagedAdBlockEngine.ExtractHost(url);
        if (!string.IsNullOrEmpty(requestHost) && _managedCompanion.IsDomainWhitelisted(requestHost))
        {
            return false;
        }

        IntPtr handle = _nativeHandle;
        if (handle != IntPtr.Zero)
        {
            try
            {
                return adblock_engine_should_block(handle, url, sourceUrl ?? string.Empty, resourceType ?? "other");
            }
            catch
            {
                // Nếu native call lỗi thì fallback sang managed
            }
        }

        return _managedCompanion.ShouldBlock(url, sourceUrl ?? string.Empty, resourceType ?? "other");
    }

    public bool IsTrackerUrl(string url) => _managedCompanion.IsTrackerUrl(url);

    public void IncrementBlockedCount(Guid tabId) => _managedCompanion.IncrementBlockedCount(tabId);
    public void IncrementBlockedCount(Guid tabId, bool isTracker) => _managedCompanion.IncrementBlockedCount(tabId, isTracker);
    public int GetBlockedCount(Guid tabId) => _managedCompanion.GetBlockedCount(tabId);
    public int GetBlockedTrackersCount(Guid tabId) => _managedCompanion.GetBlockedTrackersCount(tabId);
    public void ResetTabCount(Guid tabId) => _managedCompanion.ResetTabCount(tabId);

    public bool IsDomainWhitelisted(string domain) => _managedCompanion.IsDomainWhitelisted(domain);
    public Task SetDomainWhitelistAsync(string domain, bool whitelisted) => _managedCompanion.SetDomainWhitelistAsync(domain, whitelisted);
    public void SetWhitelistedDomains(IEnumerable<string> domains) => _managedCompanion.SetWhitelistedDomains(domains);

    public void SetCustomRules(IEnumerable<string> rules) => _managedCompanion.SetCustomRules(rules);
    public void AddCustomRule(string rule) => _managedCompanion.AddCustomRule(rule);
    public void RemoveCustomRule(string rule) => _managedCompanion.RemoveCustomRule(rule);

    public string? GetCosmeticCss(string url) => _managedCompanion.GetCosmeticCss(url);

    public void LoadRawRules(IEnumerable<string> lines, bool resetExisting = false)
    {
        var list = lines.ToList();
        _managedCompanion.LoadRawRules(list, resetExisting);
        if (IsNativeDllPresent())
        {
            TryInitializeNativeHandle(string.Join('\n', list));
        }
    }

    public Task LoadFiltersAsync(CancellationToken cancellationToken = default) =>
        _managedCompanion.LoadFiltersAsync(cancellationToken);

    public Task LoadFiltersAsync(bool forceDownload, CancellationToken cancellationToken = default) =>
        _managedCompanion.LoadFiltersAsync(forceDownload, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        lock (_nativeLock)
        {
            if (_nativeHandle != IntPtr.Zero)
            {
                try
                {
                    adblock_engine_destroy(_nativeHandle);
                }
                catch
                {
                    // Bỏ qua lỗi khi giải phóng native handle
                }
                _nativeHandle = IntPtr.Zero;
            }
        }

        _managedCompanion.Dispose();
    }
}
