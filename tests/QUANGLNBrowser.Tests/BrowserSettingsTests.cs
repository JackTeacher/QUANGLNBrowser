using System.IO;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;
using QUANGLNBrowser.Security;
using Xunit;

namespace QUANGLNBrowser.Tests;

public sealed class BrowserSettingsTests : IDisposable
{
    private readonly string _tempRoot;

    public BrowserSettingsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"quangln_settings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void UrlHelper_RecognizesSettingsUrls_AndExtractsSections()
    {
        Assert.True(UrlHelper.IsSettingsUrl("quangln://settings"));
        Assert.True(UrlHelper.IsSettingsUrl("coccoc://settings/vpn"));
        Assert.True(UrlHelper.IsSettingsUrl("chrome://settings/adblock"));
        Assert.False(UrlHelper.IsSettingsUrl("https://google.com"));

        Assert.Equal("General", UrlHelper.ExtractSettingsSection("quangln://settings"));
        Assert.Equal("VpnNetwork", UrlHelper.ExtractSettingsSection("coccoc://settings/vpn"));
        Assert.Equal("AdBlock", UrlHelper.ExtractSettingsSection("chrome://settings/adblock"));
        Assert.Equal("Appearance", UrlHelper.ExtractSettingsSection("quangln://settings/appearance"));
        Assert.Equal("SearchEngine", UrlHelper.ExtractSettingsSection("quangln://settings/search"));
        Assert.Equal("quangln://settings/vpnnetwork", UrlHelper.NormalizeUrlOrSearch("coccoc://settings/vpn"));
    }

    [Fact]
    public void UrlHelper_UsesConfiguredSearchEngine_ForOmniboxQueries()
    {
        Assert.Equal("https://coccoc.com/search?query=tin%20t%E1%BB%A9c", UrlHelper.NormalizeUrlOrSearch("tin tức", "coccoc"));
        Assert.Equal("https://www.bing.com/search?q=hello%20world", UrlHelper.NormalizeUrlOrSearch("hello world", "bing"));
        Assert.Equal("https://duckduckgo.com/?q=privacy", UrlHelper.NormalizeUrlOrSearch("privacy", "duckduckgo"));
        Assert.Equal("https://www.google.com/search?q=dotnet", UrlHelper.NormalizeUrlOrSearch("dotnet", "google"));
    }

    [Fact]
    public async Task VaultSecurityService_PersistsCocCocStyleBrowserSettings_Encrypted()
    {
        using var vault = new VaultSecurityService(_tempRoot);
        await vault.InitializeNewVaultAsync("SettingsPin#2026", decoyPin: null, enableWindowsHello: false);

        var prefs = new VpnPreferences
        {
            ThemeKey = "cyan",
            DefaultSearchEngine = "coccoc",
            StartupMode = "custom",
            StartupCustomUrl = "https://vnexpress.net",
            ShowHomeButton = false,
            ShowLeftSidebar = false,
            DefaultZoomPercent = 125,
            EnableAdBlock = true,
            DownloadDirectory = @"D:\MyDownloads",
            TranslateTargetLanguage = "en",
            AutoLockIdleMinutes = 15
        };

        await vault.SaveVpnPreferencesAsync(prefs);
        vault.Lock();

        var unlock = await vault.UnlockWithPinAsync("SettingsPin#2026");
        Assert.True(unlock.Success);

        var loaded = await vault.GetVpnPreferencesAsync();
        Assert.Equal("cyan", loaded.ThemeKey);
        Assert.Equal("coccoc", loaded.DefaultSearchEngine);
        Assert.Equal("custom", loaded.StartupMode);
        Assert.Equal("https://vnexpress.net", loaded.StartupCustomUrl);
        Assert.False(loaded.ShowHomeButton);
        Assert.False(loaded.ShowLeftSidebar);
        Assert.Equal(125, loaded.DefaultZoomPercent);
        Assert.True(loaded.EnableAdBlock);
        Assert.Equal(@"D:\MyDownloads", loaded.DownloadDirectory);
        Assert.Equal("en", loaded.TranslateTargetLanguage);
        Assert.Equal(15, loaded.AutoLockIdleMinutes);
    }

    [Fact]
    public async Task VaultSecurityService_UpdatesProfileName_ChangesPinWithRekey_AndManagesDecoyPin()
    {
        using var vault = new VaultSecurityService(_tempRoot);
        await vault.InitializeNewVaultAsync("OldPin#123", decoyPin: null, enableWindowsHello: false);
        await vault.AddBookmarkAsync("Test Site", "https://example.com");

        // 1. Đổi tên hồ sơ
        var renameRes = await vault.UpdateProfileNameAsync("Hồ sơ Công việc");
        Assert.True(renameRes.Success);
        Assert.Equal("Hồ sơ Công việc", vault.CurrentProfile?.Name);

        // 2. Thiết lập Decoy PIN mới
        var decoyRes = await vault.SetDecoyPinAsync("9999");
        Assert.True(decoyRes.Success);
        Assert.True(vault.HasDecoyProfile);

        // 3. Đổi Master PIN (rekey SQLCipher + giữ nguyên dữ liệu đã mã hóa)
        var badPinRes = await vault.ChangePinAsync("WrongPin", "NewPin#456");
        Assert.False(badPinRes.Success);

        var changePinRes = await vault.ChangePinAsync("OldPin#123", "NewPin#456");
        Assert.True(changePinRes.Success);

        vault.Lock();

        // PIN cũ phải thất bại, PIN mới phải mở được và giữ nguyên Bookmark + Tên hồ sơ
        var oldUnlock = await vault.UnlockWithPinAsync("OldPin#123");
        Assert.False(oldUnlock.Success);

        var newUnlock = await vault.UnlockWithPinAsync("NewPin#456");
        Assert.True(newUnlock.Success);
        Assert.Equal("Hồ sơ Công việc", newUnlock.Profile?.Name);

        var bookmarks = await vault.GetBookmarksAsync();
        Assert.Single(bookmarks);
        Assert.Equal("https://example.com", bookmarks[0].Url);

        // Decoy PIN phải mở sang hồ sơ khách trống
        vault.Lock();
        var decoyUnlock = await vault.UnlockWithPinAsync("9999");
        Assert.True(decoyUnlock.Success);
        Assert.True(decoyUnlock.Profile?.IsDecoy);
        Assert.Empty(await vault.GetBookmarksAsync());
    }

    [Fact]
    public void VietnameseCalendarService_ComputesSolarLunarAndWeatherLabels()
    {
        var sampleDate = new DateTime(2026, 11, 22);
        Assert.Equal("Chủ Nhật, 22/11/2026", VietnameseCalendarService.FormatSolarDate(sampleDate));

        var (lunarDay, lunarMonth, isLeap) = VietnameseCalendarService.GetLunarDate(sampleDate);
        Assert.Equal(14, lunarDay);
        Assert.Equal(10, lunarMonth);
        Assert.False(isLeap);
        Assert.Equal("14/10 Âm lịch", VietnameseCalendarService.FormatLunarDateBadge(sampleDate));
        Assert.Equal("Âm lịch: 14/10 Bính Ngọ", VietnameseCalendarService.FormatHomeLunarText(sampleDate));

        var (condition, icon) = VietnameseCalendarService.MapWmoWeatherCode(1);
        Assert.Equal("Nắng nhẹ", condition);
        Assert.False(string.IsNullOrEmpty(icon));
    }

    [Fact]
    public async Task VaultSecurityService_PersistsSidebarAppToggles_Encrypted()
    {
        using var vault = new VaultSecurityService(_tempRoot);
        await vault.InitializeNewVaultAsync("PinTest#99", decoyPin: null, enableWindowsHello: false);

        var prefs = new VpnPreferences
        {
            ShowLeftSidebar = true,
            SidebarShowZalo = false,
            SidebarShowMessenger = true,
            SidebarShowDiscord = false,
            SidebarShowTelegram = true,
            SidebarShowAiChat = true,
            SidebarShowBookmarks = false,
            SidebarShowHistory = true,
            SidebarShowDownloads = false
        };
        await vault.SaveVpnPreferencesAsync(prefs);

        vault.Lock();
        var unlock = await vault.UnlockWithPinAsync("PinTest#99");
        Assert.True(unlock.Success);

        var loaded = await vault.GetVpnPreferencesAsync();
        Assert.True(loaded.ShowLeftSidebar);
        Assert.False(loaded.SidebarShowZalo);
        Assert.True(loaded.SidebarShowMessenger);
        Assert.False(loaded.SidebarShowDiscord);
        Assert.True(loaded.SidebarShowTelegram);
        Assert.True(loaded.SidebarShowAiChat);
        Assert.False(loaded.SidebarShowBookmarks);
        Assert.True(loaded.SidebarShowHistory);
        Assert.False(loaded.SidebarShowDownloads);
    }

    [Fact]
    public void HomePage_SmartOptimization_ContainsExpectedControls()
    {
        string homeHtmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "home.html");
        if (!File.Exists(homeHtmlPath))
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QUANGLNBrowser.sln")))
            {
                dir = dir.Parent;
            }
            if (dir != null)
            {
                homeHtmlPath = Path.Combine(dir.FullName, "src", "QUANGLNBrowser.App", "Assets", "home.html");
            }
        }

        Assert.True(File.Exists(homeHtmlPath));
        string html = File.ReadAllText(homeHtmlPath);

        Assert.Contains("TỐI ƯU THÔNG MINH &amp; DỌN DẸP", html);
        Assert.Contains("Chế độ Ngủ Tab tự động (Tab Snoozer)", html);
        Assert.Contains("Tăng tốc phần cứng (Hardware Acceleration)", html);
        Assert.Contains("GX Cleaner — Dọn bộ nhớ đệm &amp; dữ liệu rác", html);
        Assert.Contains("Trình duyệt đã sạch", html);
        Assert.Contains("Chạy GX Cleaner", html);
        Assert.Contains("sel-tab-snooze-time", html);
        Assert.Contains("toggle-tab-snoozer", html);
        Assert.Contains("toggle-hardware-accel", html);
        Assert.Contains("btn-run-gx-cleaner", html);
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
            // Bỏ qua nếu file tạm đang được dọn bởi SQLite
        }
    }
}
