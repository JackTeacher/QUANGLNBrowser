using QUANGLNBrowser.Core.Services;
using QUANGLNBrowser.Security;
using Xunit;

namespace QUANGLNBrowser.Tests;

public sealed class AdBlockEngineTests : IDisposable
{
    private readonly string _tempRoot;

    public AdBlockEngineTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "QUANGLNBrowser_AdBlockTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void ManagedAdBlockEngine_BlocksBaselineGlobalAndVietnameseAdDomains()
    {
        using var engine = new ManagedAdBlockEngine();
        Assert.True(engine.TotalRulesCount > 20);

        // Chặn mạng quảng cáo quốc tế (EasyList) và Việt Nam (ABPVN)
        Assert.True(engine.ShouldBlock(
            "https://securepubads.g.doubleclick.net/gampad/ads?iu=/1234",
            "https://vnexpress.net/thoi-su",
            "script"));

        Assert.True(engine.ShouldBlock(
            "https://media1.admicro.vn/core/admicro.js",
            "https://dantri.com.vn/",
            "script"));

        Assert.True(engine.ShouldBlock(
            "https://static.eclick.vn/ popped/banner.js",
            "https://tuoitre.vn/",
            "script"));

        // Không chặn tài nguyên nội dung hợp lệ hoặc main_frame
        Assert.False(engine.ShouldBlock(
            "https://vnexpress.net/thoi-su",
            "https://vnexpress.net/",
            "main_frame"));

        Assert.False(engine.ShouldBlock(
            "https://s1.vnecdn.net/vnexpress/restruct/c/v2641/pc/general.css",
            "https://vnexpress.net/thoi-su",
            "stylesheet"));
    }

    [Fact]
    public void ManagedAdBlockEngine_SupportsExceptionRules_ThirdParty_AndDomainConstraints()
    {
        using var engine = new ManagedAdBlockEngine(loadBaseline: false);
        engine.LoadRawRules(
        [
            "! Custom Filter List",
            "||ads.example.com^",
            "@@||safe.ads.example.com^",
            "||tracker.example.org^$third-party",
            "||cdn-ad.net/promo.js$script,domain=news.vn"
        ], resetExisting: true);

        // 1. Domain block & subdomain exception (@@||)
        Assert.True(engine.ShouldBlock("https://banner.ads.example.com/ad.js", "https://site.com", "script"));
        Assert.False(engine.ShouldBlock("https://safe.ads.example.com/widget.js", "https://site.com", "script"));

        // 2. Third-party ($third-party)
        Assert.True(engine.ShouldBlock("https://tracker.example.org/pixel.gif", "https://other-site.com", "image"));
        Assert.False(engine.ShouldBlock("https://tracker.example.org/pixel.gif", "https://www.example.org/home", "image"));

        // 3. Resource type + Source domain constraint ($script,domain=news.vn)
        Assert.True(engine.ShouldBlock("https://cdn-ad.net/promo.js", "https://sub.news.vn/article", "script"));
        Assert.False(engine.ShouldBlock("https://cdn-ad.net/promo.js", "https://other.vn/article", "script"));
        Assert.False(engine.ShouldBlock("https://cdn-ad.net/promo.js", "https://sub.news.vn/article", "image"));
    }

    [Fact]
    public async Task ManagedAdBlockEngine_WhitelistAndCosmeticCss_WorkTogether()
    {
        using var engine = new ManagedAdBlockEngine();

        string pageUrl = "https://vnexpress.net/tin-tuc-24h.html";
        string adScriptUrl = "https://securepubads.g.doubleclick.net/tag/js/gpt.js";

        // Khi chưa Whitelist: chặn request quảng cáo và sinh CSS ẩn vùng chứa quảng cáo
        Assert.True(engine.ShouldBlock(adScriptUrl, pageUrl, "script"));
        string? cssBefore = engine.GetCosmeticCss(pageUrl);
        Assert.NotNull(cssBefore);
        Assert.Contains(".adsbox", cssBefore);
        Assert.Contains(".box_quangcao", cssBefore);

        // Bật Whitelist cho vnexpress.net -> cho phép quảng cáo hiển thị lại (kể cả trên subdomain)
        await engine.SetDomainWhitelistAsync("vnexpress.net", true);
        Assert.True(engine.IsDomainWhitelisted("vnexpress.net"));
        Assert.True(engine.IsDomainWhitelisted("video.vnexpress.net"));
        Assert.False(engine.ShouldBlock(adScriptUrl, pageUrl, "script"));
        Assert.False(engine.ShouldBlock(adScriptUrl, "https://video.vnexpress.net/clip", "script"));
        Assert.Null(engine.GetCosmeticCss(pageUrl));

        // Tắt Whitelist -> chặn trở lại
        await engine.SetDomainWhitelistAsync("vnexpress.net", false);
        Assert.False(engine.IsDomainWhitelisted("vnexpress.net"));
        Assert.True(engine.ShouldBlock(adScriptUrl, pageUrl, "script"));
        Assert.NotNull(engine.GetCosmeticCss(pageUrl));
    }

    [Fact]
    public void ManagedAdBlockEngine_TracksPerTabBlockedCount_AndRaisesEvents()
    {
        using var engine = new ManagedAdBlockEngine();
        Guid tabId = Guid.NewGuid();
        int eventCount = 0;

        engine.BlockedCountChanged += (_, id) =>
        {
            if (id == tabId)
            {
                eventCount++;
            }
        };

        Assert.Equal(0, engine.GetBlockedCount(tabId));
        engine.IncrementBlockedCount(tabId);
        engine.IncrementBlockedCount(tabId);
        Assert.Equal(2, engine.GetBlockedCount(tabId));
        Assert.Equal(2, eventCount);

        engine.ResetTabCount(tabId);
        Assert.Equal(0, engine.GetBlockedCount(tabId));
        Assert.Equal(3, eventCount);
    }

    [Fact]
    public async Task VaultSecurityService_PersistsAdBlockDomainWhitelist_AcrossLockAndUnlock()
    {
        using var vault = new VaultSecurityService(_tempRoot);
        await vault.InitializeNewVaultAsync("Pin#1502", decoyPin: null, enableWindowsHello: false);

        await vault.SetDomainWhitelistAsync("www.vnexpress.net", true);
        await vault.SetDomainWhitelistAsync("dantri.com.vn", true);

        vault.Lock();
        var unlock = await vault.UnlockWithPinAsync("Pin#1502");
        Assert.True(unlock.Success);

        var list = await vault.GetWhitelistedDomainsAsync();
        Assert.Equal(2, list.Count);
        Assert.Contains("vnexpress.net", list);
        Assert.Contains("dantri.com.vn", list);

        await vault.SetDomainWhitelistAsync("vnexpress.net", false);
        var updatedList = await vault.GetWhitelistedDomainsAsync();
        Assert.Single(updatedList);
        Assert.Equal("dantri.com.vn", updatedList[0]);
    }

    [Fact]
    public void AdBlockEngineFactory_CreatesWorkingFallbackEngine_WhenRustDllAbsent()
    {
        using var engine = AdBlockEngineFactory.Create();
        Assert.NotNull(engine);
        Assert.True(engine.IsEnabled);
        Assert.True(engine.TotalRulesCount > 0);
    }

    [Fact]
    public void ManagedAdBlockEngine_SupportsCocCocModes_StandardAndStrict_AndAcceptableAds()
    {
        using var engine = new ManagedAdBlockEngine();

        // 1. Chế độ Tiêu chuẩn (Standard) + Cho phép quảng cáo chấp nhận được (Acceptable Ads) trên Google Tìm kiếm
        engine.BlockingMode = "standard";
        engine.AllowAcceptableAds = true;
        Assert.False(engine.ShouldBlock(
            "https://www.googleadservices.com/pagead/aclk?sa=L&ai=123",
            "https://www.google.com/search?q=laptop",
            "sub_frame"));

        // 2. Chế độ Nâng cao (Strict): Chặn cả quảng cáo tìm kiếm
        engine.BlockingMode = "strict";
        Assert.True(engine.ShouldBlock(
            "https://www.googleadservices.com/pagead/aclk?sa=L&ai=123",
            "https://www.google.com/search?q=laptop",
            "sub_frame"));

        // 3. YouTube Ad Blocking
        engine.BlockYouTubeAds = true;
        Assert.True(engine.ShouldBlock(
            "https://www.youtube.com/pagead/parallel?video_id=abc",
            "https://www.youtube.com/watch?v=abc",
            "xhr"));

        engine.BlockYouTubeAds = false;
        Assert.False(engine.ShouldBlock(
            "https://www.youtube.com/pagead/parallel?video_id=abc",
            "https://www.youtube.com/watch?v=abc",
            "xhr"));
    }

    [Fact]
    public void ManagedAdBlockEngine_SupportsCustomRules_AndElementPickerRules()
    {
        using var engine = new ManagedAdBlockEngine();
        int initialRules = engine.TotalRulesCount;

        // Thêm quy tắc tùy chỉnh (Cosmetic element picker & Network block)
        engine.AddCustomRule("kenh14.vn##.custom-floating-qc");
        engine.AddCustomRule("||annoying-popup.net^");

        Assert.Equal(initialRules + 2, engine.TotalRulesCount);
        Assert.Contains("kenh14.vn##.custom-floating-qc", engine.CustomRules);

        // Kiểm tra chặn theo quy tắc vừa thêm
        Assert.True(engine.ShouldBlock("https://annoying-popup.net/pop.js", "https://example.com", "script"));
        string? css = engine.GetCosmeticCss("https://kenh14.vn/star.chn");
        Assert.NotNull(css);
        Assert.Contains(".custom-floating-qc", css);

        // Xóa quy tắc
        engine.RemoveCustomRule("||annoying-popup.net^");
        Assert.False(engine.ShouldBlock("https://annoying-popup.net/pop.js", "https://example.com", "script"));
    }

    [Fact]
    public void ManagedAdBlockEngine_IdentifiesTrackers_AndIncrementsTrackerCount()
    {
        using var engine = new ManagedAdBlockEngine();
        Guid tabId = Guid.NewGuid();

        Assert.True(engine.IsTrackerUrl("https://www.google-analytics.com/analytics.js"));
        Assert.True(engine.IsTrackerUrl("https://connect.facebook.net/en_US/fbevents.js"));
        Assert.False(engine.IsTrackerUrl("https://vnexpress.net/images/logo.png"));

        engine.IncrementBlockedCount(tabId, isTracker: false);
        engine.IncrementBlockedCount(tabId, isTracker: true);

        Assert.Equal(2, engine.GetBlockedCount(tabId));
        Assert.Equal(1, engine.GetBlockedTrackersCount(tabId));
        Assert.Equal(1, engine.TotalLifetimeAdsBlocked);
        Assert.Equal(1, engine.TotalLifetimeTrackersBlocked);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Bỏ qua nếu file đang nhả handle
        }
    }
}
