namespace QUANGLNBrowser.Core.Services;

/// <summary>
/// Chuẩn hóa chuỗi nhập từ thanh địa chỉ (URL, trang nội bộ quangln://settings hoặc truy vấn tìm kiếm theo công cụ mặc định).
/// </summary>
public static class UrlHelper
{
    public const string SettingsUrlPrefix = "quangln://settings";

    public static string DefaultSearchEngineKey { get; set; } = "google";

    public static bool IsSettingsUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string trimmed = input.Trim();
        return trimmed.StartsWith("quangln://settings", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("coccoc://settings", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("chrome://settings", StringComparison.OrdinalIgnoreCase);
    }

    public static string ExtractSettingsSection(string? input)
    {
        if (!IsSettingsUrl(input))
        {
            return "General";
        }

        string trimmed = input!.Trim();
        int idx = trimmed.IndexOf("settings/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return "General";
        }

        string raw = trimmed[(idx + "settings/".Length)..].Trim().Trim('/');
        return raw.ToLowerInvariant() switch
        {
            "appearance" or "theme" => "Appearance",
            "search" or "searchengine" => "SearchEngine",
            "startup" or "onstartup" => "Startup",
            "vpn" or "network" => "VpnNetwork",
            "adblock" or "ads" => "AdBlock",
            "privacy" or "security" => "Privacy",
            "language" or "languages" or "translate" => "Language",
            "downloads" or "media" => "Downloads",
            "about" or "default" or "defaultbrowser" or "help" => "DefaultAbout",
            _ => "General"
        };
    }

    public static string BuildSearchUrl(string query, string? searchEngineKey = null)
    {
        string encoded = Uri.EscapeDataString(query);
        string key = string.IsNullOrWhiteSpace(searchEngineKey)
            ? DefaultSearchEngineKey
            : searchEngineKey.Trim();

        return key.ToLowerInvariant() switch
        {
            "coccoc" => $"https://coccoc.com/search?query={encoded}",
            "bing" => $"https://www.bing.com/search?q={encoded}",
            "duckduckgo" => $"https://duckduckgo.com/?q={encoded}",
            _ => $"https://www.google.com/search?q={encoded}"
        };
    }

    public static string NormalizeUrlOrSearch(string input) =>
        NormalizeUrlOrSearch(input, searchEngineKey: null);

    public static string NormalizeUrlOrSearch(string input, string? searchEngineKey)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        string trimmed = input.Trim();
        if (IsSettingsUrl(trimmed))
        {
            string section = ExtractSettingsSection(trimmed);
            return section == "General"
                ? SettingsUrlPrefix
                : $"{SettingsUrlPrefix}/{section.ToLowerInvariant()}";
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (!trimmed.Contains(' ') &&
            (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("localhost/", StringComparison.OrdinalIgnoreCase)))
        {
            return $"http://{trimmed}";
        }

        if (!trimmed.Contains(' ') && trimmed.Contains('.'))
        {
            return $"https://{trimmed}";
        }

        return BuildSearchUrl(trimmed, searchEngineKey);
    }
}
