using System.IO;
using System.Net;
using QUANGLNBrowser.Core.Services;

namespace QUANGLNBrowser.App.Services;

/// <summary>
/// Tạo trang khởi đầu Dark Mode sạch, không theo dõi cho Cửa sổ Ẩn danh &amp; Cửa sổ Ẩn danh với 1.1.1.1 WARP.
/// </summary>
public static class IncognitoStartPageBuilder
{
    private static readonly string UiDir = Path.Combine(AppPaths.AppDataRoot, "ui");

    public static string GetStartPagePath(bool withWarp) =>
        Path.Combine(UiDir, withWarp ? "incognito_warp.html" : "incognito.html");

    public static string GetStartPageUri(bool withWarp) =>
        new Uri(GetStartPagePath(withWarp)).AbsoluteUri;

    public static bool IsInternalStartUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string warpUri = GetStartPageUri(true);
        string plainUri = GetStartPageUri(false);
        return url.Equals(warpUri, StringComparison.OrdinalIgnoreCase) ||
               url.Equals(plainUri, StringComparison.OrdinalIgnoreCase);
    }

    public static string EnsureStartPageFile(
        bool withWarp,
        bool isWarpConnected,
        string? virtualIp = null,
        string? popLocation = null)
    {
        Directory.CreateDirectory(UiDir);
        string path = GetStartPagePath(withWarp);

        string badgeColor = withWarp ? "#10B981" : "#A855F7";
        string badgeBg = withWarp ? "rgba(16, 185, 129, 0.14)" : "rgba(168, 85, 247, 0.16)";
        string badgeText = withWarp
            ? (isWarpConnected ? "ẨN DANH · 1.1.1.1 WARP ĐANG BẬT" : "ẨN DANH · ĐANG KẾT NỐI 1.1.1.1 WARP")
            : "CHẾ ĐỘ ẨN DANH CÔ LẬP";

        string heading = withWarp
            ? "Duyệt web Ẩn danh qua Cloudflare 1.1.1.1 WARP"
            : "Bạn đã chuyển sang Cửa sổ Ẩn danh";

        string ipText = string.IsNullOrWhiteSpace(virtualIp) || virtualIp.Contains(' ')
            ? (isWarpConnected ? "Đang ẩn IP thật (1.1.1.1 WARP)" : "Đang thiết lập đường hầm WireGuard...")
            : virtualIp;
        string popText = string.IsNullOrWhiteSpace(popLocation) || popLocation == "—" ? "Cloudflare Edge" : popLocation;

        string safeIp = WebUtility.HtmlEncode(ipText);
        string safePop = WebUtility.HtmlEncode(popText);
        string safeTitle = withWarp ? "Ẩn danh · 1.1.1.1 WARP" : "Tab Ẩn danh mới";

        string warpCardHtml = withWarp
            ? $"""
               <div class="status-grid">
                 <div class="stat-box">
                   <div class="stat-label">Đường hầm bảo vệ</div>
                   <div class="stat-value ok">{(isWarpConnected ? "WARP WireGuard ON" : "Đang kết nối...")}</div>
                 </div>
                 <div class="stat-box">
                   <div class="stat-label">Địa chỉ IP ảo</div>
                   <div class="stat-value cyan">{safeIp}</div>
                 </div>
                 <div class="stat-box">
                   <div class="stat-label">Cụm máy chủ (PoP)</div>
                   <div class="stat-value">{safePop}</div>
                 </div>
                 <div class="stat-box">
                   <div class="stat-label">Chống WebRTC Leak</div>
                   <div class="stat-value ok">Đã khóa UDP</div>
                 </div>
               </div>
               <div class="actions">
                 <a class="btn" href="https://1.1.1.1/cdn-cgi/trace">🔍 Kiểm tra 1.1.1.1/cdn-cgi/trace</a>
                 <a class="btn" href="https://browserleaks.com/webrtc">🛡 Kiểm tra WebRTC Leak</a>
               </div>
               """
            : """
              <div class="actions">
                <a class="btn" href="https://duckduckgo.com">🦆 Tìm kiếm riêng tư DuckDuckGo</a>
                <a class="btn" href="https://www.google.com">🌐 Mở Google</a>
              </div>
              """;

        string html = $$"""
            <!DOCTYPE html>
            <html lang="vi">
            <head>
              <meta charset="utf-8" />
              <title>{{safeTitle}}</title>
              <style>
                * { box-sizing: border-box; }
                html, body {
                  margin: 0;
                  padding: 0;
                  min-height: 100vh;
                  background: radial-gradient(circle at top, #1b2234 0%, #121218 65%);
                  color: #f3f4f6;
                  font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
                  display: flex;
                  align-items: center;
                  justify-content: center;
                }
                .container {
                  width: 100%;
                  max-width: 720px;
                  padding: 36px 28px;
                  text-align: center;
                }
                .pill {
                  display: inline-block;
                  padding: 6px 14px;
                  border-radius: 999px;
                  font-size: 12px;
                  font-weight: 700;
                  letter-spacing: 0.5px;
                  color: {{badgeColor}};
                  background: {{badgeBg}};
                  border: 1px solid {{badgeColor}};
                  margin-bottom: 16px;
                }
                h1 {
                  font-size: 26px;
                  font-weight: 700;
                  margin: 0 0 10px 0;
                  color: #ffffff;
                }
                p.desc {
                  color: #9ca3af;
                  font-size: 14px;
                  line-height: 1.6;
                  margin: 0 0 24px 0;
                }
                .search-form {
                  display: flex;
                  gap: 8px;
                  background: #1e202b;
                  border: 1px solid #35384a;
                  border-radius: 12px;
                  padding: 6px 8px 6px 14px;
                  margin-bottom: 22px;
                }
                .search-form input {
                  flex: 1;
                  background: transparent;
                  border: none;
                  outline: none;
                  color: #ffffff;
                  font-size: 14px;
                }
                .search-form button {
                  background: {{badgeColor}};
                  color: #0b1117;
                  font-weight: 700;
                  border: none;
                  border-radius: 8px;
                  padding: 8px 16px;
                  cursor: pointer;
                  font-size: 13px;
                }
                .status-grid {
                  display: grid;
                  grid-template-columns: repeat(2, minmax(0, 1fr));
                  gap: 10px;
                  margin-bottom: 18px;
                  text-align: left;
                }
                .stat-box {
                  background: rgba(255, 255, 255, 0.04);
                  border: 1px solid #2b2f3f;
                  border-radius: 10px;
                  padding: 12px 14px;
                }
                .stat-label { font-size: 11px; color: #94a3b8; margin-bottom: 4px; }
                .stat-value { font-size: 14px; font-weight: 700; color: #f8fafc; }
                .stat-value.ok { color: #34d399; }
                .stat-value.cyan { color: #38bdf8; }
                .actions {
                  display: flex;
                  flex-wrap: wrap;
                  justify-content: center;
                  gap: 10px;
                }
                .btn {
                  text-decoration: none;
                  color: #e5e7eb;
                  background: #232634;
                  border: 1px solid #383c50;
                  padding: 9px 14px;
                  border-radius: 9px;
                  font-size: 12.5px;
                  font-weight: 600;
                }
                .btn:hover { background: #2d3144; border-color: #60a5fa; }
              </style>
            </head>
            <body>
              <div class="container">
                <div class="pill">{{badgeText}}</div>
                <h1>{{heading}}</h1>
                <p class="desc">
                  QUANGLN Browser không lưu lịch sử duyệt web, cookie hay bộ nhớ đệm xuống đĩa sau khi bạn đóng cửa sổ này.
                </p>
                <form class="search-form" action="https://duckduckgo.com/" method="get">
                  <input type="text" name="q" placeholder="Tìm kiếm riêng tư trên DuckDuckGo hoặc nhập URL lên thanh địa chỉ..." autofocus />
                  <button type="submit">Tìm kiếm</button>
                </form>
                {{warpCardHtml}}
              </div>
            </body>
            </html>
            """;

        File.WriteAllText(path, html, System.Text.Encoding.UTF8);
        return new Uri(path).AbsoluteUri;
    }
}
