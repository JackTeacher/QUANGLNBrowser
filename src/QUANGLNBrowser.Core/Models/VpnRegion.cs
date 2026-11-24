namespace QUANGLNBrowser.Core.Models;

/// <summary>
/// Vùng định tuyến ảo kiểu Opera VPN (Tối ưu / Châu Á / Châu Âu / Châu Mỹ).
/// </summary>
public enum VpnRegion
{
    Optimal = 0,
    Asia = 1,
    Europe = 2,
    Americas = 3
}

public static class VpnRegionHelper
{
    private static readonly HashSet<string> AsiaCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SG", "JP", "KR", "HK", "TW", "VN", "TH", "IN", "ID", "MY", "PH", "AU", "NZ", "KH", "LA", "MM", "AS"
    };

    private static readonly HashSet<string> EuropeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DE", "NL", "GB", "UK", "FR", "SE", "CH", "FI", "PL", "IT", "ES", "NO", "IE", "AT", "BE", "CZ", "DK", "PT", "RO", "UA", "EU"
    };

    private static readonly HashSet<string> AmericasCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "CA", "BR", "MX", "AR", "CL", "CO", "PE", "AM"
    };

    private static readonly (string Code, string[] Tokens)[] CountryKeywordTable =
    [
        ("SG", ["🇸🇬", "singapore", "sg-", "_sg", "[sg]", " sg "]),
        ("JP", ["🇯🇵", "japan", "tokyo", "osaka", "jp-", "_jp", "[jp]", " jp "]),
        ("KR", ["🇰🇷", "korea", "seoul", "kr-", "_kr", "[kr]", " kr "]),
        ("HK", ["🇭🇰", "hongkong", "hong kong", "hk-", "_hk", "[hk]", " hk "]),
        ("TW", ["🇹🇼", "taiwan", "taipei", "tw-", "_tw", "[tw]", " tw "]),
        ("VN", ["🇻🇳", "vietnam", "hanoi", "saigon", "hcm", "vn-", "_vn", "[vn]"]),
        ("IN", ["🇮🇳", "india", "mumbai", "in-", "_in", "[in]"]),
        ("AU", ["🇦🇺", "australia", "sydney", "au-", "_au", "[au]"]),
        ("DE", ["🇩🇪", "germany", "frankfurt", "berlin", "de-", "_de", "[de]", " de "]),
        ("NL", ["🇳🇱", "netherlands", "amsterdam", "nl-", "_nl", "[nl]", " nl "]),
        ("GB", ["🇬🇧", "uk", "united kingdom", "london", "england", "gb-", "_gb", "[gb]", "[uk]"]),
        ("FR", ["🇫🇷", "france", "paris", "fr-", "_fr", "[fr]", " fr "]),
        ("SE", ["🇸🇪", "sweden", "stockholm", "se-", "_se", "[se]"]),
        ("CH", ["🇨🇭", "switzerland", "zurich", "ch-", "_ch", "[ch]"]),
        ("FI", ["🇫🇮", "finland", "helsinki", "fi-", "_fi", "[fi]"]),
        ("US", ["🇺🇸", "usa", "united states", "america", "new york", "newyork", "los angeles", "seattle", "dallas", "chicago", "miami", "silicon", "us-", "_us", "[us]", " us "]),
        ("CA", ["🇨🇦", "canada", "toronto", "montreal", "vancouver", "ca-", "_ca", "[ca]"]),
        ("BR", ["🇧🇷", "brazil", "sao paulo", "br-", "_br", "[br]"]),
        ("CF", ["warp", "cloudflare", "1.1.1.1"])
    ];

    public static VpnRegion ClassifyCountryCode(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return VpnRegion.Optimal;
        }

        string code = countryCode.Trim().ToUpperInvariant();
        if (code == "CF" || code == "OPT" || code == "AUTO")
        {
            return VpnRegion.Optimal;
        }

        if (AsiaCodes.Contains(code))
        {
            return VpnRegion.Asia;
        }

        if (EuropeCodes.Contains(code))
        {
            return VpnRegion.Europe;
        }

        if (AmericasCodes.Contains(code))
        {
            return VpnRegion.Americas;
        }

        return VpnRegion.Optimal;
    }

    public static string GetDisplayName(VpnRegion region) => region switch
    {
        VpnRegion.Asia => "Châu Á",
        VpnRegion.Europe => "Châu Âu",
        VpnRegion.Americas => "Châu Mỹ",
        _ => "Tối ưu (WARP)"
    };

    public static string GetShortCode(VpnRegion region) => region switch
    {
        VpnRegion.Asia => "ASIA",
        VpnRegion.Europe => "EU",
        VpnRegion.Americas => "AM",
        _ => "WARP"
    };

    public static string? TryDetectCountryCodeFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string padded = $" {text.Trim().ToLowerInvariant()} ";
        foreach (var (code, tokens) in CountryKeywordTable)
        {
            foreach (string token in tokens)
            {
                if (padded.Contains(token, StringComparison.Ordinal))
                {
                    return code;
                }
            }
        }

        return null;
    }
}
