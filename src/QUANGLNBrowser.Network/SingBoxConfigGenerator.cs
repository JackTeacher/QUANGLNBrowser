using System.Text.Json;
using System.Text.Json.Nodes;
using QUANGLNBrowser.Core.Models;

namespace QUANGLNBrowser.Network;

/// <summary>
/// Sinh cấu hình runtime JSON chuẩn cho sing-box 1.14 (hỗ trợ WireGuard Endpoint 1.1.1.1 WARP,
/// Double VPN Multi-Hop, Clash API Telemetry thời gian thực, VLESS, Shadowsocks, SOCKS5, HTTP,
/// DNS 1.1.1.1 chống chặn ISP, Split Tunneling .vn, Bypass Search Engine &amp; Per-Site Bypass kiểu Opera VPN).
/// </summary>
public static class SingBoxConfigGenerator
{
    public static readonly string[] DefaultDomesticBypassDomains =
    [
        ".vn",
        "vnexpress.net",
        "dantri.com.vn",
        "tuoitre.vn",
        "thanhnien.vn",
        "vietnamnet.vn",
        "zalo.me",
        "zaloapp.com",
        "vtv.vn",
        "vov.vn"
    ];

    /// <summary>
    /// Danh sách công cụ tìm kiếm được bỏ qua VPN khi bật chế độ Opera "Bypass VPN for default search engines"
    /// giúp tránh CAPTCHA và giữ kết quả tìm kiếm chính xác theo khu vực.
    /// </summary>
    public static readonly string[] DefaultSearchEngineBypassDomains =
    [
        "google.com",
        "google.com.vn",
        "bing.com",
        "duckduckgo.com",
        "coccoc.com",
        "search.yahoo.com"
    ];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public static string GenerateRuntimeConfigJson(
        VpnNode node,
        int localPort = 10809,
        bool enableSplitTunneling = true,
        IEnumerable<string>? customBypassDomains = null,
        bool bypassSearchEngines = false,
        int clashApiPort = 0,
        VpnNode? doubleVpnEntryNode = null)
    {
        string protocol = (node.Protocol ?? "socks").Trim().ToLowerInvariant();
        bool isWireGuard = protocol == "wireguard";
        bool useDoubleVpn = !isWireGuard
            && protocol != "direct"
            && doubleVpnEntryNode is not null
            && string.Equals(doubleVpnEntryNode.Protocol, "wireguard", StringComparison.OrdinalIgnoreCase);

        var domainSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (enableSplitTunneling)
        {
            foreach (string d in DefaultDomesticBypassDomains)
            {
                domainSet.Add(d);
            }
        }

        if (bypassSearchEngines)
        {
            foreach (string d in DefaultSearchEngineBypassDomains)
            {
                domainSet.Add(d);
            }
        }

        if (customBypassDomains is not null)
        {
            foreach (string d in customBypassDomains)
            {
                string normalized = VpnNodeParser.NormalizeBypassDomain(d);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    domainSet.Add(normalized);
                }
            }
        }

        var rulesArray = new JsonArray
        {
            new JsonObject
            {
                ["ip_is_private"] = true,
                ["outbound"] = "direct-out"
            }
        };

        if (domainSet.Count > 0)
        {
            var suffixes = new JsonArray();
            foreach (string domain in domainSet)
            {
                suffixes.Add(domain);
            }

            rulesArray.Add(new JsonObject
            {
                ["domain_suffix"] = suffixes,
                ["outbound"] = "direct-out"
            });
        }

        // Bắt buộc phân giải tên miền sang IPv4 trước khi đẩy gói tin vào đường hầm
        rulesArray.Add(new JsonObject
        {
            ["action"] = "resolve",
            ["strategy"] = "ipv4_only"
        });

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "warn",
                ["timestamp"] = true
            }
        };

        if (clashApiPort > 0)
        {
            root["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{clashApiPort}"
                }
            };
        }

        var dnsServers = new JsonArray();
        if (isWireGuard)
        {
            dnsServers.Add(new JsonObject
            {
                ["type"] = "udp",
                ["tag"] = "dns-cf",
                ["server"] = "1.1.1.1",
                ["detour"] = "vpn-out"
            });
        }

        dnsServers.Add(new JsonObject
        {
            ["type"] = "udp",
            ["tag"] = "dns-public",
            ["server"] = "1.1.1.1"
        });

        root["dns"] = new JsonObject
        {
            ["servers"] = dnsServers,
            ["final"] = isWireGuard ? "dns-cf" : "dns-public",
            ["strategy"] = "ipv4_only"
        };

        root["inbounds"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "local-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = localPort
            }
        };

        if (isWireGuard)
        {
            root["endpoints"] = new JsonArray
            {
                BuildWireGuardEndpointObject(node, "vpn-out")
            };
            root["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "direct",
                    ["tag"] = "direct-out"
                }
            };
        }
        else if (useDoubleVpn && doubleVpnEntryNode is not null)
        {
            // Double VPN (2 lớp mã hóa): Trình duyệt -> Cloudflare 1.1.1.1 WARP (warp-hop) -> Máy chủ khu vực (vpn-out)
            root["endpoints"] = new JsonArray
            {
                BuildWireGuardEndpointObject(doubleVpnEntryNode, "warp-hop")
            };

            var chainedOutbound = BuildVpnOutboundObject(node);
            chainedOutbound["detour"] = "warp-hop";

            root["outbounds"] = new JsonArray
            {
                chainedOutbound,
                new JsonObject
                {
                    ["type"] = "direct",
                    ["tag"] = "direct-out"
                }
            };
        }
        else
        {
            root["outbounds"] = new JsonArray
            {
                BuildVpnOutboundObject(node),
                new JsonObject
                {
                    ["type"] = "direct",
                    ["tag"] = "direct-out"
                }
            };
        }

        root["route"] = new JsonObject
        {
            ["default_domain_resolver"] = isWireGuard ? "dns-cf" : "dns-public",
            ["rules"] = rulesArray,
            ["final"] = "vpn-out"
        };

        return root.ToJsonString(WriteOptions);
    }

    private static JsonObject BuildWireGuardEndpointObject(VpnNode node, string tag)
    {
        JsonObject? parsed = null;
        if (!string.IsNullOrWhiteSpace(node.ConfigJson))
        {
            try
            {
                parsed = JsonNode.Parse(node.ConfigJson) as JsonObject;
            }
            catch
            {
                parsed = null;
            }
        }
        parsed ??= new JsonObject();

        string server = GetStringOrDefault(parsed, "server", "162.159.192.1");
        int serverPort = GetIntOrDefault(parsed, "server_port", 2408);
        string privateKey = GetStringOrDefault(parsed, "private_key", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        string peerPublicKey = GetStringOrDefault(parsed, "peer_public_key", "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=");
        int mtu = GetIntOrDefault(parsed, "mtu", 1280);

        JsonArray addressArray;
        if (parsed["local_address"] is JsonArray localArr && localArr.Count > 0)
        {
            addressArray = (JsonArray)localArr.DeepClone();
        }
        else if (parsed["address"] is JsonArray addrArr && addrArr.Count > 0)
        {
            addressArray = (JsonArray)addrArr.DeepClone();
        }
        else
        {
            addressArray = new JsonArray("172.16.0.2/32");
        }

        var peerObj = new JsonObject
        {
            ["address"] = server,
            ["port"] = serverPort,
            ["public_key"] = peerPublicKey,
            ["allowed_ips"] = new JsonArray("0.0.0.0/0", "::/0")
        };

        if (parsed.TryGetPropertyValue("reserved", out var reservedNode) && reservedNode is not null)
        {
            peerObj["reserved"] = reservedNode.DeepClone();
        }
        CopyOptionalString(parsed, peerObj, "pre_shared_key");

        return new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = tag,
            ["address"] = addressArray,
            ["private_key"] = privateKey,
            ["mtu"] = mtu,
            ["peers"] = new JsonArray { peerObj }
        };
    }

    private static JsonObject BuildVpnOutboundObject(VpnNode node)
    {
        JsonObject? parsed = null;
        if (!string.IsNullOrWhiteSpace(node.ConfigJson))
        {
            try
            {
                parsed = JsonNode.Parse(node.ConfigJson) as JsonObject;
            }
            catch
            {
                parsed = null;
            }
        }

        parsed ??= new JsonObject();

        string protocol = (node.Protocol ?? "socks").Trim().ToLowerInvariant();
        if (protocol == "socks5") protocol = "socks";
        if (protocol == "ss") protocol = "shadowsocks";

        if (protocol == "direct")
        {
            return new JsonObject
            {
                ["type"] = "direct",
                ["tag"] = "vpn-out"
            };
        }

        string server = GetStringOrDefault(parsed, "server", "127.0.0.1");
        int serverPort = GetIntOrDefault(parsed, "server_port", 1080);

        switch (protocol)
        {
            case "http":
            {
                var obj = new JsonObject
                {
                    ["type"] = "http",
                    ["tag"] = "vpn-out",
                    ["server"] = server,
                    ["server_port"] = serverPort
                };
                CopyOptionalString(parsed, obj, "username");
                CopyOptionalString(parsed, obj, "password");
                if (parsed["tls"] is JsonNode tlsNode)
                {
                    obj["tls"] = tlsNode.DeepClone();
                }
                return obj;
            }

            case "shadowsocks":
            {
                return new JsonObject
                {
                    ["type"] = "shadowsocks",
                    ["tag"] = "vpn-out",
                    ["server"] = server,
                    ["server_port"] = serverPort,
                    ["method"] = GetStringOrDefault(parsed, "method", "aes-256-gcm"),
                    ["password"] = GetStringOrDefault(parsed, "password", "password")
                };
            }

            case "vless":
            {
                var obj = new JsonObject
                {
                    ["type"] = "vless",
                    ["tag"] = "vpn-out",
                    ["server"] = server,
                    ["server_port"] = serverPort,
                    ["uuid"] = GetStringOrDefault(parsed, "uuid", "00000000-0000-0000-0000-000000000000")
                };
                CopyOptionalString(parsed, obj, "flow");
                if (parsed["tls"] is JsonNode tlsNode)
                {
                    obj["tls"] = tlsNode.DeepClone();
                }
                else if (parsed.TryGetPropertyValue("sni", out var sniNode) && sniNode is not null)
                {
                    obj["tls"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["server_name"] = sniNode.ToString()
                    };
                }
                if (parsed["transport"] is JsonNode transportNode)
                {
                    obj["transport"] = transportNode.DeepClone();
                }
                return obj;
            }

            default: // socks / socks5
            {
                var obj = new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "vpn-out",
                    ["server"] = server,
                    ["server_port"] = serverPort,
                    ["version"] = "5"
                };
                CopyOptionalString(parsed, obj, "username");
                CopyOptionalString(parsed, obj, "password");
                return obj;
            }
        }
    }

    private static string GetStringOrDefault(JsonObject obj, string key, string fallback)
    {
        if (obj.TryGetPropertyValue(key, out var val) && val is not null)
        {
            string s = val.ToString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }
        return fallback;
    }

    private static int GetIntOrDefault(JsonObject obj, string key, int fallback)
    {
        if (obj.TryGetPropertyValue(key, out var val) && val is not null)
        {
            if (int.TryParse(val.ToString(), out int port) && port > 0 && port <= 65535)
            {
                return port;
            }
        }
        return fallback;
    }

    private static void CopyOptionalString(JsonObject src, JsonObject dst, string key)
    {
        if (src.TryGetPropertyValue(key, out var val) && val is not null)
        {
            string s = val.ToString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                dst[key] = s;
            }
        }
    }
}
