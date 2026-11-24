using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using QUANGLNBrowser.Core.Interfaces;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;

namespace QUANGLNBrowser.Network;

/// <summary>
/// Quản lý tiến trình sidecar native\sing-box.exe qua JobObjectManager (local SOCKS5/HTTP 127.0.0.1:10809),
/// tích hợp Clash API Telemetry thời gian thực (tốc độ / dung lượng / số kết nối kiểu Opera VPN),
/// Split Tunneling, Bypass Search Engine, Per-Site Bypass, Double VPN Multi-Hop và Kill Switch tự động.
/// </summary>
public sealed class VpnService : IVpnService
{
    private readonly JobObjectManager _jobManager;
    private readonly string _runtimeDir;
    private readonly object _sync = new();
    private readonly HttpClient _clashApiClient;
    private readonly List<string> _customBypassDomains = new();

    private Process? _singBoxProcess;
    private CancellationTokenSource? _telemetryCts;
    private DateTime? _connectedSinceUtc;
    private bool _intentionalStop;
    private bool _disposed;

    public event EventHandler? StateChanged;
    public event EventHandler<VpnTelemetry>? TelemetryUpdated;

    public bool IsRunning { get; private set; }
    public bool IsKillSwitchEnabled { get; set; } = true;
    public bool IsKillSwitchTripped { get; private set; }
    public bool IsSplitTunnelingEnabled { get; set; } = true;
    public bool BypassSearchEngines { get; set; } = true;
    public bool IsDoubleVpnEnabled { get; set; }
    public int LocalSocksPort { get; }
    public int ClashApiPort { get; private set; }
    public VpnNode? ActiveNode { get; private set; }
    public VpnNode? ActiveDoubleVpnEntryNode { get; private set; }
    public VpnTelemetry LatestTelemetry { get; private set; } = new();
    public string LastError { get; private set; } = string.Empty;

    public IReadOnlyList<string> CustomBypassDomains
    {
        get
        {
            lock (_sync)
            {
                return _customBypassDomains.ToArray();
            }
        }
    }

    public VpnService(
        JobObjectManager jobManager,
        int localSocksPort = 10809,
        string? runtimeDir = null,
        int clashApiPort = 0)
    {
        _jobManager = jobManager;
        LocalSocksPort = localSocksPort;
        ClashApiPort = clashApiPort > 0 ? clashApiPort : AllocateAvailableLoopbackPort(preferredPort: localSocksPort == 10809 ? 19090 : 0);
        _runtimeDir = runtimeDir ?? Path.Combine(AppPaths.AppDataRoot, "vpn");

        // Không dùng proxy hệ thống khi gọi Clash API cục bộ 127.0.0.1
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        };
        _clashApiClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    public void SetCustomBypassDomains(IEnumerable<string> domains)
    {
        lock (_sync)
        {
            _customBypassDomains.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string d in domains)
            {
                string clean = VpnNodeParser.NormalizeBypassDomain(d);
                if (!string.IsNullOrEmpty(clean) && seen.Add(clean))
                {
                    _customBypassDomains.Add(clean);
                }
            }
        }
    }

    public Task<bool> StartAsync(VpnNode node, CancellationToken cancellationToken = default) =>
        StartAsync(node, doubleVpnEntryNode: null, cancellationToken);

    public async Task<bool> StartAsync(
        VpnNode node,
        VpnNode? doubleVpnEntryNode,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(node);

        string singBoxPath = AppPaths.GetNativeExePath("sing-box.exe");
        if (!File.Exists(singBoxPath))
        {
            LastError = $"Không tìm thấy {singBoxPath}";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        await StopInternalAsync(resetKillSwitch: true);

        // Đảm bảo cổng Clash API sẵn sàng (nếu bị chiếm bởi instance khác thì tự đổi cổng trống)
        if (!IsLoopbackPortAvailable(ClashApiPort))
        {
            ClashApiPort = AllocateAvailableLoopbackPort(preferredPort: 0);
        }

        string[] customDomainsSnapshot;
        lock (_sync)
        {
            customDomainsSnapshot = _customBypassDomains.ToArray();
        }

        VpnNode? effectiveEntryHop = IsDoubleVpnEnabled ? doubleVpnEntryNode : null;

        Directory.CreateDirectory(_runtimeDir);
        string configPath = Path.Combine(_runtimeDir, $"singbox_runtime_{LocalSocksPort}.json");
        string configJson = SingBoxConfigGenerator.GenerateRuntimeConfigJson(
            node,
            LocalSocksPort,
            IsSplitTunnelingEnabled,
            customDomainsSnapshot,
            BypassSearchEngines,
            ClashApiPort,
            effectiveEntryHop);

        await File.WriteAllTextAsync(configPath, configJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        var psi = new ProcessStartInfo
        {
            FileName = singBoxPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);

        try
        {
            var proc = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };
            proc.Exited += OnSingBoxProcessExited;

            if (!proc.Start())
            {
                LastError = "Không thể khởi chạy tiến trình sing-box.exe.";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }

            _jobManager.AssignProcess(proc);

            lock (_sync)
            {
                _intentionalStop = false;
                _singBoxProcess = proc;
            }

            // Đợi sing-box mở cổng local SOCKS5 (127.0.0.1:LocalSocksPort)
            bool bound = await WaitForLocalPortBindAsync(proc, LocalSocksPort, cancellationToken);
            if (!bound || proc.HasExited)
            {
                string err = proc.HasExited ? await proc.StandardError.ReadToEndAsync(cancellationToken) : "Timeout mở cổng proxy cục bộ.";
                await StopInternalAsync(resetKillSwitch: true);
                LastError = $"sing-box khởi động thất bại: {err.Trim()}";
                AppLogger.Error(LastError);
                StateChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }

            _connectedSinceUtc = DateTime.UtcNow;
            ActiveNode = node;
            ActiveDoubleVpnEntryNode = effectiveEntryHop;
            IsRunning = true;
            IsKillSwitchTripped = false;
            LastError = string.Empty;
            LatestTelemetry = new VpnTelemetry { ConnectedSinceUtc = _connectedSinceUtc };

            StartTelemetryPollingLoop();

            AppLogger.Info($"sing-box VPN connected via node '{node.DisplayTitle}' (DoubleVPN={effectiveEntryHop is not null}) on 127.0.0.1:{LocalSocksPort} (ClashAPI:{ClashApiPort})");
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Lỗi khởi chạy sing-box: {ex.Message}";
            AppLogger.Error("VpnService.StartAsync error", ex);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }

    private void StartTelemetryPollingLoop()
    {
        StopTelemetryPollingLoop();

        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _telemetryCts = cts;
        }

        _ = Task.Run(async () =>
        {
            long prevDown = 0;
            long prevUp = 0;
            var prevTime = Stopwatch.StartNew();

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    if (!IsRunning || cts.IsCancellationRequested)
                    {
                        break;
                    }

                    var sample = await FetchClashConnectionsAsync(cts.Token);
                    if (sample is null)
                    {
                        continue;
                    }

                    double elapsedSec = Math.Max(0.25, prevTime.Elapsed.TotalSeconds);
                    prevTime.Restart();

                    double downSpeed = Math.Max(0, (sample.Value.DownloadTotal - prevDown) / elapsedSec);
                    double upSpeed = Math.Max(0, (sample.Value.UploadTotal - prevUp) / elapsedSec);
                    prevDown = sample.Value.DownloadTotal;
                    prevUp = sample.Value.UploadTotal;

                    var telemetry = new VpnTelemetry
                    {
                        DownloadTotalBytes = sample.Value.DownloadTotal,
                        UploadTotalBytes = sample.Value.UploadTotal,
                        DownloadBytesPerSec = downSpeed,
                        UploadBytesPerSec = upSpeed,
                        ActiveConnections = sample.Value.ActiveConnections,
                        MemoryBytes = sample.Value.MemoryBytes,
                        ConnectedSinceUtc = _connectedSinceUtc
                    };

                    LatestTelemetry = telemetry;
                    TelemetryUpdated?.Invoke(this, telemetry);
                }
            }
            catch (OperationCanceledException)
            {
                // Kết thúc bình thường khi dừng VPN
            }
            catch
            {
                // Không làm sập vòng polling nếu Clash API tạm thời bận
            }
        });
    }

    private async Task<(long DownloadTotal, long UploadTotal, int ActiveConnections, long MemoryBytes)?> FetchClashConnectionsAsync(CancellationToken ct)
    {
        try
        {
            string json = await _clashApiClient.GetStringAsync($"http://127.0.0.1:{ClashApiPort}/connections", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            long downTotal = root.TryGetProperty("downloadTotal", out var dt) && dt.TryGetInt64(out long dVal) ? dVal : 0;
            long upTotal = root.TryGetProperty("uploadTotal", out var ut) && ut.TryGetInt64(out long uVal) ? uVal : 0;
            long mem = root.TryGetProperty("memory", out var m) && m.TryGetInt64(out long mVal) ? mVal : 0;
            int connCount = 0;
            if (root.TryGetProperty("connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
            {
                connCount = conns.GetArrayLength();
            }

            return (downTotal, upTotal, connCount, mem);
        }
        catch
        {
            return null;
        }
    }

    private void StopTelemetryPollingLoop()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _telemetryCts;
            _telemetryCts = null;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // Bỏ qua
            }
            finally
            {
                cts.Dispose();
            }
        }
    }

    private async Task<bool> WaitForLocalPortBindAsync(Process proc, int port, CancellationToken ct)
    {
        for (int i = 0; i < 25; i++)
        {
            if (proc.HasExited)
            {
                return false;
            }

            try
            {
                using var tcp = new TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(120);
                await tcp.ConnectAsync("127.0.0.1", port, timeoutCts.Token);
                return true;
            }
            catch
            {
                await Task.Delay(80, ct);
            }
        }

        return !proc.HasExited;
    }

    private void OnSingBoxProcessExited(object? sender, EventArgs e)
    {
        bool shouldTripKillSwitch;
        lock (_sync)
        {
            if (_intentionalStop || _disposed)
            {
                return;
            }
            IsRunning = false;
            _connectedSinceUtc = null;
            shouldTripKillSwitch = IsKillSwitchEnabled;
            if (shouldTripKillSwitch)
            {
                IsKillSwitchTripped = true;
                LastError = "Kill Switch đã kích hoạt: sing-box.exe ngắt kết nối đột ngột! Toàn bộ lưu lượng đã bị khóa.";
            }
        }

        StopTelemetryPollingLoop();
        AppLogger.Info($"sing-box process exited unexpectedly. KillSwitchTripped={shouldTripKillSwitch}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync()
    {
        await StopInternalAsync(resetKillSwitch: true);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetKillSwitch()
    {
        lock (_sync)
        {
            IsKillSwitchTripped = false;
            LastError = string.Empty;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<int> PingNodeAsync(VpnNode node, int timeoutMs = 2500, CancellationToken cancellationToken = default) =>
        VpnNodeParser.MeasureTcpLatencyAsync(node, timeoutMs, cancellationToken);

    private async Task StopInternalAsync(bool resetKillSwitch)
    {
        StopTelemetryPollingLoop();

        Process? proc;
        lock (_sync)
        {
            _intentionalStop = true;
            proc = _singBoxProcess;
            _singBoxProcess = null;
            IsRunning = false;
            ActiveNode = null;
            ActiveDoubleVpnEntryNode = null;
            _connectedSinceUtc = null;
            LatestTelemetry = new VpnTelemetry();
            if (resetKillSwitch)
            {
                IsKillSwitchTripped = false;
                LastError = string.Empty;
            }
        }

        if (proc is not null)
        {
            proc.Exited -= OnSingBoxProcessExited;
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            catch
            {
                // Bỏ qua nếu tiến trình đã kết thúc
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static bool IsLoopbackPortAvailable(int port)
    {
        if (port <= 0 || port > 65535) return false;
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int AllocateAvailableLoopbackPort(int preferredPort)
    {
        if (preferredPort > 0 && IsLoopbackPortAvailable(preferredPort))
        {
            return preferredPort;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopInternalAsync(resetKillSwitch: true).GetAwaiter().GetResult();
        _clashApiClient.Dispose();
    }
}
