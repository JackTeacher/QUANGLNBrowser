using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;
using QUANGLNBrowser.Engine;
using QUANGLNBrowser.Network;
using QUANGLNBrowser.Security;
using Xunit;

namespace QUANGLNBrowser.Tests;

public sealed class VpnNetworkTests : IDisposable
{
    private readonly string _tempRoot;

    public VpnNetworkTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "QUANGLNBrowser_VpnTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void VpnNodeParser_ParsesVless_Shadowsocks_Socks5_Http_AndJson()
    {
        // 1. VLESS URI
        var vless = VpnNodeParser.ParseFromInput(
            "vless://11111111-2222-3333-4444-555555555555@sg1.example.com:443?security=tls&sni=sg1.example.com&type=ws&path=%2Fws#Singapore_VLESS",
            countryCode: "SG");
        Assert.Equal("vless", vless.Protocol);
        Assert.Equal("Singapore_VLESS", vless.Name);
        Assert.Contains("sg1.example.com", vless.ConfigJson);
        Assert.Contains("11111111-2222-3333-4444-555555555555", vless.ConfigJson);

        // 2. Shadowsocks SIP002 URI
        string ssCreds = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:my_secret_pass"));
        var ss = VpnNodeParser.ParseFromInput($"ss://{ssCreds}@jp.example.com:8388#Japan_SS", countryCode: "JP");
        Assert.Equal("shadowsocks", ss.Protocol);
        Assert.Equal("Japan_SS", ss.Name);
        Assert.Contains("aes-256-gcm", ss.ConfigJson);
        Assert.Contains("my_secret_pass", ss.ConfigJson);

        // 3. SOCKS5 URI
        var socks = VpnNodeParser.ParseFromInput("socks5://user1:pass1@103.1.2.3:1080#VN_Socks", countryCode: "VN");
        Assert.Equal("socks", socks.Protocol);
        Assert.Equal(("103.1.2.3", 1080), VpnNodeParser.ExtractEndpoint(socks));

        // 4. HTTP Proxy URI
        var http = VpnNodeParser.ParseFromInput("http://proxy.example.org:8080", customName: "Corp Proxy", countryCode: "US");
        Assert.Equal("http", http.Protocol);
        Assert.Equal("Corp Proxy", http.Name);
    }

    [Fact]
    public async Task SingBoxConfigGenerator_GeneratesValidConfig_WithSplitTunneling()
    {
        var node = VpnNodeParser.ParseFromInput("socks5://127.0.0.1:1080#LocalTest");
        string json = SingBoxConfigGenerator.GenerateRuntimeConfigJson(node, localPort: 10809, enableSplitTunneling: true);

        Assert.Contains("\"listen_port\": 10809", json);
        Assert.Contains("\"tag\": \"vpn-out\"", json);
        Assert.Contains("\"tag\": \"direct-out\"", json);
        Assert.Contains("\".vn\"", json);
        Assert.Contains("\"vnexpress.net\"", json);

        // Nếu có sing-box.exe, chạy "sing-box.exe check -c" để xác nhận cấu hình hợp lệ 100%
        string? singBoxExe = FindSingBoxBinary();
        if (singBoxExe is not null)
        {
            string cfgPath = Path.Combine(_tempRoot, "check_config.json");
            await File.WriteAllTextAsync(cfgPath, json, new UTF8Encoding(false));

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = singBoxExe,
                Arguments = $"check -c \"{cfgPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            });
            Assert.NotNull(proc);
            await proc!.WaitForExitAsync();
            Assert.Equal(0, proc.ExitCode);

            // Kiểm tra luôn cấu hình WireGuard (Cloudflare 1.1.1.1 WARP endpoint schema trên sing-box 1.14)
            var (privKey, pubKey) = await CloudflareWarpProvisioner.GenerateWireGuardKeypairAsync();
            Assert.False(string.IsNullOrWhiteSpace(privKey));
            Assert.False(string.IsNullOrWhiteSpace(pubKey));

            var warpNode = new VpnNode
            {
                Id = CloudflareWarpProvisioner.DefaultWarpNodeId,
                Name = "1.1.1.1 with WARP",
                CountryCode = "CF",
                Protocol = "wireguard",
                ConfigJson = $$"""
                {
                  "type": "wireguard",
                  "server": "162.159.192.1",
                  "server_port": 2408,
                  "local_address": ["172.16.0.2/32"],
                  "private_key": "{{privKey}}",
                  "peer_public_key": "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=",
                  "mtu": 1280
                }
                """
            };

            string warpJson = SingBoxConfigGenerator.GenerateRuntimeConfigJson(warpNode, localPort: 10809, enableSplitTunneling: true);
            Assert.Contains("\"endpoints\"", warpJson);
            Assert.Contains("\"162.159.192.1\"", warpJson);
            Assert.Contains("\"1.1.1.1\"", warpJson);

            string warpCfgPath = Path.Combine(_tempRoot, "check_warp_config.json");
            await File.WriteAllTextAsync(warpCfgPath, warpJson, new UTF8Encoding(false));

            using var warpProc = Process.Start(new ProcessStartInfo
            {
                FileName = singBoxExe,
                Arguments = $"check -c \"{warpCfgPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            });
            Assert.NotNull(warpProc);
            await warpProc!.WaitForExitAsync();
            Assert.Equal(0, warpProc.ExitCode);
        }
    }

    [Fact]
    public void WebView2EnvironmentManager_EnforcesWebRtcProtection_AndKillSwitchBlackhole()
    {
        // Khi bật VPN: định tuyến qua socks5://127.0.0.1:10809 + khóa WebRTC UDP
        string activeArgs = WebView2EnvironmentManager.BuildBrowserArguments(isVpnEnabled: true, killSwitchTripped: false);
        Assert.Contains("socks5://127.0.0.1:10809", activeArgs);
        Assert.Contains("disable_non_proxied_udp", activeArgs);
        Assert.Equal("disable_non_proxied_udp", WebView2EnvironmentManager.GetWebRtcPolicy(isVpnEnabled: true, killSwitchTripped: false));

        // Khi Kill Switch kích hoạt: khóa cứng về Blackhole 127.0.0.1:1 + khóa WebRTC UDP
        string ksArgs = WebView2EnvironmentManager.BuildBrowserArguments(isVpnEnabled: false, killSwitchTripped: true);
        Assert.Contains("socks5://127.0.0.1:1", ksArgs);
        Assert.Contains("disable_non_proxied_udp", ksArgs);
        Assert.Equal("disable_non_proxied_udp", WebView2EnvironmentManager.GetWebRtcPolicy(isVpnEnabled: false, killSwitchTripped: true));

        // Khi tắt VPN bình thường: không inject proxy
        string directArgs = WebView2EnvironmentManager.BuildBrowserArguments(isVpnEnabled: false, killSwitchTripped: false);
        Assert.DoesNotContain("--proxy-server", directArgs);
        Assert.Equal("default", WebView2EnvironmentManager.GetWebRtcPolicy(isVpnEnabled: false, killSwitchTripped: false));
    }

    [Fact]
    public async Task VpnService_StartsSingBox_AndTripsKillSwitch_WhenProcessTerminatedUnexpectedly()
    {
        string? singBoxSource = FindSingBoxBinary();
        if (singBoxSource is null)
        {
            return;
        }

        string targetNativePath = AppPaths.GetNativeExePath("sing-box.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(targetNativePath)!);
        if (!File.Exists(targetNativePath))
        {
            File.Copy(singBoxSource, targetNativePath, overwrite: true);
        }

        int testPort = GetAvailableTcpPort();
        using var jobManager = new JobObjectManager();
        using var vpnService = new VpnService(jobManager, localSocksPort: testPort, runtimeDir: _tempRoot);
        vpnService.IsKillSwitchEnabled = true;

        var testNode = new VpnNode
        {
            Name = "Test Local Tunnel",
            CountryCode = "SG",
            Protocol = "direct",
            ConfigJson = "{\"type\":\"direct\"}"
        };

        var killSwitchTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vpnService.StateChanged += (_, _) =>
        {
            if (vpnService.IsKillSwitchTripped)
            {
                killSwitchTcs.TrySetResult(true);
            }
        };

        bool started = await vpnService.StartAsync(testNode);
        Assert.True(started);
        Assert.True(vpnService.IsRunning);
        Assert.False(vpnService.IsKillSwitchTripped);

        // Giả lập tắt nóng sing-box.exe để kiểm tra Kill Switch tự động kích hoạt
        foreach (var proc in Process.GetProcessesByName("sing-box"))
        {
            try
            {
                proc.Kill();
            }
            catch
            {
                // Bỏ qua nếu process không thuộc quyền
            }
        }

        bool tripped = await killSwitchTcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(tripped);
        Assert.True(vpnService.IsKillSwitchTripped);
        Assert.False(vpnService.IsRunning);

        vpnService.ResetKillSwitch();
        Assert.False(vpnService.IsKillSwitchTripped);
    }

    [Fact]
    public async Task VaultSecurityService_PersistsVpnNodes_InEncryptedDatabase()
    {
        using var vault = new VaultSecurityService(_tempRoot);
        await vault.InitializeNewVaultAsync("VpnVault#2026", decoyPin: null, enableWindowsHello: false);

        var node = VpnNodeParser.ParseFromInput("socks5://10.20.30.40:1080#Tokyo_Node", countryCode: "JP");
        node.LatencyMs = 45;
        node.IsDefault = true;

        await vault.UpsertVpnNodeAsync(node);
        vault.Lock();

        var unlock = await vault.UnlockWithPinAsync("VpnVault#2026");
        Assert.True(unlock.Success);

        var nodes = await vault.GetVpnNodesAsync();
        Assert.Single(nodes);
        Assert.Equal("Tokyo_Node", nodes[0].Name);
        Assert.Equal("JP", nodes[0].CountryCode);
        Assert.Equal(45, nodes[0].LatencyMs);
        Assert.True(nodes[0].IsDefault);

        await vault.DeleteVpnNodeAsync(nodes[0].Id);
        Assert.Empty(await vault.GetVpnNodesAsync());
    }

    [Fact]
    public void VpnNodeParser_ParsesBase64Subscription_AndClassifiesOperaRegions()
    {
        string ssCreds = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:pass123"));
        string rawList = string.Join('\n',
        [
            "vless://11111111-2222-3333-4444-555555555555@jp1.example.com:443?security=tls#🇯🇵 Tokyo Fast",
            $"ss://{ssCreds}@de1.example.com:8388#DE-Frankfurt-Pro",
            "socks5://user:pw@us1.example.com:1080#US-NewYork",
            "http://sg1.example.com:8080#Singapore_Edge"
        ]);

        string base64Sub = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawList));
        var parsed = VpnNodeParser.ParseSubscriptionContent(base64Sub);

        Assert.Equal(4, parsed.Count);
        Assert.Equal("JP", parsed[0].CountryCode);
        Assert.Equal(VpnRegion.Asia, parsed[0].Region);

        Assert.Equal("DE", parsed[1].CountryCode);
        Assert.Equal(VpnRegion.Europe, parsed[1].Region);

        Assert.Equal("US", parsed[2].CountryCode);
        Assert.Equal(VpnRegion.Americas, parsed[2].Region);

        Assert.Equal("SG", parsed[3].CountryCode);
        Assert.Equal(VpnRegion.Asia, parsed[3].Region);
    }

    [Fact]
    public async Task SingBoxConfigGenerator_SupportsOperaBypass_ClashApi_AndDoubleVpn()
    {
        var exitNode = VpnNodeParser.ParseFromInput("socks5://user1:pass1@1.2.3.4:1080#DE-Frankfurt");
        var warpEntry = new VpnNode
        {
            Id = CloudflareWarpProvisioner.DefaultWarpNodeId,
            Name = "1.1.1.1 with WARP",
            CountryCode = "CF",
            Protocol = "wireguard",
            ConfigJson = """
            {
              "type": "wireguard",
              "server": "162.159.192.1",
              "server_port": 2408,
              "local_address": ["172.16.0.2/32"],
              "private_key": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
              "peer_public_key": "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=",
              "mtu": 1280
            }
            """
        };

        string json = SingBoxConfigGenerator.GenerateRuntimeConfigJson(
            exitNode,
            localPort: 10809,
            enableSplitTunneling: true,
            customBypassDomains: ["https://www.vietcombank.com.vn/login", "netflix.com"],
            bypassSearchEngines: true,
            clashApiPort: 19095,
            doubleVpnEntryNode: warpEntry);

        Assert.Contains("\"external_controller\": \"127.0.0.1:19095\"", json);
        Assert.Contains("\"google.com\"", json);
        Assert.Contains("\"bing.com\"", json);
        Assert.Contains("\"vietcombank.com.vn\"", json);
        Assert.Contains("\"netflix.com\"", json);
        Assert.Contains("\"tag\": \"warp-hop\"", json);
        Assert.Contains("\"detour\": \"warp-hop\"", json);

        string? singBoxExe = FindSingBoxBinary();
        if (singBoxExe is not null)
        {
            string cfgPath = Path.Combine(_tempRoot, "check_opera_doublevpn.json");
            await File.WriteAllTextAsync(cfgPath, json, new UTF8Encoding(false));

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = singBoxExe,
                Arguments = $"check -c \"{cfgPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            });
            Assert.NotNull(proc);
            string err = await proc!.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            Assert.True(proc.ExitCode == 0, $"sing-box check failed: {err}");
        }
    }

    [Fact]
    public void SingBoxConfigGenerator_IncludesSearchEngines_AndCustomBypassDomains()
    {
        var bypass = SingBoxConfigGenerator.DefaultDomesticBypassDomains;
        Assert.Contains(".vn", bypass);
        var searchEngines = SingBoxConfigGenerator.DefaultSearchEngineBypassDomains;
        Assert.Contains("google.com", searchEngines);
        Assert.Contains("bing.com", searchEngines);
    }

    [Fact]
    public async Task VpnService_ReportsLiveClashApiTelemetry_WhenRunning()
    {
        string? singBoxSource = FindSingBoxBinary();
        if (singBoxSource is null)
        {
            return;
        }

        string targetNativePath = AppPaths.GetNativeExePath("sing-box.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(targetNativePath)!);
        if (!File.Exists(targetNativePath))
        {
            File.Copy(singBoxSource, targetNativePath, overwrite: true);
        }

        int testPort = GetAvailableTcpPort();
        int testClashPort = GetAvailableTcpPort();
        using var jobManager = new JobObjectManager();
        using var vpnService = new VpnService(jobManager, localSocksPort: testPort, runtimeDir: _tempRoot, clashApiPort: testClashPort);

        var telemetryTcs = new TaskCompletionSource<VpnTelemetry>(TaskCreationOptions.RunContinuationsAsynchronously);
        vpnService.TelemetryUpdated += (_, telemetry) => telemetryTcs.TrySetResult(telemetry);

        var testNode = new VpnNode
        {
            Name = "Telemetry Local Tunnel",
            CountryCode = "SG",
            Protocol = "direct",
            ConfigJson = "{\"type\":\"direct\"}"
        };

        bool started = await vpnService.StartAsync(testNode);
        Assert.True(started);

        var sample = await telemetryTcs.Task.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.NotNull(sample.ConnectedSinceUtc);
        Assert.True(sample.MemoryBytes > 0);

        await vpnService.StopAsync();
        Assert.False(vpnService.IsRunning);
    }

    [Fact]
    public async Task VaultSecurityService_PersistsOperaVpnPreferences_Encrypted()
    {
        using var vault = new VaultSecurityService(_tempRoot);
        await vault.InitializeNewVaultAsync("OperaVpn#2026", decoyPin: null, enableWindowsHello: false);

        var prefs = new VpnPreferences
        {
            SelectedRegion = VpnRegion.Europe,
            BypassSearchEngines = true,
            AutoVpnInIncognito = true,
            EnableKillSwitch = true,
            EnableSplitTunneling = false,
            EnableDoubleVpn = true,
            LastSelectedNodeId = "node_de_1",
            CustomBypassDomains = ["vietcombank.com.vn", "netflix.com"]
        };

        await vault.SaveVpnPreferencesAsync(prefs);
        vault.Lock();

        var unlock = await vault.UnlockWithPinAsync("OperaVpn#2026");
        Assert.True(unlock.Success);

        var loaded = await vault.GetVpnPreferencesAsync();
        Assert.Equal(VpnRegion.Europe, loaded.SelectedRegion);
        Assert.True(loaded.BypassSearchEngines);
        Assert.True(loaded.AutoVpnInIncognito);
        Assert.True(loaded.EnableKillSwitch);
        Assert.False(loaded.EnableSplitTunneling);
        Assert.True(loaded.EnableDoubleVpn);
        Assert.Equal("node_de_1", loaded.LastSelectedNodeId);
        Assert.Equal(2, loaded.CustomBypassDomains.Count);
        Assert.Contains("vietcombank.com.vn", loaded.CustomBypassDomains);
        Assert.Contains("netflix.com", loaded.CustomBypassDomains);
        Assert.Equal("coccoc", loaded.ThemeKey);
    }

    [Fact]
    public void BrowserThemeCatalog_ResolvesAndCyclesAllNeonPresets()
    {
        Assert.Equal(4, BrowserThemeCatalog.Presets.Count);

        var coccoc = BrowserThemeCatalog.Resolve(null);
        var crimson = BrowserThemeCatalog.Resolve("CRIMSON");
        var cyan = BrowserThemeCatalog.Resolve("cyan");
        var purple = BrowserThemeCatalog.Resolve("purple");
        var fallback = BrowserThemeCatalog.Resolve("unknown_theme");

        Assert.Equal("coccoc", coccoc.Key);
        Assert.Equal("#00E676", coccoc.AccentHex);
        Assert.Equal("crimson", crimson.Key);
        Assert.Equal("#FA1E4E", crimson.AccentHex);
        Assert.Equal("cyan", cyan.Key);
        Assert.Equal("#00E5FF", cyan.AccentHex);
        Assert.Equal("purple", purple.Key);
        Assert.Equal("#B517FF", purple.AccentHex);
        Assert.Equal("coccoc", fallback.Key);

        Assert.Equal("crimson", BrowserThemeCatalog.GetNext("coccoc").Key);
        Assert.Equal("cyan", BrowserThemeCatalog.GetNext("crimson").Key);
        Assert.Equal("purple", BrowserThemeCatalog.GetNext("cyan").Key);
        Assert.Equal("coccoc", BrowserThemeCatalog.GetNext("purple").Key);
    }

    [Fact]
    public async Task VaultSecurityService_PersistsThemeKey_Encrypted()
    {
        using var vault = new VaultSecurityService(_tempRoot);
        await vault.InitializeNewVaultAsync("ThemeVault#2026", decoyPin: null, enableWindowsHello: false);

        var prefs = new VpnPreferences
        {
            ThemeKey = "crimson",
            SelectedRegion = VpnRegion.Asia
        };

        await vault.SaveVpnPreferencesAsync(prefs);
        vault.Lock();

        var unlock = await vault.UnlockWithPinAsync("ThemeVault#2026");
        Assert.True(unlock.Success);

        var loaded = await vault.GetVpnPreferencesAsync();
        Assert.Equal("crimson", loaded.ThemeKey);
    }

    private static string? FindSingBoxBinary()
    {
        string candidate1 = AppPaths.GetNativeExePath("sing-box.exe");
        if (File.Exists(candidate1)) return candidate1;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "native", "win-x64", "sing-box.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static int GetAvailableTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
