---
description: Triển khai tuần tự một giai đoạn (Phase 1–4) của dự án QUANGLN Browser theo QUANGLN_BROWSER_SPEC.md
---

# Workflow: Triển khai Giai đoạn QUANGLN Browser (`/implement-phase`)

Khi người dùng gọi `/implement-phase <số_giai_đoạn>` (ví dụ `/implement-phase 1`), thực hiện nghiêm ngặt 6 bước sau:

1. **Đọc đặc tả & kiểm tra hiện trạng:**
   - Đọc `@QUANGLN_BROWSER_SPEC.md` (đặc biệt là mục Lộ trình triển khai ứng với giai đoạn được yêu cầu) và `@Directory.Build.props`.
   - Kiểm tra cấu trúc thư mục `src/` và kiểm tra xem các binary trong `native/win-x64/` đã có hay chưa.

2. **Lập kế hoạch chi tiết trước khi viết code:**
   - Liệt kê danh sách file sẽ tạo mới hoặc chỉnh sửa trong giai đoạn này.
   - Nêu rõ các gói NuGet cần cài đặt (đúng chuẩn `.NET 8`, `WPF-UI v3`, `CefSharp.Wpf.NETCore`, `SQLitePCLRaw.bundle_e_sqlcipher`).
   - Đảm bảo không vi phạm bất kỳ quy tắc nào trong `.agents/rules/quangln-browser.md`.

3. **Khởi tạo / Cập nhật Solution & Projects (nếu là Giai đoạn 1):**
   - Tạo `QUANGLNBrowser.sln` và 6 project trong `src/` theo đúng sơ đồ kiến trúc.
   - Đảm bảo mọi `.csproj` kế thừa `Directory.Build.props` (không lặp lại `<TargetFramework>` hoặc `<PlatformTarget>` gây xung đột).

4. **Triển khai mã nguồn sạch & hoàn chỉnh:**
   - Viết đầy đủ code (không để `TODO` hoặc `NotImplementedException` cho các tính năng cốt lõi của giai đoạn hiện tại).
   - Áp dụng đúng MVVM, Async/Await (không block UI thread), và giải phóng tài nguyên (`IDisposable`) đầy đủ cho CefSharp, SQLite, JobObject và `libmpv`.

5. **Biên dịch & Kiểm thử tự động:**
   - Chạy lệnh: `dotnet build QUANGLNBrowser.sln -p:Platform=x64`
   - Nếu có lỗi biên dịch hoặc cảnh báo nghiêm trọng, tự sửa và build lại đến khi đạt **0 Errors**.
   - Nếu giai đoạn có Unit Tests (ví dụ Argon2id, SQLCipher Vault, AdBlock matcher, URL/HLS parser), viết và chạy `dotnet test -p:Platform=x64`.

6. **Nghiệm thu & Cập nhật tiến độ:**
   - Đối chiếu từng gạch đầu dòng trong mục **Tiêu chí nghiệm thu (Acceptance Criteria)** của giai đoạn trong `QUANGLN_BROWSER_SPEC.md`.
   - Đánh dấu `[x]` các hạng mục đã hoàn thành trong `QUANGLN_BROWSER_SPEC.md` và báo cáo ngắn gọn cho người dùng kèm hướng dẫn chạy thử (`dotnet run --project src/QUANGLNBrowser.App -p:Platform=x64`).
