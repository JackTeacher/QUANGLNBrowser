namespace QUANGLNBrowser.Core.Models;

/// <summary>
/// Bản ghi dấu trang (Bookmark) lưu trong vault.db.
/// </summary>
public sealed class BookmarkEntry
{
    public long Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string FolderName { get; set; } = "Bookmarks Bar";
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public byte[]? FaviconBlob { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
