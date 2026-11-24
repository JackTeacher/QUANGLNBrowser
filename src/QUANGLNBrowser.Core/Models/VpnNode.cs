namespace QUANGLNBrowser.Core.Models;

/// <summary>
/// Cấu hình VPN Node lưu mã hóa trong vault.db (WireGuard / VLESS / Shadowsocks / SOCKS5 / HTTP).
/// </summary>
public sealed class VpnNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "SG";
    public string Protocol { get; set; } = "socks";
    public string ConfigJson { get; set; } = "{}";
    public int LatencyMs { get; set; } = -1;
    public bool IsDefault { get; set; }

    public string DisplayProtocol => Protocol.ToUpperInvariant();

    public string DisplayLatency => LatencyMs switch
    {
        < 0 => "— ms",
        0 => "Timeout",
        _ => $"{LatencyMs} ms"
    };

    public string DisplayTitle => $"[{CountryCode.ToUpperInvariant()}] {Name}";

    public VpnRegion Region => VpnRegionHelper.ClassifyCountryCode(CountryCode);

    public string RegionLabel => VpnRegionHelper.GetDisplayName(Region);
}
