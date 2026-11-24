using System.Collections.Concurrent;
using System.Text;
using QUANGLNBrowser.Core.Interfaces;

namespace QUANGLNBrowser.Core.Services;

/// <summary>
/// Bộ lọc quảng cáo 2 tầng thuần C# (Fallback tốc độ cao &lt;0.1ms):
/// - Tầng 1: Chặn request mạng (Domain O(1) lookup, Domain+Path indexed lookup, Third-party, ResourceType, Exception @@).
/// - Tầng 2: Cosmetic Filtering (tiêm CSS ẩn phần tử quảng cáo toàn cục và theo từng tên miền).
/// </summary>
public sealed class ManagedAdBlockEngine : IAdBlockEngine
{
    private sealed record NetworkRule(
        string? DomainAnchor,
        string? PathOrToken,
        bool ThirdPartyOnly,
        bool FirstPartyOnly,
        HashSet<string>? AllowedResourceTypes,
        HashSet<string>? IncludedSourceDomains,
        HashSet<string>? ExcludedSourceDomains);

    private sealed class FilterSnapshot
    {
        public HashSet<string> BlockedDomains { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ThirdPartyBlockedDomains { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExceptionDomains { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ExceptionTokens { get; } = new();
        public Dictionary<string, List<NetworkRule>> RulesByDomain { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<NetworkRule> GeneralUrlRules { get; } = new();

        public HashSet<string> GlobalCosmeticSelectors { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CosmeticExceptions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> DomainCosmeticSelectors { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string CachedGlobalCss { get; set; } = string.Empty;
        public int TotalRules { get; set; }
    }

    private static readonly string[] BaselineRules =
    [
        // Global Ad & Tracking Networks (EasyList / EasyPrivacy core)
        "||doubleclick.net^",
        "||googlesyndication.com^",
        "||googleadservices.com^",
        "||adservice.google.com^",
        "||google-analytics.com^$third-party",
        "||googletagmanager.com^$third-party",
        "||adnxs.com^",
        "||criteo.com^",
        "||criteo.net^",
        "||taboola.com^",
        "||outbrain.com^",
        "||rubiconproject.com^",
        "||pubmatic.com^",
        "||openx.net^",
        "||moatads.com^",
        "||scorecardresearch.com^",
        "||casalemedia.com^",
        "||advertising.com^",
        "||adform.net^",
        "||smartadserver.com^",
        "||teads.tv^",
        "||popads.net^",
        "||popcash.net^",
        "||propellerads.com^",
        "||adsterratools.com^",
        "||adsterra.com^",
        "||clickadu.com^",
        "||hilltopads.net^",
        "||exoclick.com^",
        "||mgid.com^",
        "||adskeeper.com^",
        "||hotjar.com^$third-party",
        "||mixpanel.com^$third-party",
        "||clarity.ms^$third-party",
        "||quantserve.com^$third-party",
        "||coinhive.com^",
        "||coin-hive.com^",
        "||jsecoin.com^",
        // YouTube Ads (Cốc Cốc-style YouTube Ad Blocking)
        "||youtube.com/pagead/",
        "||youtube.com/api/stats/ads",
        "||youtube.com/ptracking",
        "||youtube.com/get_midroll_info",
        // Vietnamese Ad Networks (ABPVN core)
        "||admicro.vn^",
        "||vcmedia.vn/ads/",
        "||ants.vn^",
        "||eclick.vn^",
        "||novanet.vn^",
        "||ambientdigitalgroup.com^",
        "||adtimaserver.vn^",
        "||adtima.vn^",
        "||MathAds.vn^",
        "||polyad.net^",
        "||vietad.vn^",
        "||ad360.vn^",
        "||yomedia.vn^",
        "||123click.vn^",
        "||adnetwork.vn^",
        "||blueseed.tv^",
        "||urekamedia.vn^",
        "||adx.vn^",
        "||a.vnecdn.net/ads",
        // Common URL tokens
        "/ads/banner",
        "/adserver.",
        "/pagead/js/adsbygoogle.js",
        // Cosmetic Filtering (Global, YouTube & VN news portals)
        "##.adsbox",
        "##.ad-banner",
        "##.ad-container",
        "##.advertisement",
        "##ins.adsbygoogle",
        "##[id^=\"div-gpt-ad\"]",
        "##.box-quangcao",
        "##.banner-quangcao",
        "##.qc-banner",
        "##.adm-banner",
        "youtube.com##ytd-ad-slot-renderer",
        "youtube.com##ytd-promoted-sparkles-web-renderer",
        "youtube.com##ytd-in-feed-ad-layout-renderer",
        "youtube.com##ytd-banner-promo-renderer",
        "youtube.com###player-ads",
        "youtube.com###masthead-ad",
        "vnexpress.net##.box_quangcao",
        "dantri.com.vn##.dt-ads",
        "tuoitre.vn##.box-ads",
        "thanhnien.vn##.zone-ads",
        "24h.com.vn##.banner-ads",
        "kenh14.vn##.adm-banner",
        "soha.vn##.ad-zone",
        "cafef.vn##.adm-banner"
    ];

    private static readonly HashSet<string> KnownTrackerHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "google-analytics.com",
        "googletagmanager.com",
        "scorecardresearch.com",
        "moatads.com",
        "hotjar.com",
        "mixpanel.com",
        "segment.io",
        "clarity.ms",
        "quantserve.com",
        "criteo.com",
        "criteo.net",
        "mc.yandex.ru",
        "amplitude.com",
        "fullstory.com",
        "newrelic.com",
        "coinhive.com",
        "coin-hive.com",
        "jsecoin.com"
    };

    private static readonly HashSet<string> AcceptableSearchAdSourceHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "google.com",
        "google.com.vn",
        "coccoc.com"
    };

    private readonly AdBlockFilterListUpdater _updater;
    private readonly ConcurrentDictionary<Guid, int> _tabBlockedCounts = new();
    private readonly ConcurrentDictionary<Guid, int> _tabBlockedTrackersCounts = new();
    private readonly ConcurrentDictionary<string, byte> _whitelistedDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _customRules = new();
    private readonly object _reloadLock = new();
    private FilterSnapshot _baseSnapshot = new();
    private volatile FilterSnapshot _snapshot = new();
    private long _lifetimeAdsBlocked;
    private long _lifetimeTrackersBlocked;
    private string _blockingMode = "standard";
    private bool _disposed;

    public event EventHandler<Guid>? BlockedCountChanged;

    public bool IsNativeEngine => false;
    public bool IsEnabled { get; set; } = true;
    public int TotalRulesCount => _snapshot.TotalRules;

    public string BlockingMode
    {
        get => _blockingMode;
        set => _blockingMode = string.Equals(value, "strict", StringComparison.OrdinalIgnoreCase) ? "strict" : "standard";
    }

    public bool BlockPopups { get; set; } = true;
    public bool BlockTrackers { get; set; } = true;
    public bool BlockYouTubeAds { get; set; } = true;
    public bool AllowAcceptableAds { get; set; } = true;

    public bool EnableEasyList { get; set; } = true;
    public bool EnableEasyPrivacy { get; set; } = true;
    public bool EnableAbpvn { get; set; } = true;
    public bool EnableAntiAnnoyance { get; set; } = true;

    public long TotalLifetimeAdsBlocked
    {
        get => Interlocked.Read(ref _lifetimeAdsBlocked);
        set => Interlocked.Exchange(ref _lifetimeAdsBlocked, Math.Max(0, value));
    }

    public long TotalLifetimeTrackersBlocked
    {
        get => Interlocked.Read(ref _lifetimeTrackersBlocked);
        set => Interlocked.Exchange(ref _lifetimeTrackersBlocked, Math.Max(0, value));
    }

    public DateTime? LastFilterUpdateUtc => _updater.GetLatestFilterUpdateUtc();

    public IReadOnlyList<string> CustomRules
    {
        get
        {
            lock (_reloadLock)
            {
                return _customRules.ToList();
            }
        }
    }

    public ManagedAdBlockEngine(AdBlockFilterListUpdater? updater = null, bool loadBaseline = true)
    {
        _updater = updater ?? new AdBlockFilterListUpdater();
        if (loadBaseline)
        {
            LoadRawRules(BaselineRules, resetExisting: true);
        }
    }

    public bool IsTrackerUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string host = ExtractHost(url);
        if (!string.IsNullOrEmpty(host) && MatchesDomainOrParent(host, KnownTrackerHosts))
        {
            return true;
        }

        return url.Contains("fbevents.js", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/analytics.js", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/gtag/js", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/collect?", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("tracking", StringComparison.OrdinalIgnoreCase);
    }

    public bool ShouldBlock(string url, string sourceUrl, string resourceType)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("chrome-extension:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Không chặn tài liệu chính (main_frame) và luồng video/âm thanh (media) để đảm bảo chuẩn web video
        string normalizedType = NormalizeResourceType(resourceType);
        if (normalizedType == "main_frame" || normalizedType == "media")
        {
            return false;
        }

        string requestHost = ExtractHost(url);
        if (string.IsNullOrEmpty(requestHost))
        {
            return false;
        }

        string sourceHost = ExtractHost(sourceUrl);
        if (!string.IsNullOrEmpty(sourceHost) && IsDomainWhitelisted(sourceHost))
        {
            return false;
        }

        if (IsDomainWhitelisted(requestHost))
        {
            return false;
        }

        bool isStrict = string.Equals(_blockingMode, "strict", StringComparison.OrdinalIgnoreCase);

        // Chế độ Tiêu chuẩn (kiểu Cốc Cốc): tôn trọng Quảng cáo chấp nhận được trên trang tìm kiếm khi AllowAcceptableAds bật
        if (!isStrict && AllowAcceptableAds &&
            !string.IsNullOrEmpty(sourceHost) &&
            MatchesDomainOrParent(sourceHost, AcceptableSearchAdSourceHosts) &&
            (requestHost.EndsWith("googleadservices.com", StringComparison.OrdinalIgnoreCase) ||
             requestHost.EndsWith("qc.coccoc.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Nếu người dùng tắt chặn quảng cáo YouTube thì bỏ qua các request nội bộ YouTube
        if (!BlockYouTubeAds &&
            (requestHost.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) ||
             sourceHost.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)) &&
            (url.Contains("/pagead/", StringComparison.OrdinalIgnoreCase) ||
             url.Contains("/api/stats/ads", StringComparison.OrdinalIgnoreCase) ||
             url.Contains("/ptracking", StringComparison.OrdinalIgnoreCase) ||
             url.Contains("/get_midroll_info", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Nếu tắt chặn trình theo dõi ở chế độ Tiêu chuẩn, bỏ qua các tracker thuần túy
        if (!isStrict && !BlockTrackers && IsTrackerUrl(url))
        {
            return false;
        }

        var snap = _snapshot;

        // 1. Kiểm tra Exception Domains (@@||domain^)
        if (MatchesDomainOrParent(requestHost, snap.ExceptionDomains))
        {
            return false;
        }

        // 2. Kiểm tra Exception URL Tokens
        for (int i = 0; i < snap.ExceptionTokens.Count; i++)
        {
            if (url.Contains(snap.ExceptionTokens[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        bool isThirdParty = !string.IsNullOrEmpty(sourceHost) && !IsSameRootDomain(requestHost, sourceHost);

        // 3. O(1) Domain Lookup (||domain^)
        if (MatchesDomainOrParent(requestHost, snap.BlockedDomains))
        {
            return true;
        }

        // 4. O(1) Third-Party Domain Lookup (||domain^$third-party)
        if (isThirdParty && MatchesDomainOrParent(requestHost, snap.ThirdPartyBlockedDomains))
        {
            return true;
        }

        // 5. Domain-anchored Rules có Path hoặc Option (tra theo từng cấp domain của requestHost)
        if (MatchesDomainAnchoredRules(snap, requestHost, url, sourceHost, isThirdParty, normalizedType))
        {
            return true;
        }

        // 6. General URL Substring Rules
        for (int i = 0; i < snap.GeneralUrlRules.Count; i++)
        {
            var rule = snap.GeneralUrlRules[i];
            if (MatchesRuleConstraints(rule, url, sourceHost, isThirdParty, normalizedType))
            {
                return true;
            }
        }

        return false;
    }

    public void IncrementBlockedCount(Guid tabId) =>
        IncrementBlockedCount(tabId, isTracker: false);

    public void IncrementBlockedCount(Guid tabId, bool isTracker)
    {
        _tabBlockedCounts.AddOrUpdate(tabId, 1, (_, count) => count + 1);
        if (isTracker)
        {
            _tabBlockedTrackersCounts.AddOrUpdate(tabId, 1, (_, count) => count + 1);
            Interlocked.Increment(ref _lifetimeTrackersBlocked);
        }
        else
        {
            Interlocked.Increment(ref _lifetimeAdsBlocked);
        }
        BlockedCountChanged?.Invoke(this, tabId);
    }

    public int GetBlockedCount(Guid tabId) =>
        _tabBlockedCounts.TryGetValue(tabId, out int count) ? count : 0;

    public int GetBlockedTrackersCount(Guid tabId) =>
        _tabBlockedTrackersCounts.TryGetValue(tabId, out int count) ? count : 0;

    public void ResetTabCount(Guid tabId)
    {
        _tabBlockedCounts[tabId] = 0;
        _tabBlockedTrackersCounts[tabId] = 0;
        BlockedCountChanged?.Invoke(this, tabId);
    }

    public void SetCustomRules(IEnumerable<string> rules)
    {
        lock (_reloadLock)
        {
            _customRules.Clear();
            foreach (string r in rules)
            {
                string trimmed = (r ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(trimmed) && !_customRules.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    _customRules.Add(trimmed);
                }
            }
            RebuildSnapshotWithCustomRulesLocked();
        }
    }

    public void AddCustomRule(string rule)
    {
        string trimmed = (rule ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!_customRules.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                _customRules.Add(trimmed);
                RebuildSnapshotWithCustomRulesLocked();
            }
        }
    }

    public void RemoveCustomRule(string rule)
    {
        string trimmed = (rule ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        lock (_reloadLock)
        {
            int removed = _customRules.RemoveAll(r => string.Equals(r, trimmed, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                RebuildSnapshotWithCustomRulesLocked();
            }
        }
    }

    public bool IsDomainWhitelisted(string domain)
    {
        string clean = NormalizeDomain(domain);
        if (string.IsNullOrEmpty(clean))
        {
            return false;
        }

        ReadOnlySpan<char> span = clean.AsSpan();
        while (!span.IsEmpty)
        {
            if (_whitelistedDomains.ContainsKey(span.ToString()))
            {
                return true;
            }

            int dot = span.IndexOf('.');
            if (dot < 0)
            {
                break;
            }
            span = span[(dot + 1)..];
        }

        return false;
    }

    public Task SetDomainWhitelistAsync(string domain, bool whitelisted)
    {
        string clean = NormalizeDomain(domain);
        if (!string.IsNullOrEmpty(clean))
        {
            if (whitelisted)
            {
                _whitelistedDomains[clean] = 1;
            }
            else
            {
                _whitelistedDomains.TryRemove(clean, out _);
            }
        }
        return Task.CompletedTask;
    }

    public void SetWhitelistedDomains(IEnumerable<string> domains)
    {
        _whitelistedDomains.Clear();
        foreach (string domain in domains)
        {
            string clean = NormalizeDomain(domain);
            if (!string.IsNullOrEmpty(clean))
            {
                _whitelistedDomains[clean] = 1;
            }
        }
    }

    public string? GetCosmeticCss(string url)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string host = ExtractHost(url);
        if (string.IsNullOrEmpty(host) || IsDomainWhitelisted(host))
        {
            return null;
        }

        var snap = _snapshot;
        var domainSelectors = new List<string>();

        ReadOnlySpan<char> span = host.AsSpan();
        while (!span.IsEmpty)
        {
            string candidate = span.ToString();
            if (snap.DomainCosmeticSelectors.TryGetValue(candidate, out var list))
            {
                foreach (string sel in list)
                {
                    if (!snap.CosmeticExceptions.Contains(sel))
                    {
                        domainSelectors.Add(sel);
                    }
                }
            }

            int dot = span.IndexOf('.');
            if (dot < 0)
            {
                break;
            }
            span = span[(dot + 1)..];
        }

        if (domainSelectors.Count == 0)
        {
            return string.IsNullOrEmpty(snap.CachedGlobalCss) ? null : snap.CachedGlobalCss;
        }

        var sb = new StringBuilder(snap.CachedGlobalCss.Length + domainSelectors.Count * 32);
        if (!string.IsNullOrEmpty(snap.CachedGlobalCss))
        {
            sb.AppendLine(snap.CachedGlobalCss);
        }

        sb.Append(string.Join(", ", domainSelectors));
        sb.Append(" { display: none !important; visibility: hidden !important; }");
        return sb.ToString();
    }

    public Task LoadFiltersAsync(CancellationToken cancellationToken = default) =>
        LoadFiltersAsync(forceDownload: false, cancellationToken);

    public async Task LoadFiltersAsync(bool forceDownload, CancellationToken cancellationToken = default)
    {
        var enabledKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (EnableEasyList) enabledKeys.Add("easylist");
        if (EnableEasyPrivacy && BlockTrackers) enabledKeys.Add("easyprivacy");
        if (EnableAbpvn) enabledKeys.Add("abpvn");
        if (EnableAntiAnnoyance) enabledKeys.Add("annoyances");

        var files = await _updater.EnsureFilterFilesAsync(forceDownload, enabledKeys, cancellationToken);
        await Task.Run(() =>
        {
            var allLines = new List<string>(BaselineRules);
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    allLines.AddRange(File.ReadLines(file));
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Failed to read filter file {file}", ex);
                }
            }

            LoadRawRules(allLines, resetExisting: true);
            AppLogger.Info($"AdBlock engine loaded {_snapshot.TotalRules} active rules from {files.Count} filter files.");
        }, cancellationToken);
    }

    public void LoadRawRules(IEnumerable<string> lines, bool resetExisting = false)
    {
        lock (_reloadLock)
        {
            var nextBase = resetExisting ? new FilterSnapshot() : CloneSnapshot(_baseSnapshot);

            foreach (string rawLine in lines)
            {
                ParseRuleLine(rawLine, nextBase);
            }

            BuildGlobalCssCache(nextBase);
            _baseSnapshot = nextBase;
            RebuildSnapshotWithCustomRulesLocked();
        }
    }

    private void RebuildSnapshotWithCustomRulesLocked()
    {
        if (_customRules.Count == 0)
        {
            _snapshot = _baseSnapshot;
            return;
        }

        var merged = CloneSnapshot(_baseSnapshot);
        foreach (string customRule in _customRules)
        {
            ParseRuleLine(customRule, merged);
        }
        BuildGlobalCssCache(merged);
        _snapshot = merged;
    }

    private static void BuildGlobalCssCache(FilterSnapshot snap)
    {
        if (snap.GlobalCosmeticSelectors.Count > 0)
        {
            var activeGlobals = snap.GlobalCosmeticSelectors
                .Where(s => !snap.CosmeticExceptions.Contains(s))
                .Take(1500)
                .ToList();

            snap.CachedGlobalCss = activeGlobals.Count > 0
                ? $"{string.Join(", ", activeGlobals)} {{ display: none !important; visibility: hidden !important; }}"
                : string.Empty;
        }
        else
        {
            snap.CachedGlobalCss = string.Empty;
        }
    }

    private static void ParseRuleLine(string rawLine, FilterSnapshot snap)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return;
        }

        string line = rawLine.Trim();
        if (line[0] == '!' || line.StartsWith("[Adblock", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Bỏ qua extended CSS/JSlet của ABP/uBO (#?#, #$#, #@?#, #%#)
        if (line.Contains("#?#", StringComparison.Ordinal) ||
            line.Contains("#$#", StringComparison.Ordinal) ||
            line.Contains("#%#", StringComparison.Ordinal))
        {
            return;
        }

        // 1. Cosmetic Exception (#@#)
        int cosmeticExIdx = line.IndexOf("#@#", StringComparison.Ordinal);
        if (cosmeticExIdx >= 0)
        {
            string exSelector = line[(cosmeticExIdx + 3)..].Trim();
            if (!string.IsNullOrEmpty(exSelector))
            {
                snap.CosmeticExceptions.Add(exSelector);
                snap.TotalRules++;
            }
            return;
        }

        // 2. Cosmetic Filtering (##)
        int cosmeticIdx = line.IndexOf("##", StringComparison.Ordinal);
        if (cosmeticIdx >= 0)
        {
            string domainPart = line[..cosmeticIdx].Trim();
            string selector = line[(cosmeticIdx + 2)..].Trim();
            if (!IsValidCssSelector(selector))
            {
                return;
            }

            if (string.IsNullOrEmpty(domainPart))
            {
                if (snap.GlobalCosmeticSelectors.Count < 1500 && snap.GlobalCosmeticSelectors.Add(selector))
                {
                    snap.TotalRules++;
                }
            }
            else
            {
                string[] domains = domainPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string d in domains)
                {
                    if (d.StartsWith('~'))
                    {
                        continue;
                    }
                    string cleanDomain = NormalizeDomain(d);
                    if (string.IsNullOrEmpty(cleanDomain))
                    {
                        continue;
                    }
                    if (!snap.DomainCosmeticSelectors.TryGetValue(cleanDomain, out var list))
                    {
                        list = new List<string>();
                        snap.DomainCosmeticSelectors[cleanDomain] = list;
                    }
                    if (!list.Contains(selector, StringComparer.Ordinal))
                    {
                        list.Add(selector);
                        snap.TotalRules++;
                    }
                }
            }
            return;
        }

        // 3. Hosts file line (0.0.0.0 domain / 127.0.0.1 domain)
        if (line.StartsWith("0.0.0.0 ", StringComparison.Ordinal) ||
            line.StartsWith("127.0.0.1 ", StringComparison.Ordinal))
        {
            string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                string host = NormalizeDomain(parts[1]);
                if (!string.IsNullOrEmpty(host) && host != "localhost")
                {
                    if (snap.BlockedDomains.Add(host))
                    {
                        snap.TotalRules++;
                    }
                }
            }
            return;
        }

        // 4. Network Exception Rule (@@)
        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            ParseExceptionRule(line[2..], snap);
            return;
        }

        // 5. Network Blocking Rule
        ParseNetworkBlockRule(line, snap);
    }

    private static void ParseExceptionRule(string ruleBody, FilterSnapshot snap)
    {
        int dollarIdx = ruleBody.IndexOf('$');
        string pattern = dollarIdx >= 0 ? ruleBody[..dollarIdx] : ruleBody;

        if (pattern.StartsWith("||", StringComparison.Ordinal))
        {
            string rest = pattern[2..];
            int sepIdx = rest.IndexOfAny(['^', '/']);
            string hostPart = sepIdx >= 0 ? rest[..sepIdx] : rest;
            string afterSep = sepIdx >= 0 ? rest[(sepIdx + 1)..] : string.Empty;

            string cleanHost = NormalizeDomain(hostPart);
            if (!string.IsNullOrEmpty(cleanHost) && string.IsNullOrEmpty(afterSep))
            {
                if (snap.ExceptionDomains.Add(cleanHost))
                {
                    snap.TotalRules++;
                }
                return;
            }
        }

        string token = CleanUrlToken(pattern);
        if (token.Length >= 4)
        {
            snap.ExceptionTokens.Add(token);
            snap.TotalRules++;
        }
    }

    private static void ParseNetworkBlockRule(string line, FilterSnapshot snap)
    {
        // Bỏ qua regex phức tạp /.../
        if (line.Length > 2 && line[0] == '/' && line[^1] == '/')
        {
            return;
        }

        string pattern = line;
        string? optionsPart = null;

        int dollarIdx = line.LastIndexOf('$');
        if (dollarIdx > 0)
        {
            pattern = line[..dollarIdx];
            optionsPart = line[(dollarIdx + 1)..];
        }

        bool thirdPartyOnly = false;
        bool firstPartyOnly = false;
        HashSet<string>? allowedTypes = null;
        HashSet<string>? includedDomains = null;
        HashSet<string>? excludedDomains = null;

        if (!string.IsNullOrEmpty(optionsPart))
        {
            if (!TryParseOptions(optionsPart, out thirdPartyOnly, out firstPartyOnly, out allowedTypes, out includedDomains, out excludedDomains))
            {
                return;
            }
        }

        bool hasAdvancedConstraints = firstPartyOnly
            || allowedTypes is not null
            || includedDomains is not null
            || excludedDomains is not null;

        if (pattern.StartsWith("||", StringComparison.Ordinal))
        {
            string rest = pattern[2..];
            int sepIdx = rest.IndexOfAny(['^', '/']);
            string hostPart = sepIdx >= 0 ? rest[..sepIdx] : rest;
            string pathPart = sepIdx >= 0 ? rest[sepIdx..] : string.Empty;

            if (hostPart.Contains('*'))
            {
                return;
            }

            string cleanHost = NormalizeDomain(hostPart);
            if (string.IsNullOrEmpty(cleanHost))
            {
                return;
            }

            // Trường hợp ||domain^ hoặc ||domain (không có path phía sau)
            bool isPureDomain = string.IsNullOrEmpty(pathPart) || pathPart == "^" || pathPart == "^*";
            if (isPureDomain && !hasAdvancedConstraints)
            {
                if (thirdPartyOnly)
                {
                    if (snap.ThirdPartyBlockedDomains.Add(cleanHost))
                    {
                        snap.TotalRules++;
                    }
                }
                else
                {
                    if (snap.BlockedDomains.Add(cleanHost))
                    {
                        snap.TotalRules++;
                    }
                }
                return;
            }

            string? cleanPath = isPureDomain ? null : CleanUrlToken(pathPart);
            var rule = new NetworkRule(
                cleanHost,
                cleanPath,
                thirdPartyOnly,
                firstPartyOnly,
                allowedTypes,
                includedDomains,
                excludedDomains);

            if (!snap.RulesByDomain.TryGetValue(cleanHost, out var list))
            {
                list = new List<NetworkRule>();
                snap.RulesByDomain[cleanHost] = list;
            }
            list.Add(rule);
            snap.TotalRules++;
            return;
        }

        string generalToken = CleanUrlToken(pattern);
        if (generalToken.Length >= 5 && !generalToken.Contains('*') && snap.GeneralUrlRules.Count < 4000)
        {
            snap.GeneralUrlRules.Add(new NetworkRule(
                null,
                generalToken,
                thirdPartyOnly,
                firstPartyOnly,
                allowedTypes,
                includedDomains,
                excludedDomains));
            snap.TotalRules++;
        }
    }

    private static bool TryParseOptions(
        string optionsPart,
        out bool thirdPartyOnly,
        out bool firstPartyOnly,
        out HashSet<string>? allowedTypes,
        out HashSet<string>? includedDomains,
        out HashSet<string>? excludedDomains)
    {
        thirdPartyOnly = false;
        firstPartyOnly = false;
        allowedTypes = null;
        includedDomains = null;
        excludedDomains = null;

        string[] opts = optionsPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string rawOpt in opts)
        {
            string opt = rawOpt.ToLowerInvariant();

            if (opt is "popup" or "elemhide" or "generichide" or "genericblock" or "badfilter" or "document" or "all" ||
                opt.StartsWith("csp=", StringComparison.Ordinal) ||
                opt.StartsWith("rewrite=", StringComparison.Ordinal) ||
                opt.StartsWith("redirect=", StringComparison.Ordinal) ||
                opt.StartsWith("redirect-rule=", StringComparison.Ordinal) ||
                opt.StartsWith("removeparam", StringComparison.Ordinal))
            {
                return false;
            }

            if (opt is "third-party" or "3p")
            {
                thirdPartyOnly = true;
            }
            else if (opt is "~third-party" or "~3p" or "1p" or "first-party")
            {
                firstPartyOnly = true;
            }
            else if (opt is "important" or "match-case")
            {
                // Cho phép tiếp tục
            }
            else if (opt.StartsWith("domain=", StringComparison.Ordinal))
            {
                string domainList = opt[7..];
                foreach (string d in domainList.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (d.StartsWith('~'))
                    {
                        string exDom = NormalizeDomain(d[1..]);
                        if (!string.IsNullOrEmpty(exDom))
                        {
                            excludedDomains ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            excludedDomains.Add(exDom);
                        }
                    }
                    else
                    {
                        string inDom = NormalizeDomain(d);
                        if (!string.IsNullOrEmpty(inDom))
                        {
                            includedDomains ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            includedDomains.Add(inDom);
                        }
                    }
                }
            }
            else if (!opt.StartsWith('~'))
            {
                string? mapped = MapOptionResourceType(opt);
                if (mapped is not null)
                {
                    allowedTypes ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    allowedTypes.Add(mapped);
                }
            }
        }

        return true;
    }

    private static string? MapOptionResourceType(string opt) => opt switch
    {
        "script" => "script",
        "image" => "image",
        "stylesheet" or "css" => "stylesheet",
        "xmlhttprequest" or "xhr" => "xhr",
        "subdocument" or "frame" => "sub_frame",
        "media" => "media",
        "font" => "font",
        "ping" => "ping",
        _ => null
    };

    private static string NormalizeResourceType(string resourceType)
    {
        if (string.IsNullOrEmpty(resourceType))
        {
            return "other";
        }

        string lower = resourceType.ToLowerInvariant();
        return lower switch
        {
            "mainframe" or "main_frame" => "main_frame",
            "subframe" or "sub_frame" => "sub_frame",
            "stylesheet" => "stylesheet",
            "script" => "script",
            "image" => "image",
            "fontresource" or "font" => "font",
            "media" => "media",
            "xhr" or "xmlhttprequest" => "xhr",
            "ping" => "ping",
            _ => lower
        };
    }

    private static bool MatchesDomainAnchoredRules(
        FilterSnapshot snap,
        string requestHost,
        string url,
        string sourceHost,
        bool isThirdParty,
        string normalizedType)
    {
        ReadOnlySpan<char> span = requestHost.AsSpan();
        while (!span.IsEmpty)
        {
            if (snap.RulesByDomain.TryGetValue(span.ToString(), out var list))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (MatchesRuleConstraints(list[i], url, sourceHost, isThirdParty, normalizedType))
                    {
                        return true;
                    }
                }
            }

            int dot = span.IndexOf('.');
            if (dot < 0)
            {
                break;
            }
            span = span[(dot + 1)..];
        }

        return false;
    }

    private static bool MatchesRuleConstraints(
        NetworkRule rule,
        string url,
        string sourceHost,
        bool isThirdParty,
        string normalizedType)
    {
        if (rule.ThirdPartyOnly && !isThirdParty)
        {
            return false;
        }

        if (rule.FirstPartyOnly && isThirdParty)
        {
            return false;
        }

        if (rule.AllowedResourceTypes is not null && !rule.AllowedResourceTypes.Contains(normalizedType))
        {
            return false;
        }

        if (rule.ExcludedSourceDomains is not null &&
            !string.IsNullOrEmpty(sourceHost) &&
            MatchesDomainOrParent(sourceHost, rule.ExcludedSourceDomains))
        {
            return false;
        }

        if (rule.IncludedSourceDomains is not null)
        {
            if (string.IsNullOrEmpty(sourceHost) || !MatchesDomainOrParent(sourceHost, rule.IncludedSourceDomains))
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(rule.PathOrToken) &&
            !url.Contains(rule.PathOrToken, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesDomainOrParent(string host, HashSet<string> domainSet)
    {
        if (domainSet.Count == 0 || string.IsNullOrEmpty(host))
        {
            return false;
        }

        ReadOnlySpan<char> span = host.AsSpan();
        while (!span.IsEmpty)
        {
            if (domainSet.Contains(span.ToString()))
            {
                return true;
            }

            int dot = span.IndexOf('.');
            if (dot < 0)
            {
                break;
            }
            span = span[(dot + 1)..];
        }

        return false;
    }

    private static bool IsSameRootDomain(string host1, string host2)
    {
        if (string.Equals(host1, host2, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string root1 = GetRegistrableDomainApprox(host1);
        string root2 = GetRegistrableDomainApprox(host2);
        return string.Equals(root1, root2, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRegistrableDomainApprox(string host)
    {
        string[] parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2)
        {
            return host;
        }

        // Hỗ trợ tên miền 2 cấp phổ biến tại Việt Nam / Quốc tế (.com.vn, .net.vn, .org.vn, .edu.vn, .gov.vn, .co.uk)
        string lastTwo = $"{parts[^2]}.{parts[^1]}";
        if (lastTwo.Equals("com.vn", StringComparison.OrdinalIgnoreCase) ||
            lastTwo.Equals("net.vn", StringComparison.OrdinalIgnoreCase) ||
            lastTwo.Equals("org.vn", StringComparison.OrdinalIgnoreCase) ||
            lastTwo.Equals("edu.vn", StringComparison.OrdinalIgnoreCase) ||
            lastTwo.Equals("gov.vn", StringComparison.OrdinalIgnoreCase) ||
            lastTwo.Equals("co.uk", StringComparison.OrdinalIgnoreCase))
        {
            return parts.Length >= 3 ? $"{parts[^3]}.{lastTwo}" : host;
        }

        return lastTwo;
    }

    public static string ExtractHost(string? urlOrDomain)
    {
        if (string.IsNullOrWhiteSpace(urlOrDomain))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(urlOrDomain, UriKind.Absolute, out var uri))
        {
            return NormalizeDomain(uri.Host);
        }

        return NormalizeDomain(urlOrDomain);
    }

    public static string NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return string.Empty;
        }

        string d = domain.Trim().Trim('.').ToLowerInvariant();
        if (d.StartsWith("www.", StringComparison.Ordinal))
        {
            d = d[4..];
        }
        return d;
    }

    private static string CleanUrlToken(string raw)
    {
        string s = raw.Trim().Trim('|', '*');
        if (s.StartsWith('^'))
        {
            s = s[1..];
        }
        if (s.EndsWith('^'))
        {
            s = s[..^1];
        }
        return s.Replace('^', '/');
    }

    private static bool IsValidCssSelector(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector.Length > 240)
        {
            return false;
        }

        if (selector.Contains('{') ||
            selector.Contains('}') ||
            selector.Contains(":-abp-", StringComparison.OrdinalIgnoreCase) ||
            selector.Contains(":has-text(", StringComparison.OrdinalIgnoreCase) ||
            selector.Contains(":xpath(", StringComparison.OrdinalIgnoreCase) ||
            selector.Contains(":upward(", StringComparison.OrdinalIgnoreCase) ||
            selector.Contains(":remove(", StringComparison.OrdinalIgnoreCase) ||
            selector.Contains(":style(", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static FilterSnapshot CloneSnapshot(FilterSnapshot src)
    {
        var copy = new FilterSnapshot
        {
            CachedGlobalCss = src.CachedGlobalCss,
            TotalRules = src.TotalRules
        };

        foreach (string d in src.BlockedDomains) copy.BlockedDomains.Add(d);
        foreach (string d in src.ThirdPartyBlockedDomains) copy.ThirdPartyBlockedDomains.Add(d);
        foreach (string d in src.ExceptionDomains) copy.ExceptionDomains.Add(d);
        copy.ExceptionTokens.AddRange(src.ExceptionTokens);

        foreach (var kv in src.RulesByDomain)
        {
            copy.RulesByDomain[kv.Key] = new List<NetworkRule>(kv.Value);
        }
        copy.GeneralUrlRules.AddRange(src.GeneralUrlRules);

        foreach (string s in src.GlobalCosmeticSelectors) copy.GlobalCosmeticSelectors.Add(s);
        foreach (string s in src.CosmeticExceptions) copy.CosmeticExceptions.Add(s);
        foreach (var kv in src.DomainCosmeticSelectors)
        {
            copy.DomainCosmeticSelectors[kv.Key] = new List<string>(kv.Value);
        }

        return copy;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _tabBlockedCounts.Clear();
        _tabBlockedTrackersCounts.Clear();
    }
}
