namespace QUANGLNBrowser.Core.Models;

/// <summary>
/// Bản ghi lịch sử duyệt web lưu trong vault.db.
/// </summary>
public sealed class HistoryEntry
{
    public long Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime VisitTime { get; set; } = DateTime.UtcNow;
    public int VisitCount { get; set; } = 1;
}
