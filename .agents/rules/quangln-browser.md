---
trigger: always_on
---

# QUANGLN Browser — Quy tắc kỹ thuật bắt buộc (Antigravity Rules)

Bạn đang phát triển **QUANGLN Browser**, ứng dụng trình duyệt desktop Windows dùng **.NET 8 (WPF)** + **CefSharp (x64)**. Trong mọi phiên làm việc, bạn PHẢI tuân thủ tuyệt đối các ràng buộc sau:

## 1. Quy ước tên & Kiến trúc Solution
- **Tên ứng dụng:** `QUANGLN Browser`
- **Root Namespace:** `QUANGLNBrowser` (các project: `QUANGLNBrowser.App`, `QUANGLNBrowser.Engine`, `QUANGLNBrowser.Security`, `QUANGLNBrowser.Network`, `QUANGLNBrowser.Media`, `QUANGLNBrowser.Core`).
- **Đặc tả nguồn chân lý (Source of Truth):** Luôn đối chiếu file `QUANGLN_BROWSER_SPEC.md` ở thư mục gốc trước khi thêm tính năng hoặc thay đổi cấu trúc.
- **Kiến trúc UI:** Bắt buộc áp dụng MVVM với `CommunityToolkit.Mvvm` và giao diện **WPF-UI v3 (`Wpf.Ui`)** Dark Mode (Mica backdrop). Không nhét business logic vào code-behind `.xaml.cs` ngoại trừ khởi tạo Control / `HwndHost` / Window lifecycle.

## 2. Ràng buộc nền tảng & Build (`x64` Only)
- Toàn bộ Solution sử dụng `Directory.Build.props` ép cứng **`PlatformTarget=x64`**. Tuyệt đối KHÔNG chuyển sang `AnyCPU` hay `x86`.
- `QUANGLNBrowser.Core` thuần `net8.0` (không tham chiếu WPF hay CefSharp).
- Các project còn lại dùng `net8.0-windows10.0.19041.0` để hỗ trợ Windows Hello (`UserConsentVerifier`).
- Đường dẫn binary khi chạy thực tế:
  - Sidecar `.exe` (`sing-box.exe`, `ffmpeg.exe`, `ffprobe.exe`, `yt-dlp.exe`): `Path.Combine(AppContext.BaseDirectory, "native", "<file>.exe")`
  - Native `.dll` (`libmpv-2.dll`, `adblock_rust.dll`): nằm trực tiếp tại `AppContext.BaseDirectory`.

## 3. Bảo mật & Quản lý tiến trình (Zero Leaks)
- **Mọi tiến trình con** (`sing-box.exe`, `ffmpeg.exe`, `yt-dlp.exe`) BẮT BUỘC chạy không cửa sổ (`CreateNoWindow = true`, `UseShellExecute = false`) và phải được gán vào **Win32 Job Object** với cờ `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` để không bao giờ để lại zombie process khi app crash.
- **Mã hóa dữ liệu:** Lịch sử, Bookmarks, Downloads, VPN Nodes phải lưu trong SQLite mã hóa bằng **SQLCipher (`AES-256`)** với khóa dẫn xuất từ **Argon2id**. Tuyệt đối không lưu Master Password, PIN hay khóa giải mã dạng plain-text lên đĩa hoặc trong log.
- **Xóa mật khẩu khỏi bộ nhớ:** Ngay sau khi xác thực xong ở `LockScreenViewModel`, phải lập tức xóa `PasswordInput = string.Empty` và `CryptographicOperations.ZeroMemory` các mảng byte nhạy cảm.
- **Chống rò rỉ VPN:** Khi bật VPN trong CefSharp, BẮT BUỘC đặt `webrtc.ip_handling_policy = "disable_non_proxied_udp"` và kích hoạt Kill Switch (chuyển proxy sang blackhole `127.0.0.1:1` nếu `sing-box.exe` rớt đột ngột).

## 4. Chiến lược AdBlock Fallback
- Luôn lập trình dựa trên interface `IAdBlockEngine` (trong `QUANGLNBrowser.Core`).
- Nếu máy chưa biên dịch `adblock_rust.dll` vào `native/win-x64`, sử dụng lớp cài đặt fallback thuần C# (`ManagedAdBlockEngine`) hỗ trợ nạp danh sách EasyList/EasyPrivacy/ABPVN (host blocking + URL rule matching) để không làm chặn tiến độ build/test; tự động ưu tiên nạp `NativeRustAdBlockEngine` khi phát hiện `adblock_rust.dll`.

## 5. Quy tắc kiểm thử trước khi báo hoàn thành
- Sau mỗi thay đổi code, chạy `dotnet build QUANGLNBrowser.sln -p:Platform=x64` và đảm bảo **0 Errors**.
- Nếu có unit test, chạy `dotnet test -p:Platform=x64` và xác nhận tất cả test đều Pass trước khi kết thúc tác vụ.
