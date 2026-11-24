using CommunityToolkit.Mvvm.ComponentModel;
using QUANGLNBrowser.Core.Models;

namespace QUANGLNBrowser.App.ViewModels;

/// <summary>
/// ViewModel hiển thị trạng thái và tiến độ thực tế của một tác vụ tải xuống trong Side Drawer.
/// </summary>
public sealed partial class DownloadTaskViewModel : ObservableObject
{
    public string Id { get; }
    public string Url { get; }
    public string MediaType { get; }

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _savePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private string _status = "Queued";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressSummary))]
    private long _downloadedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressSummary))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressSummary))]
    private double _progressPercent;

    [ObservableProperty]
    private string? _errorMessage;

    public DownloadTaskViewModel(DownloadItem item)
    {
        Id = item.Id;
        Url = item.Url;
        MediaType = item.MediaType;
        _fileName = item.FileName;
        _savePath = item.SavePath;
        _status = item.Status;
        _downloadedBytes = item.DownloadedBytes;
        _totalBytes = item.TotalBytes;
        _progressPercent = item.Status == "Completed"
            ? 100.0
            : (item.TotalBytes > 0 ? Math.Clamp(item.DownloadedBytes * 100.0 / item.TotalBytes, 0, 100) : 0);
    }

    public bool CanPause => Status is "Downloading" or "Queued" or "Remuxing";
    public bool CanResume => Status is "Paused" or "Failed";
    public bool IsCompleted => Status == "Completed";

    public string StatusLabel => Status switch
    {
        "Queued" => "Đang chờ...",
        "Downloading" => $"Đang tải ({MediaType})",
        "Remuxing" => "Đang ghép MP4 (FFmpeg)...",
        "Paused" => "Đã tạm dừng",
        "Completed" => "Hoàn tất",
        "Failed" => string.IsNullOrWhiteSpace(ErrorMessage) ? "Lỗi tải xuống" : ErrorMessage,
        _ => Status
    };

    public string ProgressSummary
    {
        get
        {
            if (TotalBytes > 0)
            {
                return $"{FormatSize(DownloadedBytes)} / {FormatSize(TotalBytes)} ({ProgressPercent:0.0}%)";
            }
            if (DownloadedBytes > 0)
            {
                return $"{FormatSize(DownloadedBytes)} ({ProgressPercent:0.0}%)";
            }
            return $"{ProgressPercent:0.0}%";
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };
}
