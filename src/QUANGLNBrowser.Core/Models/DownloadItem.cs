namespace QUANGLNBrowser.Core.Models;

/// <summary>
/// Trạng thái và thông tin tác vụ tải xuống trong vault.db.
/// </summary>
public sealed class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProfileId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SavePath { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public string Status { get; set; } = "Queued";
    public string MediaType { get; set; } = "Direct";
    public string? HeadersJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
