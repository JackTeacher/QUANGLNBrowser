using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QUANGLNBrowser.App.ViewModels;

/// <summary>
/// Đại diện cho một dòng gợi ý tự động trên thanh địa chỉ (Omnibox):
/// Lịch sử đã truy cập (kèm Favicon &amp; nút xóa ×), Dấu trang (☆) hoặc Tìm kiếm Google.
/// </summary>
public sealed partial class OmniboxSuggestionItem : ObservableObject
{
    public long? HistoryId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string DisplayUrl { get; init; } = string.Empty;
    public bool IsBookmark { get; init; }
    public bool IsSearchQuery { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFavicon))]
    private ImageSource? _faviconSource;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasFavicon => FaviconSource is not null;
    public bool HasDisplayUrl => !string.IsNullOrWhiteSpace(DisplayUrl);
    public bool CanDelete => HistoryId.HasValue;

    public static string FormatDisplayUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        string clean = url.Trim();
        if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[8..];
        }
        else if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[7..];
        }

        if (clean.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[4..];
        }

        return clean.TrimEnd('/');
    }
}
