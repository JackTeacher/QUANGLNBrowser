using System.Windows;
using QUANGLNBrowser.Core.Interfaces;
using QUANGLNBrowser.Core.Services;
using QUANGLNBrowser.Network;

namespace QUANGLNBrowser.App.Services;

/// <summary>
/// Tập hợp các dịch vụ lõi dùng chung giữa Cửa sổ chính và các Cửa sổ phụ / Cửa sổ Ẩn danh.
/// Giúp mở nhiều cửa sổ không bị khóa lại LockScreen hoặc xung đột cổng sing-box / vault.db.
/// </summary>
public sealed class BrowserSessionServices
{
    private readonly List<Window> _childWindows = new();
    private readonly List<Func<bool>> _vpnUsageProbes = new();

    public required IVaultService VaultService { get; init; }
    public required IIdleMonitorService IdleMonitor { get; init; }
    public required JobObjectManager JobManager { get; init; }
    public required IAdBlockEngine AdBlockEngine { get; init; }
    public required IMediaSnifferService MediaSniffer { get; init; }
    public required IDownloadManager DownloadManager { get; init; }
    public required IVpnService VpnService { get; init; }
    public string? ProfileId { get; set; }
    public Func<bool>? PrimaryVpnUsageProbe { get; set; }
    public Action? RequestGlobalLock { get; set; }

    public void RegisterChildWindow(Window window, Func<bool> hasActiveVpnTab)
    {
        if (!_childWindows.Contains(window))
        {
            _childWindows.Add(window);
            _vpnUsageProbes.Add(hasActiveVpnTab);
            window.Closed += (_, _) =>
            {
                _childWindows.Remove(window);
                _vpnUsageProbes.Remove(hasActiveVpnTab);
            };
        }
    }

    public bool IsAnyWindowUsingVpn()
    {
        if (PrimaryVpnUsageProbe?.Invoke() == true)
        {
            return true;
        }

        return _vpnUsageProbes.Any(probe =>
        {
            try
            {
                return probe();
            }
            catch
            {
                return false;
            }
        });
    }

    public void CloseAllChildWindows()
    {
        foreach (var win in _childWindows.ToArray())
        {
            try
            {
                win.Close();
            }
            catch
            {
                // Bỏ qua nếu cửa sổ đang đóng
            }
        }
        _childWindows.Clear();
        _vpnUsageProbes.Clear();
    }
}
