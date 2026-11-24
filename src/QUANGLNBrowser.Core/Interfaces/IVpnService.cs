using QUANGLNBrowser.Core.Models;

namespace QUANGLNBrowser.Core.Interfaces;

/// <summary>
/// Giao diện dịch vụ VPN tích hợp (sing-box sidecar, Kill Switch, Split Tunneling, đo độ trễ TCP Ping).
/// </summary>
public interface IVpnService : IDisposable
{
    event EventHandler? StateChanged;
    event EventHandler<VpnTelemetry>? TelemetryUpdated;

    bool IsRunning { get; }
    bool IsKillSwitchEnabled { get; set; }
    bool IsKillSwitchTripped { get; }
    bool IsSplitTunnelingEnabled { get; set; }
    bool BypassSearchEngines { get; set; }
    bool IsDoubleVpnEnabled { get; set; }
    int LocalSocksPort { get; }
    int ClashApiPort { get; }
    VpnNode? ActiveNode { get; }
    VpnNode? ActiveDoubleVpnEntryNode { get; }
    VpnTelemetry LatestTelemetry { get; }
    IReadOnlyList<string> CustomBypassDomains { get; }
    string LastError { get; }

    void SetCustomBypassDomains(IEnumerable<string> domains);
    Task<bool> StartAsync(VpnNode node, CancellationToken cancellationToken = default);
    Task<bool> StartAsync(VpnNode node, VpnNode? doubleVpnEntryNode, CancellationToken cancellationToken = default);
    Task StopAsync();
    void ResetKillSwitch();
    Task<int> PingNodeAsync(VpnNode node, int timeoutMs = 2500, CancellationToken cancellationToken = default);
}
