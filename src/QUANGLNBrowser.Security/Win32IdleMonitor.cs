using System.Runtime.InteropServices;
using QUANGLNBrowser.Core.Interfaces;

namespace QUANGLNBrowser.Security;

/// <summary>
/// Theo dõi thời gian nhàn rỗi hệ thống bằng Win32 GetLastInputInfo để kích hoạt Auto-Lock.
/// </summary>
public sealed partial class Win32IdleMonitor : IIdleMonitorService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private TimeSpan _timeout = TimeSpan.FromMinutes(5);
    private bool _triggeredForCurrentIdle;
    private bool _disposed;

    public event EventHandler? IdleTimeoutReached;

    public void Start(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        _timeout = timeout;
        _triggeredForCurrentIdle = false;
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        _ = MonitorLoopAsync(_timer, _cts.Token);
    }

    public void UpdateTimeout(TimeSpan timeout)
    {
        _timeout = timeout;
        _triggeredForCurrentIdle = false;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _timer?.Dispose();
        _timer = null;
        _triggeredForCurrentIdle = false;
    }

    public TimeSpan GetIdleDuration()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        uint idleTicks = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleTicks);
    }

    private async Task MonitorLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var idle = GetIdleDuration();
                if (idle >= _timeout)
                {
                    if (!_triggeredForCurrentIdle)
                    {
                        _triggeredForCurrentIdle = true;
                        IdleTimeoutReached?.Invoke(this, EventArgs.Empty);
                    }
                }
                else
                {
                    _triggeredForCurrentIdle = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Bình thường khi Stop/Dispose
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
    }
}
