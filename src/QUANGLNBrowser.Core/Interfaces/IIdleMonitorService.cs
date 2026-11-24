namespace QUANGLNBrowser.Core.Interfaces;

/// <summary>
/// Theo dõi thời gian nhàn rỗi của hệ thống để tự động khóa trình duyệt.
/// </summary>
public interface IIdleMonitorService : IDisposable
{
    event EventHandler? IdleTimeoutReached;

    void Start(TimeSpan timeout);
    void Stop();
    void UpdateTimeout(TimeSpan timeout);
    TimeSpan GetIdleDuration();
}
