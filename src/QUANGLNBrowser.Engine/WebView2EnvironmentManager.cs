using System.Collections.Concurrent;
using System.IO;
using Microsoft.Web.WebView2.Core;
using QUANGLNBrowser.Core.Services;

namespace QUANGLNBrowser.Engine;

/// <summary>
/// Quản lý CoreWebView2Environment tập trung cho QUANGLN Browser:
/// - Thiết lập tham số Chromium (tăng tốc GPU, bypass autoplay restriction, HEVC/H.264/AV1 hardware decoding).
/// - Hỗ trợ Per-Tab VPN và Kill Switch thông qua các Environment cô lập với cờ proxy tương ứng.
/// - Lưu trữ dữ liệu hồ sơ vào %AppData%\QUANGLNBrowser\profiles\{profileId}\wv2_*.
/// </summary>
public static class WebView2EnvironmentManager
{
    private static readonly ConcurrentDictionary<string, Task<CoreWebView2Environment>> EnvironmentCache = new();

    public static async Task<CoreWebView2Environment> GetEnvironmentAsync(
        string profileId = "primary",
        bool isVpnEnabled = false,
        bool killSwitchTripped = false)
    {
        string key = $"{profileId}_vpn:{isVpnEnabled}_kill:{killSwitchTripped}";
        return await EnvironmentCache.GetOrAdd(key, _ => CreateEnvironmentInternalAsync(profileId, isVpnEnabled, killSwitchTripped));
    }

    public static Task<CoreWebView2Environment> GetStandardEnvironmentAsync() =>
        GetEnvironmentAsync("primary", isVpnEnabled: false, killSwitchTripped: false);

    public static string BuildBrowserArguments(bool isVpnEnabled, bool killSwitchTripped)
    {
        string browserArgs = "--autoplay-policy=no-user-gesture-required " +
                             "--enable-gpu-rasterization " +
                             "--enable-zero-copy " +
                             "--ignore-gpu-blocklist " +
                             "--enable-accelerated-video-decode " +
                             "--enable-accelerated-video " +
                             "--allow-running-insecure-content " +
                             "--enable-features=PlatformHEVCDecoderSupport,VaapiVideoDecoder,HardwareMediaKeyHandling,MediaSourceExtensions,AutoplayIgnoreWebAudio,EncryptedMediaExtensions";

        if (killSwitchTripped)
        {
            browserArgs += " --proxy-server=\"socks5://127.0.0.1:1\" --webrtc-ip-handling-policy=disable_non_proxied_udp";
        }
        else if (isVpnEnabled)
        {
            browserArgs += " --proxy-server=\"socks5://127.0.0.1:10809\" --webrtc-ip-handling-policy=disable_non_proxied_udp";
        }

        return browserArgs;
    }

    public static string GetWebRtcPolicy(bool isVpnEnabled, bool killSwitchTripped) =>
        (isVpnEnabled || killSwitchTripped) ? "disable_non_proxied_udp" : "default";

    private static async Task<CoreWebView2Environment> CreateEnvironmentInternalAsync(
        string profileId,
        bool isVpnEnabled,
        bool killSwitchTripped)
    {
        string subDir = killSwitchTripped ? "wv2_killswitch" : (isVpnEnabled ? "wv2_vpn" : "wv2_data");
        string userDataFolder = Path.Combine(AppPaths.GetProfileDirectory(profileId), subDir);
        Directory.CreateDirectory(userDataFolder);

        string browserArgs = BuildBrowserArguments(isVpnEnabled, killSwitchTripped);

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = browserArgs,
            Language = "vi",
            AreBrowserExtensionsEnabled = true
        };

        AppLogger.Info($"Khởi tạo CoreWebView2Environment [Profile: {profileId}, SubDir: {subDir}]");
        return await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
    }
}
