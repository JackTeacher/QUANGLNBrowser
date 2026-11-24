using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;

namespace QUANGLNBrowser.Media;

/// <summary>
/// Nhúng libmpv-2.dll trực tiếp vào WPF qua HwndHost (wid = Handle cửa sổ con),
/// hỗ trợ giải mã GPU (hwdec=auto-safe), khuếch đại âm lượng 200%, tốc độ 0.25x-4.0x và phụ đề ngoài.
/// </summary>
public sealed class MpvVideoHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int SsBlackRect = 0x00000004;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpszClassName,
        string lpszWindowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr hwndParent,
        IntPtr hMenu,
        IntPtr hInst,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private IntPtr _hwndChild;
    private IntPtr _mpvHandle;
    private bool _isPaused;
    private double _subtitleDelaySeconds;
    private DetectedMedia? _pendingMedia;

    public bool IsMpvInitialized => _mpvHandle != IntPtr.Zero;
    public bool IsPaused => _isPaused;
    public double SubtitleDelaySeconds => _subtitleDelaySeconds;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwndChild = CreateWindowEx(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | WsClipChildren | SsBlackRect,
            0,
            0,
            Math.Max(1, (int)ActualWidth),
            Math.Max(1, (int)ActualHeight),
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        InitializeMpv(_hwndChild);

        if (_pendingMedia is not null)
        {
            var media = _pendingMedia;
            _pendingMedia = null;
            LoadMedia(media);
        }

        return new HandleRef(this, _hwndChild);
    }

    private void InitializeMpv(IntPtr hwnd)
    {
        try
        {
            if (!File.Exists(AppPaths.GetNativeDllPath(MpvInterop.DllName)))
            {
                AppLogger.Error("libmpv-2.dll not found in AppContext.BaseDirectory.");
                return;
            }

            _mpvHandle = MpvInterop.mpv_create();
            if (_mpvHandle == IntPtr.Zero)
            {
                return;
            }

            long wid = hwnd.ToInt64();
            MpvInterop.mpv_set_option(_mpvHandle, MpvInterop.ToUtf8NullTerminated("wid"), MpvInterop.MpvFormatInt64, ref wid);
            MpvInterop.SetOptionString(_mpvHandle, "vo", "gpu");
            MpvInterop.SetOptionString(_mpvHandle, "gpu-context", "d3d11");
            MpvInterop.SetOptionString(_mpvHandle, "gpu-api", "d3d11");
            MpvInterop.SetOptionString(_mpvHandle, "hwdec", "auto-safe");
            MpvInterop.SetOptionString(_mpvHandle, "video-sync", "display-resample");
            MpvInterop.SetOptionString(_mpvHandle, "interpolation", "yes");
            MpvInterop.SetOptionString(_mpvHandle, "demuxer-max-bytes", "150MiB");
            MpvInterop.SetOptionString(_mpvHandle, "demuxer-max-back-bytes", "50MiB");
            MpvInterop.SetOptionString(_mpvHandle, "cache", "yes");
            MpvInterop.SetOptionString(_mpvHandle, "cache-secs", "60");
            MpvInterop.SetOptionString(_mpvHandle, "keep-open", "yes");
            MpvInterop.SetOptionString(_mpvHandle, "volume-max", "200");
            MpvInterop.SetOptionString(_mpvHandle, "input-default-bindings", "no");
            MpvInterop.SetOptionString(_mpvHandle, "input-vo-keyboard", "no");
            MpvInterop.SetOptionString(_mpvHandle, "osc", "no");

            // Cho phép libmpv dùng yt-dlp trong thư mục native nếu người dùng truyền link trang web
            string ytDlpPath = AppPaths.GetNativeExePath("yt-dlp.exe");
            if (File.Exists(ytDlpPath))
            {
                MpvInterop.SetOptionString(_mpvHandle, "script-opts", $"ytdl_hook-ytdl_path={ytDlpPath}");
            }

            int rc = MpvInterop.mpv_initialize(_mpvHandle);
            if (rc < 0)
            {
                AppLogger.Error($"mpv_initialize failed with code {rc}");
                MpvInterop.mpv_terminate_destroy(_mpvHandle);
                _mpvHandle = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to initialize libmpv-2.dll", ex);
            _mpvHandle = IntPtr.Zero;
        }
    }

    public void LoadMedia(DetectedMedia media)
    {
        if (_mpvHandle == IntPtr.Zero)
        {
            _pendingMedia = media;
            return;
        }

        if (!string.IsNullOrWhiteSpace(media.Referer))
        {
            MpvInterop.SetPropertyString(_mpvHandle, "referrer", media.Referer);
        }

        if (!string.IsNullOrWhiteSpace(media.UserAgent))
        {
            MpvInterop.SetPropertyString(_mpvHandle, "user-agent", media.UserAgent);
        }

        var headerFields = new List<string>();
        if (!string.IsNullOrWhiteSpace(media.Cookie))
        {
            headerFields.Add($"Cookie: {media.Cookie.Replace(",", "%2C")}");
        }
        if (!string.IsNullOrWhiteSpace(media.Referer))
        {
            headerFields.Add($"Referer: {media.Referer}");
        }
        if (headerFields.Count > 0)
        {
            MpvInterop.SetPropertyString(_mpvHandle, "http-header-fields", string.Join(",", headerFields));
        }

        _isPaused = false;
        MpvInterop.SetPropertyString(_mpvHandle, "pause", "no");
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            ResizeChildWindow((int)ActualWidth, (int)ActualHeight);
        }
        AppLogger.Info($"[MpvVideoHost] LoadMedia: {media.Url} (Title: {media.DisplayTitle})");
        MpvInterop.ExecuteCommand(_mpvHandle, "loadfile", media.Url, "replace");
        if (!string.IsNullOrWhiteSpace(media.AudioUrl))
        {
            MpvInterop.ExecuteCommand(_mpvHandle, "audio-add", media.AudioUrl, "auto");
        }
    }

    public void TogglePause()
    {
        SetPaused(!_isPaused);
    }

    public void SetPaused(bool paused)
    {
        _isPaused = paused;
        if (_mpvHandle != IntPtr.Zero)
        {
            MpvInterop.SetPropertyString(_mpvHandle, "pause", paused ? "yes" : "no");
        }
    }

    public void SeekToSeconds(double seconds)
    {
        if (_mpvHandle != IntPtr.Zero)
        {
            MpvInterop.SetPropertyDouble(_mpvHandle, "time-pos", Math.Max(0, seconds));
        }
    }

    public void SeekRelative(double deltaSeconds)
    {
        if (_mpvHandle != IntPtr.Zero)
        {
            MpvInterop.ExecuteCommand(_mpvHandle, "seek", deltaSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "relative");
        }
    }

    public void SetVolume(double volumePercent)
    {
        if (_mpvHandle != IntPtr.Zero)
        {
            MpvInterop.SetPropertyDouble(_mpvHandle, "volume", Math.Clamp(volumePercent, 0.0, 200.0));
        }
    }

    public void SetSpeed(double speed)
    {
        if (_mpvHandle != IntPtr.Zero)
        {
            MpvInterop.SetPropertyDouble(_mpvHandle, "speed", Math.Clamp(speed, 0.25, 4.0));
        }
    }

    public void SetMuted(bool muted)
    {
        if (_mpvHandle != IntPtr.Zero)
        {
            MpvInterop.SetPropertyString(_mpvHandle, "mute", muted ? "yes" : "no");
        }
    }

    public void AddSubtitleFile(string subtitleFilePath)
    {
        if (_mpvHandle != IntPtr.Zero && File.Exists(subtitleFilePath))
        {
            MpvInterop.ExecuteCommand(_mpvHandle, "sub-add", subtitleFilePath, "select");
        }
    }

    public double AdjustSubtitleDelay(double deltaSeconds)
    {
        _subtitleDelaySeconds = Math.Round(_subtitleDelaySeconds + deltaSeconds, 2);
        if (_mpvHandle != IntPtr.Zero)
        {
            MpvInterop.SetPropertyDouble(_mpvHandle, "sub-delay", _subtitleDelaySeconds);
        }
        return _subtitleDelaySeconds;
    }

    public double GetPositionSeconds() =>
        MpvInterop.GetPropertyDouble(_mpvHandle, "time-pos", 0.0);

    public double GetDurationSeconds() =>
        MpvInterop.GetPropertyDouble(_mpvHandle, "duration", 0.0);

    protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ResizeChildWindow((int)sizeInfo.NewSize.Width, (int)sizeInfo.NewSize.Height);
    }

    public void ResizeChildWindow(int width, int height)
    {
        if (_hwndChild != IntPtr.Zero && width > 0 && height > 0)
        {
            SetWindowPos(_hwndChild, IntPtr.Zero, 0, 0, width, height, SwpNoZOrder | SwpNoActivate);
        }
    }

    public void Stop()
    {
        _isPaused = true;
        if (_mpvHandle != IntPtr.Zero)
        {
            try
            {
                MpvInterop.ExecuteCommand(_mpvHandle, "stop");
                MpvInterop.SetPropertyString(_mpvHandle, "pause", "yes");
            }
            catch { }
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Stop();
        if (_mpvHandle != IntPtr.Zero)
        {
            try
            {
                MpvInterop.mpv_terminate_destroy(_mpvHandle);
            }
            catch
            {
                // Bỏ qua lỗi khi đóng handle
            }
            _mpvHandle = IntPtr.Zero;
        }

        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
            _hwndChild = IntPtr.Zero;
        }
    }
}
