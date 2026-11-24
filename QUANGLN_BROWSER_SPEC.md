# QUANGLN Browser — Tài liệu Đặc tả Thiết kế & Triển khai (Master Spec)

> **Nền tảng:** Windows Desktop (x64) · **.NET 8 (WPF)** · **CefSharp (Chromium)** · **WPF-UI v3 (Fluent Dark Mode)**  
> **Tính năng cốt lõi:** Khóa đăng nhập (PIN / Master Password / Windows Hello / Decoy Profile) · Mã hóa SQLCipher (AES-256) · Chặn quảng cáo 2 tầng (EasyList + ABPVN) · VPN tích hợp (`sing-box` + Split Tunneling + Kill Switch + Chống WebRTC Leak) · Bắt link & Tải Video đa luồng (MP4/HLS/DASH + `yt-dlp` + `ffmpeg`, kiểu Cốc Cốc) · Trình xem Video độc lập (`libmpv` GPU + Mini-mode + Phụ đề).

---

## 1. Cấu trúc Solution & Công nghệ lõi (Tech Stack)

### 1.1. Cấu trúc thư mục Workspace
```text
QUANGLNBrowser/
├── .agents/
│   ├── rules/quangln-browser.md       # Quy tắc luôn bật cho Google Antigravity
│   └── workflows/implement-phase.md   # Quy trình /implement-phase
├── native/
│   └── win-x64/                       # sing-box.exe, ffmpeg.exe, ffprobe.exe, yt-dlp.exe, libmpv-2.dll, [adblock_rust.dll]
├── src/
│   ├── QUANGLNBrowser.Core/           # net8.0 — Models, Interfaces, EventBus (không phụ thuộc WPF/CEF)
│   ├── QUANGLNBrowser.Security/       # Argon2id, SQLCipher Vault, Windows Hello, DPAPI, Idle Auto-Lock
│   ├── QUANGLNBrowser.Network/        # sing-box VPN Controller, Kill Switch, Win32 JobObjectManager
│   ├── QUANGLNBrowser.Engine/         # CefSharp Wrapper, SmartResourceRequestHandler (AdBlock + Sniffer), Video Overlay JS
│   ├── QUANGLNBrowser.Media/          # libmpv HwndHost Player, Multi-thread Downloader, FFmpeg/yt-dlp Workers
│   └── QUANGLNBrowser.App/            # WPF UI (FluentWindow, LockScreenView, MpvPlayerWindow, MVVM)
├── .gitignore
├── Directory.Build.props              # Ép cứng .NET 8 + PlatformTarget=x64 + Auto-copy native binaries
├── QUANGLNBrowser.sln
├── QUANGLN_BROWSER_SPEC.md
└── setup-native.ps1                   # Script tải tự động bộ binary native win-x64
```

### 1.2. Danh sách NuGet & Thư viện lõi

| Phân hệ | Gói NuGet / Binary | Mục đích |
| :--- | :--- | :--- |
| **Browser Engine** | `CefSharp.Wpf.NETCore` | Nhúng nhân Chromium (x64 OSR) vào WPF |
| **UI & MVVM** | `WPF-UI`, `CommunityToolkit.Mvvm` | Fluent Design Dark Mode (Mica), MVVM Source Generators |
| **Database mã hóa** | `Microsoft.Data.Sqlite.Core`, `SQLitePCLRaw.bundle_e_sqlcipher` | SQLite mã hóa AES-256 cho History, Bookmarks, Downloads, VPN Nodes |
| **KDF & Auth** | `Konscious.Security.Cryptography.Argon2`, `System.Security.Cryptography.ProtectedData` | Dẫn xuất MasterKey bằng Argon2id + bảo vệ bằng DPAPI + Windows Hello |
| **AdBlock Engine** | `adblock_rust.dll` (P/Invoke) + Fallback `ManagedAdBlockEngine` (C#) | Chặn request tầng mạng (<0.1ms) hỗ trợ EasyList, EasyPrivacy, ABPVN |
| **VPN Core** | `sing-box.exe` (Sidecar) | Local SOCKS5/HTTP Proxy (`127.0.0.1:10809`), Split Tunneling |
| **Media & Player** | `libmpv-2.dll`, `ffmpeg.exe`, `ffprobe.exe`, `yt-dlp.exe` | Phát video GPU độc lập, tải đa luồng & ghép luồng HLS/DASH |

---

## 2. Quy tắc bắt buộc cho AI Agent & Kiến trúc hệ thống

1. **Bắt buộc `x64` tuyệt đối:** `Directory.Build.props` ép cứng `PlatformTarget=x64`. Mọi lệnh build/test phải dùng `-p:Platform=x64`.
2. **Đường dẫn runtime của Binary:**
   - File `.exe` sidecar nằm tại `Path.Combine(AppContext.BaseDirectory, "native", "<name>.exe")`.
   - File `.dll` native (`libmpv-2.dll`, `adblock_rust.dll`) nằm ngay tại `AppContext.BaseDirectory`.
3. **Không Zombie Process:** Mọi tiến trình con (`sing-box.exe`, `ffmpeg.exe`, `yt-dlp.exe`) phải chạy ẩn (`CreateNoWindow = true`) và gắn vào **Win32 Job Object** (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`).
4. **Dữ liệu người dùng cô lập:** Lưu toàn bộ dữ liệu tại `%AppData%\QUANGLNBrowser\profiles\<profile_id>\` (`vault.db` + `cef_cache\`).
5. **AdBlock Fallback thông minh:** Lập trình theo interface `IAdBlockEngine`. Tự động dùng `NativeRustAdBlockEngine` nếu có `adblock_rust.dll`, ngược lại fallback sang `ManagedAdBlockEngine` (thuần C#, đọc EasyList + ABPVN) để ứng dụng chạy hoàn chỉnh ngay lập tức không cần cài Rust.

---

## 3. Lược đồ Cơ sở dữ liệu mã hóa (`vault.db` — SQLCipher)

```sql
CREATE TABLE IF NOT EXISTS Profiles (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    IsDecoy INTEGER NOT NULL DEFAULT 0,
    AuthSaltBase64 TEXT NOT NULL,
    PinVerifierHash TEXT NOT NULL,
    CacheFolderName TEXT NOT NULL,
    AutoLockMinutes INTEGER NOT NULL DEFAULT 5,
    CreatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS History (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileId TEXT NOT NULL,
    Url TEXT NOT NULL,
    Title TEXT,
    VisitTime TEXT NOT NULL,
    VisitCount INTEGER NOT NULL DEFAULT 1,
    FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_History_Profile_Time ON History(ProfileId, VisitTime DESC);

CREATE TABLE IF NOT EXISTS Bookmarks (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileId TEXT NOT NULL,
    FolderName TEXT NOT NULL DEFAULT 'Bookmarks Bar',
    Title TEXT NOT NULL,
    Url TEXT NOT NULL,
    FaviconBlob BLOB,
    CreatedAt TEXT NOT NULL,
    FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS Downloads (
    Id TEXT PRIMARY KEY,
    ProfileId TEXT NOT NULL,
    Url TEXT NOT NULL,
    FileName TEXT NOT NULL,
    SavePath TEXT NOT NULL,
    TotalBytes INTEGER NOT NULL DEFAULT 0,
    DownloadedBytes INTEGER NOT NULL DEFAULT 0,
    Status TEXT NOT NULL,          -- Queued | Downloading | Paused | Remuxing | Completed | Failed
    MediaType TEXT NOT NULL,       -- Direct | HlsDash | YtDlp
    HeadersJson TEXT,
    CreatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS AdBlockDomainRules (
    Domain TEXT PRIMARY KEY,
    IsWhitelisted INTEGER NOT NULL DEFAULT 0,
    CustomCss TEXT
);

CREATE TABLE IF NOT EXISTS VpnNodes (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    CountryCode TEXT NOT NULL,
    Protocol TEXT NOT NULL,        -- wireguard | vless | shadowsocks | socks | http
    ConfigJson TEXT NOT NULL,
    LatencyMs INTEGER NOT NULL DEFAULT -1,
    IsDefault INTEGER NOT NULL DEFAULT 0
);
```

---

## 4. Thiết kế kỹ thuật 5 Module cốt lõi

### 4.1. Security & Vault (`QUANGLNBrowser.Security`)
- **Khởi tạo lần đầu:** Tạo Master Password / PIN + tùy chọn **Decoy PIN** (mở hồ sơ trống hoàn toàn) + tùy chọn **Windows Hello**.
- **Argon2id KDF:** `DegreeOfParallelism = 4`, `MemorySize = 65536` (64 MB), `Iterations = 3`, Salt 32 bytes ngẫu nhiên → sinh `MasterKey` 32 bytes (256-bit).
- **Windows Hello + DPAPI:** Nếu bật Windows Hello, mã hóa `MasterKey` bằng `ProtectedData.Protect(key, entropy, DataProtectionScope.CurrentUser)`. Khi mở app, gọi `UserConsentVerifier.RequestVerificationAsync("Mở khóa QUANGLN Browser")` → nếu `Verified` mới giải mã DPAPI và mở `vault.db`.
- **Auto-Lock & Panic Lock (`Ctrl + Shift + L`):** Đo thời gian nhàn rỗi bằng Win32 `GetLastInputInfo`. Khi khóa: tạm dừng toàn bộ media trên CefSharp & `libmpv`, đóng kết nối SQLite, xóa `MasterKey` bằng `CryptographicOperations.ZeroMemory`, hiện `LockScreenView` ở `Panel.ZIndex="999"`.

### 4.2. CefSharp Pipeline: AdBlock + Video Sniffer (`QUANGLNBrowser.Engine`)
```csharp
public sealed class SmartResourceRequestHandler : ResourceRequestHandler
{
    private readonly IAdBlockEngine _adBlock;
    private readonly IMediaSnifferService _sniffer;
    private readonly Guid _tabId;

    public SmartResourceRequestHandler(IAdBlockEngine adBlock, IMediaSnifferService sniffer, Guid tabId)
    {
        _adBlock = adBlock;
        _sniffer = sniffer;
        _tabId = tabId;
    }

    protected override CefReturnValue OnBeforeResourceLoad(
        IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, IRequestCallback callback)
    {
        string sourceUrl = frame?.Url ?? string.Empty;
        string resourceType = request.ResourceType.ToString().ToLowerInvariant();

        if (_adBlock.ShouldBlock(request.Url, sourceUrl, resourceType))
        {
            _adBlock.IncrementBlockedCount(_tabId);
            return CefReturnValue.Cancel;
        }

        if (request.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            request.Url.Contains(".mpd", StringComparison.OrdinalIgnoreCase))
        {
            _sniffer.CaptureMedia(_tabId, new DetectedMedia
            {
                Url = request.Url,
                Referer = request.Headers["Referer"] ?? sourceUrl,
                UserAgent = request.Headers["User-Agent"] ?? string.Empty,
                Type = MediaStreamType.HlsOrDash
            });
        }

        return CefReturnValue.Continue;
    }

    protected override bool OnResourceResponse(
        IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, IResponse response)
    {
        string mime = response.MimeType?.ToLowerInvariant() ?? string.Empty;
        if (mime.StartsWith("video/") || mime.StartsWith("audio/") ||
            mime.Contains("mpegurl") || mime.Contains("dash+xml"))
        {
            long.TryParse(response.Headers["Content-Length"], out long size);
            if (size > 500_000 || mime.Contains("mpegurl") || mime.Contains("dash+xml"))
            {
                _sniffer.CaptureMedia(_tabId, new DetectedMedia
                {
                    Url = request.Url,
                    MimeType = mime,
                    FileSize = size,
                    Referer = request.Headers["Referer"] ?? frame?.Url ?? string.Empty,
                    UserAgent = request.Headers["User-Agent"] ?? string.Empty
                });
            }
        }
        return false;
    }
}
```
- **Floating Video Overlay (giống Cốc Cốc):** Tại `FrameLoadEnd`, tiêm script `video-overlay.js` dùng `MutationObserver` (duyệt cả Shadow DOM) phát hiện thẻ `<video>`. Khi hover hiện thanh nút nổi gồm **⬇ Tải xuống** và **⧉ Xem riêng** gửi sự kiện về C# qua `CefSharp.PostMessage`.

### 4.3. VPN tích hợp & Chống WebRTC Leak (`QUANGLNBrowser.Network`)
- Khởi chạy `native\sing-box.exe run -c <runtime_config.json>` gắn vào `JobObjectManager`, mở local SOCKS5 tại `127.0.0.1:10809`.
- Cập nhật proxy động không cần restart trình duyệt:
```csharp
public async Task ApplyVpnProxyAsync(IRequestContext requestContext, bool enableVpn, bool killSwitchTripped = false, int port = 10809)
{
    await Cef.UIThreadTaskFactory.StartNew(() =>
    {
        var proxyConfig = new Dictionary<string, object>();
        if (killSwitchTripped)
        {
            proxyConfig["mode"] = "fixed_servers";
            proxyConfig["server"] = "socks5://127.0.0.1:1"; // Blackhole
        }
        else if (enableVpn)
        {
            proxyConfig["mode"] = "fixed_servers";
            proxyConfig["server"] = $"socks5://127.0.0.1:{port}";
            proxyConfig["bypass_list"] = "localhost;127.0.0.1;192.168.*.*;10.*.*.*";
        }
        else
        {
            proxyConfig["mode"] = "direct";
        }

        requestContext.SetPreference("proxy", proxyConfig, out _);
        requestContext.SetPreference(
            "webrtc.ip_handling_policy",
            (enableVpn || killSwitchTripped) ? "disable_non_proxied_udp" : "default",
            out _);
    });
}
```
- Hỗ trợ **Per-Tab VPN** bằng cách khởi tạo `RequestContext` riêng cho từng tab khi người dùng bật công tắc VPN riêng.

### 4.4. Bộ tải Video đa luồng (`QUANGLNBrowser.Media`)
1. **Direct MP4/MKV/MP3:** Kiểm tra `Accept-Ranges: bytes`, chia 8 luồng `HttpClient` tải song song (kèm `Referer`, `User-Agent`, `Cookie`), ghi trực tiếp vào đĩa bằng `RandomAccess.WriteAsync`. Hỗ trợ Pause/Resume.
2. **HLS (`.m3u8`) / DASH (`.mpd`):** Gọi `native\ffmpeg.exe` chạy ngầm với `-headers` đầy đủ và `-c copy -bsf:a aac_adtstoasc` để remux thành `.mp4` tức thời, đọc stderr parse `time=` để cập nhật `%` tiến độ lên UI.
3. **Nền tảng đặc thù (YouTube, TikTok, Facebook...):** Gọi `native\yt-dlp.exe --dump-json <page_url>` ở nền để nạp danh sách định dạng (4K/1080p/720p/Audio) vào Omnibox Video Sniffer Popup, sau đó tải và ghép luồng bằng `yt-dlp` + `--ffmpeg-location native\`.

### 4.5. Trình xem Video độc lập (`libmpv` + `MpvPlayerWindow`)
- Nhúng `libmpv-2.dll` qua WPF `HwndHost` (truyền `wid` = Handle cửa sổ con).
- Khi bấm **Xem riêng (Pop-out)**: tạm dừng video trên tab Web, truyền thẳng `Url`, `referrer`, `user-agent`, `http-header-fields` (`Cookie: ...`), bật `hwdec=auto-safe` để phát ngay lập tức không phải tải trước.
- Tính năng bắt buộc: Ghim luôn trên cùng (`Topmost`), Mini-mode không viền, khuếch đại âm lượng `0–200%`, tốc độ `0.25x–4.0x`, kéo thả phụ đề `.srt`/`.ass`/`.vtt` trực tiếp lên cửa sổ (`AllowDrop="True"`), chỉnh độ trễ phụ đề `±0.1s`.

---

## 5. Đặc tả Giao diện XAML (WPF-UI v3 Dark Mode)

### 5.1. `src/QUANGLNBrowser.App/App.xaml`
```xml
<Application x:Class="QUANGLNBrowser.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ui:ThemesDictionary Theme="Dark" />
                <ui:ControlsDictionary />
            </ResourceDictionary.MergedDictionaries>

            <SolidColorBrush x:Key="BrowserSurfaceBrush" Color="#141418" />
            <SolidColorBrush x:Key="BrowserChromeBrush" Color="#1C1C22" />
            <SolidColorBrush x:Key="BrowserElevatedBrush" Color="#26262F" />
            <SolidColorBrush x:Key="BrowserBorderBrush" Color="#32323D" />

            <SolidColorBrush x:Key="VpnConnectedBrush" Color="#10B981" />
            <SolidColorBrush x:Key="VpnConnectedBgBrush" Color="#2210B981" />
            <SolidColorBrush x:Key="VpnKillSwitchBrush" Color="#EF4444" />
            <SolidColorBrush x:Key="AdBlockActiveBrush" Color="#38BDF8" />
            <SolidColorBrush x:Key="AdBlockActiveBgBrush" Color="#2238BDF8" />
            <SolidColorBrush x:Key="VideoDetectedBrush" Color="#F59E0B" />
            <SolidColorBrush x:Key="VideoDetectedBgBrush" Color="#26F59E0B" />
            <SolidColorBrush x:Key="PanicLockBrush" Color="#F43F5E" />

            <BooleanToVisibilityConverter x:Key="BoolToVis" />

            <Style x:Key="OmniboxBadgeToggleStyle" TargetType="ToggleButton">
                <Setter Property="Height" Value="26" />
                <Setter Property="Padding" Value="8,0" />
                <Setter Property="Margin" Value="4,0,0,0" />
                <Setter Property="Cursor" Value="Hand" />
                <Setter Property="Foreground" Value="#E5E7EB" />
                <Setter Property="Background" Value="#2A2A34" />
                <Setter Property="BorderBrush" Value="Transparent" />
                <Setter Property="BorderThickness" Value="1" />
                <Setter Property="FontSize" Value="12" />
                <Setter Property="FontWeight" Value="SemiBold" />
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="ToggleButton">
                            <Border x:Name="BadgeBorder"
                                    Background="{TemplateBinding Background}"
                                    BorderBrush="{TemplateBinding BorderBrush}"
                                    BorderThickness="{TemplateBinding BorderThickness}"
                                    CornerRadius="13"
                                    Padding="{TemplateBinding Padding}">
                                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsMouseOver" Value="True">
                                    <Setter TargetName="BadgeBorder" Property="Opacity" Value="0.88" />
                                </Trigger>
                                <Trigger Property="IsChecked" Value="True">
                                    <Setter TargetName="BadgeBorder" Property="BorderBrush" Value="#60FFFFFF" />
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>

            <Style x:Key="SidebarIconButtonStyle" TargetType="ui:Button">
                <Setter Property="Width" Value="38" />
                <Setter Property="Height" Value="38" />
                <Setter Property="Margin" Value="0,3" />
                <Setter Property="Padding" Value="0" />
                <Setter Property="Appearance" Value="Transparent" />
                <Setter Property="HorizontalAlignment" Value="Center" />
            </Style>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

### 5.2. `src/QUANGLNBrowser.App/MainWindow.xaml`
- Cửa sổ `ui:FluentWindow` (`Title="QUANGLN Browser"`, `ExtendsContentIntoTitleBar="True"`, `WindowBackdropType="Mica"`).
- **Row 0:** Thanh Tab ngang tích hợp TitleBar, favicon/loading ring, chấm xanh báo Tab VPN, nút `+` mở tab mới (`Ctrl+T`), đóng tab (`Ctrl+W`).
- **Row 1:** Thanh điều hướng + Omnibox chứa 3 Pill Badges có Popup Flyout:
  1. **Video Sniffer (`VideoSnifferToggle`)**: Hiện sáng màu cam khi phát hiện video/HLS, Popup liệt kê luồng kèm 2 nút **Xem riêng (`libmpv`)** và **Tải xuống**.
  2. **AdBlock Shield (`AdBlockToggle`)**: Hiển thị số lượng đã chặn, Popup bật/tắt Whitelist theo tên miền.
  3. **VPN Status (`VpnToggle`)**: Hiển thị trạng thái/quốc gia, Popup chọn node `sing-box`, bật VPN riêng cho tab hiện tại, bật Kill Switch & chống WebRTC Leak.
- **Row 2:** Left Sidebar Rail (`48px`: Home, Bookmarks, History, Downloads kèm badge đếm, Media Library, Settings, Panic Lock `Ctrl+Shift+L`) + Vùng `ContentControl` nhúng `ChromiumWebBrowser` của `ActiveTab` + Side Drawer trượt phải (`340px`).
- **Overlay Layer (`Panel.ZIndex="999"`):** `views:LockScreenView` phủ toàn màn hình khi `IsLocked == true`.

### 5.3. `src/QUANGLNBrowser.App/Views/LockScreenView.xaml`
- Thẻ đăng nhập trung tâm tiêu đề **QUANGLN Browser Vault**.
- Ô `ui:PasswordBox` hỗ trợ nhập PIN / Master Password (phím `Enter` mở khóa; nhập Decoy PIN tự mở hồ sơ ẩn).
- `ui:InfoBar` thông báo lỗi xác thực & chống brute-force.
- Nút **Mở khóa bằng Windows Hello** (hiện khi `IsWindowsHelloAvailable == true`).

### 5.4. `src/QUANGLNBrowser.App/Views/MpvPlayerWindow.xaml`
- Cửa sổ phát video độc lập (`AllowsTransparency="False"`, hỗ trợ chuyển đổi `WindowStyle="None"` cho Mini-mode và `Topmost` ghim trên cùng, `AllowDrop="True"` nhận file phụ đề `.srt`/`.ass`/`.vtt`).
- Vùng trung tâm chứa `MpvVideoHost` (`HwndHost` của `libmpv-2.dll`).
- Thanh điều khiển dưới (tự ẩn khi không di chuột): Play/Pause, thanh tua thời gian (Seek Slider), âm lượng `0–200%`, chọn tốc độ (`0.5x–2.0x`), nút nạp/đồng bộ phụ đề (`-0.5s` / `+0.5s`), nút Ghim (`Topmost`) và Fullscreen (`F11`).

---

## 6. Lộ trình triển khai & Tiêu chí nghiệm thu (4 Giai đoạn)

### Giai đoạn 1 — Core Shell, WPF-UI & Vault Security
- [x] Khởi tạo `QUANGLNBrowser.sln` cùng 6 project kế thừa `Directory.Build.props` (`x64`).
- [x] Cài đặt `VaultSecurityService` (Argon2id + SQLCipher `vault.db` + DPAPI + Windows Hello + hỗ trợ Decoy Profile).
- [x] Xây dựng `MainWindow.xaml` đa tab dùng `CefSharp.Wpf.NETCore`, thanh địa chỉ, Sidebar, History/Bookmarks lưu mã hóa.
- [x] Xây dựng `LockScreenView.xaml`, Auto-lock khi nhàn rỗi và phím tắt Panic Lock (`Ctrl + Shift + L`).
- [x] **Nghiệm thu:** `dotnet build -p:Platform=x64` đạt 0 lỗi; mở app hiện màn hình khóa; nhập đúng mật khẩu mở tab duyệt web; bấm `Ctrl + Shift + L` khóa ngay lập tức.

### Giai đoạn 2 — Chặn quảng cáo 2 tầng (AdBlock Engine)
- [x] Xây dựng `IAdBlockEngine`, lớp `ManagedAdBlockEngine` (fallback thuần C#) và lớp P/Invoke `NativeRustAdBlockEngine` (nạp `adblock_rust.dll` nếu tồn tại).
- [x] Tự động tải và cache bộ lọc `EasyList`, `EasyPrivacy`, `ABPVN` vào `%AppData%\QUANGLNBrowser\filters\`.
- [x] Tích hợp `SmartResourceRequestHandler` chặn tầng mạng + tiêm CSS Cosmetic Filtering; kết nối `AdBlockToggle` Badge & Whitelist theo tên miền lưu vào `vault.db`.
- [x] **Nghiệm thu:** Bộ đếm trên Omnibox tăng thực tế khi mở trang báo/web có quảng cáo; bật Whitelist cho domain thì quảng cáo hiển thị lại sau khi reload.

### Giai đoạn 3 — Video Sniffer, Multi-thread Downloader & `libmpv` Player
- [x] Hoàn thiện `MediaSnifferService` bắt MP4/HLS (`.m3u8`)/DASH (`.mpd`) kèm đầy đủ `Referer`, `User-Agent`, `Cookie`.
- [x] Viết `video-overlay.js` (hỗ trợ Shadow DOM) hiển thị nút nổi **Tải xuống** & **Xem riêng** trên khung video.
- [x] Xây dựng `DownloadManager` 3 chế độ (Direct 8 luồng `RandomAccess`, FFmpeg remux `.m3u8`, `yt-dlp` extractor) + `JobObjectManager` quản lý tiến trình con + Side Drawer quản lý tải xuống (Pause/Resume/Open Folder).
- [x] Xây dựng `MpvPlayerWindow` (`HwndHost` + `libmpv-2.dll`) hỗ trợ phát luồng trực tiếp có Header/Cookie, kéo thả phụ đề, Audio Boost 200%, Mini-mode `Topmost`.
- [x] **Nghiệm thu:** Bắt được video trên web, bấm **Xem riêng** phát mượt trên cửa sổ `libmpv`, bấm **Tải xuống** ghép ra file `.mp4` hoàn chỉnh; đóng nóng app không để lại `ffmpeg.exe` hay `yt-dlp.exe` trong Task Manager.

### Giai đoạn 4 — Built-in VPN (`sing-box`), Per-Tab Routing & Hoàn thiện
- [x] Xây dựng `VpnService` sinh cấu hình `sing-box.exe` chạy ngầm qua `JobObjectManager` (local SOCKS5 `127.0.0.1:10809`).
- [x] Tích hợp `ApplyVpnProxyAsync` (đổi proxy nóng cho toàn trình duyệt hoặc cô lập riêng từng tab bằng `RequestContext`), chống rò rỉ WebRTC và cơ chế Kill Switch tự động.
- [x] Giao diện quản lý/nhập danh sách VPN Node (WireGuard / VLESS / Shadowsocks / SOCKS5 / HTTP Proxy), đo độ trễ (Ping) và Split Tunneling (bypass tên miền nội địa).
- [x] **Nghiệm thu:** Bật VPN đổi IP trình duyệt tức thời không cần restart, kiểm tra WebRTC leak không lộ IP thật, tắt nóng `sing-box.exe` kích hoạt Kill Switch chặn lưu lượng ngay lập tức.
