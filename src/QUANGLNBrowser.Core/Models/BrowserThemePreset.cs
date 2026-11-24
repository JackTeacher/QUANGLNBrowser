namespace QUANGLNBrowser.Core.Models;

/// <summary>
/// Bộ 4 chủ đề màu Neon toàn trình duyệt (đồng bộ giữa vỏ ứng dụng WPF và Trang chủ).
/// </summary>
public sealed record BrowserThemePreset(
    string Key,
    string DisplayName,
    string AccentHex,
    byte AccentR,
    byte AccentG,
    byte AccentB,
    string SurfaceHex,
    string ChromeHex,
    string ElevatedHex,
    string TabIdleHex,
    string TabActiveHex,
    string BorderHex);

public static class BrowserThemeCatalog
{
    public static readonly IReadOnlyList<BrowserThemePreset> Presets =
    [
        new BrowserThemePreset(
            Key: "coccoc",
            DisplayName: "Cốc Cốc Neon",
            AccentHex: "#00E676",
            AccentR: 0x00, AccentG: 0xE6, AccentB: 0x76,
            SurfaceHex: "#0B0D12",
            ChromeHex: "#101618",
            ElevatedHex: "#172022",
            TabIdleHex: "#0E1315",
            TabActiveHex: "#182824",
            BorderHex: "#243632"),

        new BrowserThemePreset(
            Key: "crimson",
            DisplayName: "GX Crimson",
            AccentHex: "#FA1E4E",
            AccentR: 0xFA, AccentG: 0x1E, AccentB: 0x4E,
            SurfaceHex: "#0D0A0F",
            ChromeHex: "#180F16",
            ElevatedHex: "#231620",
            TabIdleHex: "#130C12",
            TabActiveHex: "#2D1521",
            BorderHex: "#3D2130"),

        new BrowserThemePreset(
            Key: "cyan",
            DisplayName: "Cyber Cyan",
            AccentHex: "#00E5FF",
            AccentR: 0x00, AccentG: 0xE5, AccentB: 0xFF,
            SurfaceHex: "#090D14",
            ChromeHex: "#0E1520",
            ElevatedHex: "#15202E",
            TabIdleHex: "#0C121B",
            TabActiveHex: "#122736",
            BorderHex: "#21384B"),

        new BrowserThemePreset(
            Key: "purple",
            DisplayName: "Quantum Purple",
            AccentHex: "#B517FF",
            AccentR: 0xB5, AccentG: 0x17, AccentB: 0xFF,
            SurfaceHex: "#0C0914",
            ChromeHex: "#150F21",
            ElevatedHex: "#201730",
            TabIdleHex: "#120D1C",
            TabActiveHex: "#27173D",
            BorderHex: "#392452")
    ];

    public static BrowserThemePreset Resolve(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var match = Presets.FirstOrDefault(p => string.Equals(p.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }
        return Presets[0];
    }

    public static BrowserThemePreset GetNext(string? currentKey)
    {
        var current = Resolve(currentKey);
        int idx = 0;
        for (int i = 0; i < Presets.Count; i++)
        {
            if (Presets[i].Key == current.Key)
            {
                idx = i;
                break;
            }
        }
        return Presets[(idx + 1) % Presets.Count];
    }
}
