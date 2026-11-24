using System.IO;

namespace QUANGLNBrowser.App.Services;

/// <summary>
/// Quản lý đường dẫn trang chủ nội bộ (Assets\home.html) của QUANGLN Browser.
/// </summary>
public static class HomePageBuilder
{
    public static string GetHomePagePath()
    {
        string outputCandidate = Path.Combine(AppContext.BaseDirectory, "Assets", "home.html");
        if (File.Exists(outputCandidate))
        {
            return outputCandidate;
        }

        // Fallback khi chạy từ thư mục nguồn dev
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            string devCandidate = Path.Combine(dir.FullName, "src", "QUANGLNBrowser.App", "Assets", "home.html");
            if (File.Exists(devCandidate))
            {
                return devCandidate;
            }
        }

        return outputCandidate;
    }

    public static string GetHomePageUri()
    {
        string path = GetHomePagePath();
        return File.Exists(path) ? new Uri(path).AbsoluteUri : "https://www.google.com";
    }

    public static bool IsHomeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string homeUri = GetHomePageUri();
        if (url.Equals(homeUri, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
               url.EndsWith("/Assets/home.html", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInternalBrowserPage(string? url) =>
        IsHomeUrl(url) || IncognitoStartPageBuilder.IsInternalStartUrl(url);
}
