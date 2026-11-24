namespace QUANGLNBrowser.Core.Models;

/// <summary>
/// Dữ liệu đo lường lưu lượng thời gian thực của đường hầm VPN (đọc từ sing-box Clash API).
/// </summary>
public sealed class VpnTelemetry
{
    public long DownloadTotalBytes { get; init; }
    public long UploadTotalBytes { get; init; }
    public double DownloadBytesPerSec { get; init; }
    public double UploadBytesPerSec { get; init; }
    public int ActiveConnections { get; init; }
    public long MemoryBytes { get; init; }
    public DateTime? ConnectedSinceUtc { get; init; }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.2} GB";
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0.5) return "0 KB/s";
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:0.0} KB/s";
        return $"{bytesPerSec / (1024.0 * 1024):0.2} MB/s";
    }
}
