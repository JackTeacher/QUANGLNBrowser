using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;

namespace QUANGLNBrowser.Network;

public sealed record WarpTraceInfo(bool IsWarpActive, string PublicIp, string Colo, string RawTrace, string Location = "");

/// <summary>
/// Tự động khởi tạo và đăng ký tài khoản Cloudflare 1.1.1.1 WARP (WireGuard) miễn phí
/// qua API chính thức của Cloudflare, tối ưu Edge Endpoint nhanh nhất và kiểm tra trạng thái đường hầm (warp=on).
/// </summary>
public static class CloudflareWarpProvisioner
{
    public const string DefaultWarpNodeId = "cloudflare_warp_1111";
    private const string CloudflareRegApiUrl = "https://api.cloudflareclient.com/v0a2158/reg";
    private const string CloudflareTraceUrl = "https://1.1.1.1/cdn-cgi/trace";
    private const string DefaultWarpServerIp = "162.159.192.1";
    private const int DefaultWarpServerPort = 2408;

    public static readonly string[] OfficialWarpEndpoints =
    [
        "162.159.192.1",
        "162.159.193.1",
        "162.159.195.1",
        "188.114.96.1",
        "188.114.97.1"
    ];

    private static readonly HttpClient DirectHttpClient = CreateDirectHttpClient();

    private static HttpClient CreateDirectHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "okhttp/3.12.1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("CF-Client-Version", "a-6.10-2158");
        return client;
    }

    public static bool IsValidProvisionedWarpNode(VpnNode? node)
    {
        if (node is null || !string.Equals(node.Protocol, "wireguard", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(node.ConfigJson) ||
            node.ConfigJson.Contains("AAAAAAAA", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(node.ConfigJson) is JsonObject obj)
            {
                string? priv = obj["private_key"]?.ToString();
                string? peer = obj["peer_public_key"]?.ToString();
                return !string.IsNullOrWhiteSpace(priv) && !string.IsNullOrWhiteSpace(peer);
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static async Task<VpnNode> ProvisionWarpNodeAsync(CancellationToken cancellationToken = default)
    {
        var (privateKey, publicKey) = await GenerateWireGuardKeypairAsync(cancellationToken);

        var reqBody = new JsonObject
        {
            ["key"] = publicKey,
            ["install_id"] = string.Empty,
            ["fcm_token"] = string.Empty,
            ["tos"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["model"] = "PC",
            ["serial_number"] = Guid.NewGuid().ToString("N"),
            ["locale"] = "en_US"
        };

        using var content = new StringContent(reqBody.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await DirectHttpClient.PostAsync(CloudflareRegApiUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        string respJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(respJson);
        var root = doc.RootElement;
        var configEl = root.GetProperty("config");

        var addressesEl = configEl.GetProperty("interface").GetProperty("addresses");
        string v4 = addressesEl.GetProperty("v4").GetString() ?? "172.16.0.2";
        string v6 = addressesEl.TryGetProperty("v6", out var v6Prop) ? (v6Prop.GetString() ?? string.Empty) : string.Empty;

        if (!v4.Contains('/')) v4 += "/32";
        if (!string.IsNullOrEmpty(v6) && !v6.Contains('/')) v6 += "/128";

        var peer0 = configEl.GetProperty("peers")[0];
        string peerPublicKey = peer0.GetProperty("public_key").GetString()
            ?? "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=";

        string clientId = configEl.TryGetProperty("client_id", out var cidProp)
            ? (cidProp.GetString() ?? string.Empty)
            : string.Empty;

        var localAddresses = new JsonArray(v4);
        if (!string.IsNullOrEmpty(v6))
        {
            localAddresses.Add(v6);
        }

        var warpConfig = new JsonObject
        {
            ["type"] = "wireguard",
            ["server"] = DefaultWarpServerIp,
            ["server_port"] = DefaultWarpServerPort,
            ["local_address"] = localAddresses,
            ["private_key"] = privateKey,
            ["peer_public_key"] = peerPublicKey,
            ["mtu"] = 1280
        };

        if (!string.IsNullOrEmpty(clientId))
        {
            warpConfig["reserved"] = clientId;
        }

        AppLogger.Info($"Provisioned Cloudflare 1.1.1.1 WARP WireGuard profile (v4={v4})");

        return new VpnNode
        {
            Id = DefaultWarpNodeId,
            Name = "1.1.1.1 with WARP (Cloudflare)",
            CountryCode = "CF",
            Protocol = "wireguard",
            ConfigJson = warpConfig.ToJsonString(),
            LatencyMs = 15,
            IsDefault = true
        };
    }

    public static async Task<(string PrivateKey, string PublicKey)> GenerateWireGuardKeypairAsync(CancellationToken ct = default)
    {
        string singBoxPath = AppPaths.GetNativeExePath("sing-box.exe");
        if (!File.Exists(singBoxPath))
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                string devCandidate = Path.Combine(dir.FullName, "native", "win-x64", "sing-box.exe");
                if (File.Exists(devCandidate))
                {
                    singBoxPath = devCandidate;
                    break;
                }
            }
        }

        if (!File.Exists(singBoxPath))
        {
            throw new FileNotFoundException("Không tìm thấy native\\sing-box.exe để sinh khóa WireGuard.", singBoxPath);
        }

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = singBoxPath,
            Arguments = "generate wg-keypair",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Không thể chạy sing-box generate wg-keypair.");

        string output = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        string? priv = null;
        string? pub = null;
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("PrivateKey:", StringComparison.OrdinalIgnoreCase))
            {
                priv = line["PrivateKey:".Length..].Trim();
            }
            else if (line.StartsWith("PublicKey:", StringComparison.OrdinalIgnoreCase))
            {
                pub = line["PublicKey:".Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(priv) || string.IsNullOrEmpty(pub))
        {
            throw new InvalidOperationException("Không đọc được cặp khóa WireGuard từ sing-box.");
        }

        return (priv, pub);
    }

    public static async Task<WarpTraceInfo> VerifyWarpTraceAsync(int localSocksPort = 10809, CancellationToken ct = default)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{localSocksPort}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(7)
            };

            string trace = await client.GetStringAsync(CloudflareTraceUrl, ct);
            string ip = string.Empty;
            string colo = string.Empty;
            string warp = string.Empty;
            string loc = string.Empty;

            foreach (string line in trace.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line[..eq].Trim();
                string v = line[(eq + 1)..].Trim();

                if (k.Equals("ip", StringComparison.OrdinalIgnoreCase)) ip = v;
                else if (k.Equals("colo", StringComparison.OrdinalIgnoreCase)) colo = v;
                else if (k.Equals("warp", StringComparison.OrdinalIgnoreCase)) warp = v;
                else if (k.Equals("loc", StringComparison.OrdinalIgnoreCase)) loc = v;
            }

            bool active = warp.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                          warp.Equals("plus", StringComparison.OrdinalIgnoreCase);

            return new WarpTraceInfo(active, ip, colo, trace, loc);
        }
        catch (Exception ex)
        {
            return new WarpTraceInfo(false, string.Empty, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Quét song song các Cloudflare WARP Anycast Edge IP chính thức để chọn endpoint có độ trễ thấp nhất
    /// (chế độ "Tối ưu" giống Opera VPN).
    /// </summary>
    public static async Task<(string BestEndpoint, int LatencyMs, bool Updated)> OptimizeWarpEndpointAsync(
        VpnNode warpNode,
        int timeoutMs = 1500,
        CancellationToken ct = default)
    {
        var probeTasks = OfficialWarpEndpoints.Select(async ip =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                await tcp.ConnectAsync(ip, 443, cts.Token);
                sw.Stop();
                return (Endpoint: ip, Latency: Math.Max(1, (int)sw.ElapsedMilliseconds));
            }
            catch
            {
                return (Endpoint: ip, Latency: int.MaxValue);
            }
        });

        var results = await Task.WhenAll(probeTasks);
        var best = results.OrderBy(r => r.Latency).FirstOrDefault();
        if (string.IsNullOrEmpty(best.Endpoint) || best.Latency == int.MaxValue)
        {
            return (DefaultWarpServerIp, 0, false);
        }

        try
        {
            if (JsonNode.Parse(warpNode.ConfigJson) is JsonObject obj)
            {
                obj["server"] = best.Endpoint;
                warpNode.ConfigJson = obj.ToJsonString();
                warpNode.LatencyMs = best.Latency;
                return (best.Endpoint, best.Latency, true);
            }
        }
        catch
        {
            // Giữ cấu hình cũ nếu JSON không đọc được
        }

        return (best.Endpoint, best.Latency, false);
    }
}
