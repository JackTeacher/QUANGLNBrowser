using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using QUANGLNBrowser.Core.Models;

namespace QUANGLNBrowser.Network;

/// <summary>
/// Phân tích chuỗi cấu hình VPN Node (URI vless://, ss://, socks5://, http://, wireguard JSON,
/// Base64 Subscription nhiều dòng), tự nhận diện quốc gia/vùng Opera và đo độ trễ thực tế (TCP Ping).
/// </summary>
public static class VpnNodeParser
{
    public static VpnNode ParseFromInput(string rawInput, string? customName = null, string? countryCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawInput);
        string input = rawInput.Trim();

        VpnNode parsed;
        if (input.StartsWith('{'))
        {
            parsed = ParseFromRawJson(input, customName, countryCode);
        }
        else if (input.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            parsed = ParseVlessUri(input, customName, countryCode);
        }
        else if (input.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
        {
            parsed = ParseShadowsocksUri(input, customName, countryCode);
        }
        else if (input.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) ||
                 input.StartsWith("socks://", StringComparison.OrdinalIgnoreCase) ||
                 input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            parsed = ParseSocksOrHttpUri(input, customName, countryCode);
        }
        else
        {
            // Hỗ trợ nhập nhanh dạng "host:port" (mặc định SOCKS5)
            int colonIdx = input.LastIndexOf(':');
            if (colonIdx > 0 && int.TryParse(input[(colonIdx + 1)..], out int port))
            {
                string host = input[..colonIdx].Trim();
                var cfg = new JsonObject
                {
                    ["server"] = host,
                    ["server_port"] = port
                };
                string name = string.IsNullOrWhiteSpace(customName) ? $"SOCKS5 {host}:{port}" : customName;
                parsed = new VpnNode
                {
                    Name = name,
                    CountryCode = ResolveCountryCode(countryCode, name, host),
                    Protocol = "socks",
                    ConfigJson = cfg.ToJsonString()
                };
            }
            else
            {
                throw new FormatException("Định dạng VPN Node không hợp lệ. Hỗ trợ: vless://, ss://, socks5://, http://, host:port, JSON hoặc Base64 Subscription.");
            }
        }

        return parsed;
    }

    /// <summary>
    /// Phân tích hàng loạt danh sách VPN Node từ nhiều dòng link (vless://, ss://, socks5://, http://)
    /// hoặc chuỗi Subscription mã hóa Base64 chuẩn V2Ray/sing-box.
    /// </summary>
    public static IReadOnlyList<VpnNode> ParseSubscriptionContent(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return Array.Empty<VpnNode>();
        }

        string trimmed = rawContent.Trim();

        // Nếu toàn bộ là JSON array của các outbound/node
        if (trimmed.StartsWith('['))
        {
            try
            {
                if (JsonNode.Parse(trimmed) is JsonArray arr)
                {
                    var fromJson = new List<VpnNode>();
                    foreach (var item in arr)
                    {
                        if (item is JsonObject obj)
                        {
                            try
                            {
                                fromJson.Add(ParseFromRawJson(obj.ToJsonString(), obj["tag"]?.ToString(), null));
                            }
                            catch
                            {
                                // Bỏ qua phần tử lỗi
                            }
                        }
                    }
                    return fromJson;
                }
            }
            catch
            {
                // Thử tiếp theo dòng
            }
        }

        // Nếu là chuỗi đơn không chứa "://" và không phải JSON/host:port -> thử giải mã Base64 subscription
        string decodedText = trimmed;
        if (!trimmed.Contains("://", StringComparison.Ordinal) &&
            !trimmed.StartsWith('{') &&
            !trimmed.Contains(':'))
        {
            try
            {
                string candidate = DecodeBase64Safe(trimmed);
                if (candidate.Contains("://", StringComparison.Ordinal))
                {
                    decodedText = candidate;
                }
            }
            catch
            {
                // Giữ nguyên văn bản gốc
            }
        }

        var nodes = new List<VpnNode>();
        string[] lines = decodedText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                nodes.Add(ParseFromInput(line, customName: null, countryCode: null));
            }
            catch
            {
                // Bỏ qua dòng không hợp lệ trong subscription
            }
        }

        return nodes;
    }

    public static string NormalizeBypassDomain(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return string.Empty;
        }

        string s = rawInput.Trim().ToLowerInvariant();
        if (s.StartsWith("http://", StringComparison.Ordinal) || s.StartsWith("https://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                s = uri.Host.ToLowerInvariant();
            }
        }
        else
        {
            int slashIdx = s.IndexOf('/');
            if (slashIdx > 0)
            {
                s = s[..slashIdx];
            }
            int colonIdx = s.IndexOf(':');
            if (colonIdx > 0)
            {
                s = s[..colonIdx];
            }
        }

        if (s.StartsWith("www.", StringComparison.Ordinal))
        {
            s = s[4..];
        }

        s = s.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(s) || s.Contains(' '))
        {
            return string.Empty;
        }

        return s;
    }

    private static string ResolveCountryCode(string? explicitCountry, string? nameHint, string? hostHint)
    {
        if (!string.IsNullOrWhiteSpace(explicitCountry))
        {
            return explicitCountry.Trim().ToUpperInvariant();
        }

        return VpnRegionHelper.TryDetectCountryCodeFromText(nameHint)
            ?? VpnRegionHelper.TryDetectCountryCodeFromText(hostHint)
            ?? "SG";
    }

    private static VpnNode ParseFromRawJson(string json, string? customName, string? countryCode)
    {
        var obj = JsonNode.Parse(json) as JsonObject
            ?? throw new FormatException("JSON cấu hình VPN Node không hợp lệ.");

        string protocol = obj["type"]?.ToString()?.ToLowerInvariant() ?? "socks";
        string server = obj["server"]?.ToString() ?? "127.0.0.1";
        int port = int.TryParse(obj["server_port"]?.ToString(), out int p) ? p : 1080;
        string name = !string.IsNullOrWhiteSpace(customName) ? customName : $"{protocol.ToUpperInvariant()} {server}:{port}";

        return new VpnNode
        {
            Name = name,
            CountryCode = ResolveCountryCode(countryCode, name, server),
            Protocol = protocol,
            ConfigJson = obj.ToJsonString()
        };
    }

    private static VpnNode ParseVlessUri(string uriText, string? customName, string? countryCode)
    {
        var uri = new Uri(uriText);
        string uuid = Uri.UnescapeDataString(uri.UserInfo);
        string host = uri.Host;
        int port = uri.Port > 0 ? uri.Port : 443;
        string fragmentName = !string.IsNullOrWhiteSpace(uri.Fragment)
            ? Uri.UnescapeDataString(uri.Fragment.TrimStart('#'))
            : string.Empty;

        var query = HttpUtility.ParseQueryString(uri.Query);
        string security = query["security"] ?? string.Empty;
        string sni = query["sni"] ?? query["host"] ?? host;
        string flow = query["flow"] ?? string.Empty;
        string type = query["type"] ?? "tcp";
        string path = query["path"] ?? "/";

        var cfg = new JsonObject
        {
            ["type"] = "vless",
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = uuid
        };

        if (!string.IsNullOrWhiteSpace(flow))
        {
            cfg["flow"] = flow;
        }

        if (security.Equals("tls", StringComparison.OrdinalIgnoreCase) ||
            security.Equals("reality", StringComparison.OrdinalIgnoreCase))
        {
            cfg["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = sni
            };
        }

        if (type.Equals("ws", StringComparison.OrdinalIgnoreCase))
        {
            cfg["transport"] = new JsonObject
            {
                ["type"] = "ws",
                ["path"] = path
            };
        }

        string name = !string.IsNullOrWhiteSpace(customName)
            ? customName
            : (!string.IsNullOrWhiteSpace(fragmentName) ? fragmentName : $"VLESS {host}:{port}");

        return new VpnNode
        {
            Name = name,
            CountryCode = ResolveCountryCode(countryCode, name, host),
            Protocol = "vless",
            ConfigJson = cfg.ToJsonString()
        };
    }

    private static VpnNode ParseShadowsocksUri(string uriText, string? customName, string? countryCode)
    {
        // Định dạng SIP002: ss://BASE64(method:password)@host:port#Name
        string withoutScheme = uriText[5..];
        string fragmentName = string.Empty;
        int hashIdx = withoutScheme.IndexOf('#');
        if (hashIdx >= 0)
        {
            fragmentName = Uri.UnescapeDataString(withoutScheme[(hashIdx + 1)..]);
            withoutScheme = withoutScheme[..hashIdx];
        }

        string userInfoPart;
        string hostPortPart;

        int atIdx = withoutScheme.LastIndexOf('@');
        if (atIdx > 0)
        {
            userInfoPart = withoutScheme[..atIdx];
            hostPortPart = withoutScheme[(atIdx + 1)..];
        }
        else
        {
            string decodedAll = DecodeBase64Safe(withoutScheme);
            int decodedAt = decodedAll.LastIndexOf('@');
            if (decodedAt <= 0)
            {
                throw new FormatException("Link Shadowsocks (ss://) không đúng định dạng.");
            }
            userInfoPart = decodedAll[..decodedAt];
            hostPortPart = decodedAll[(decodedAt + 1)..];
        }

        string methodAndPass = userInfoPart.Contains(':') ? userInfoPart : DecodeBase64Safe(userInfoPart);
        int colonMethod = methodAndPass.IndexOf(':');
        if (colonMethod <= 0)
        {
            throw new FormatException("Không tìm thấy method:password trong link Shadowsocks.");
        }

        string method = methodAndPass[..colonMethod];
        string password = methodAndPass[(colonMethod + 1)..];

        int qIdx = hostPortPart.IndexOf('?');
        if (qIdx >= 0)
        {
            hostPortPart = hostPortPart[..qIdx];
        }
        hostPortPart = hostPortPart.TrimEnd('/');

        int colonPort = hostPortPart.LastIndexOf(':');
        if (colonPort <= 0 || !int.TryParse(hostPortPart[(colonPort + 1)..], out int port))
        {
            throw new FormatException("Không tìm thấy host:port hợp lệ trong link Shadowsocks.");
        }
        string host = hostPortPart[..colonPort];

        var cfg = new JsonObject
        {
            ["type"] = "shadowsocks",
            ["server"] = host,
            ["server_port"] = port,
            ["method"] = method,
            ["password"] = password
        };

        string name = !string.IsNullOrWhiteSpace(customName)
            ? customName
            : (!string.IsNullOrWhiteSpace(fragmentName) ? fragmentName : $"SS {host}:{port}");

        return new VpnNode
        {
            Name = name,
            CountryCode = ResolveCountryCode(countryCode, name, host),
            Protocol = "shadowsocks",
            ConfigJson = cfg.ToJsonString()
        };
    }

    private static VpnNode ParseSocksOrHttpUri(string uriText, string? customName, string? countryCode)
    {
        var uri = new Uri(uriText);
        bool isHttp = uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        string protocol = isHttp ? "http" : "socks";
        string host = uri.Host;
        int port = uri.Port > 0 ? uri.Port : (isHttp ? 8080 : 1080);

        string fragmentName = !string.IsNullOrWhiteSpace(uri.Fragment)
            ? Uri.UnescapeDataString(uri.Fragment.TrimStart('#'))
            : string.Empty;

        var cfg = new JsonObject
        {
            ["type"] = protocol,
            ["server"] = host,
            ["server_port"] = port
        };

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            string[] parts = uri.UserInfo.Split(':', 2);
            cfg["username"] = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1)
            {
                cfg["password"] = Uri.UnescapeDataString(parts[1]);
            }
        }

        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            cfg["tls"] = new JsonObject { ["enabled"] = true };
        }

        string name = !string.IsNullOrWhiteSpace(customName)
            ? customName
            : (!string.IsNullOrWhiteSpace(fragmentName) ? fragmentName : $"{protocol.ToUpperInvariant()} {host}:{port}");

        return new VpnNode
        {
            Name = name,
            CountryCode = ResolveCountryCode(countryCode, name, host),
            Protocol = protocol,
            ConfigJson = cfg.ToJsonString()
        };
    }

    public static (string Host, int Port) ExtractEndpoint(VpnNode node)
    {
        try
        {
            if (JsonNode.Parse(node.ConfigJson) is JsonObject obj)
            {
                string host = obj["server"]?.ToString() ?? "127.0.0.1";
                int port = int.TryParse(obj["server_port"]?.ToString(), out int p) ? p : 1080;
                return (host, port);
            }
        }
        catch
        {
            // Fallback
        }
        return ("127.0.0.1", 1080);
    }

    public static async Task<int> MeasureTcpLatencyAsync(VpnNode node, int timeoutMs = 2500, CancellationToken ct = default)
    {
        var (host, port) = ExtractEndpoint(node);
        if (string.IsNullOrWhiteSpace(host) || port <= 0)
        {
            return 0;
        }

        // WireGuard (như Cloudflare 1.1.1.1 WARP cổng 2408) chạy trên UDP, nên đo độ trễ TCP qua cổng 443 của cùng Edge IP
        int probePort = string.Equals(node.Protocol, "wireguard", StringComparison.OrdinalIgnoreCase) && port == 2408
            ? 443
            : port;

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            await client.ConnectAsync(host, probePort, timeoutCts.Token);
            sw.Stop();
            return Math.Max(1, (int)sw.ElapsedMilliseconds);
        }
        catch
        {
            return 0;
        }
    }

    private static string DecodeBase64Safe(string raw)
    {
        string padded = raw.Trim().Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        byte[] bytes = Convert.FromBase64String(padded);
        return Encoding.UTF8.GetString(bytes);
    }
}
