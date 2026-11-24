using System.IO;
using System.Windows;
using System.Windows.Media;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;
using Wpf.Ui.Appearance;

namespace QUANGLNBrowser.App.Services;

/// <summary>
/// Quản lý chủ đề màu Neon toàn ứng dụng (WPF Chrome, Sidebar, Tabs, LockScreen, ContextMenu và Trang chủ HTML).
/// </summary>
public static class BrowserThemeManager
{
    private static readonly string ThemePrefFile = Path.Combine(AppPaths.AppDataRoot, "ui_theme.txt");

    public static BrowserThemePreset CurrentPreset { get; private set; } = BrowserThemeCatalog.Resolve("coccoc");
    public static string CurrentThemeKey => CurrentPreset.Key;

    public static event EventHandler<BrowserThemePreset>? ThemeChanged;

    public static void Initialize()
    {
        string savedKey = "coccoc";
        try
        {
            if (File.Exists(ThemePrefFile))
            {
                string raw = File.ReadAllText(ThemePrefFile).Trim();
                if (!string.IsNullOrEmpty(raw))
                {
                    savedKey = raw;
                }
            }
        }
        catch
        {
            // Giữ mặc định nếu chưa đọc được
        }

        ApplyTheme(savedKey, persistToStartupFile: false);
    }

    public static void ApplyTheme(string? themeKey, bool persistToStartupFile = true)
    {
        var preset = BrowserThemeCatalog.Resolve(themeKey);
        CurrentPreset = preset;

        if (persistToStartupFile)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ThemePrefFile)!);
                File.WriteAllText(ThemePrefFile, preset.Key);
            }
            catch
            {
                // Bỏ qua lỗi I/O không nghiêm trọng
            }
        }

        void ApplyOnUi()
        {
            var res = Application.Current?.Resources;
            if (res is null)
            {
                return;
            }

            var accent = Color.FromRgb(preset.AccentR, preset.AccentG, preset.AccentB);
            res["ThemeAccentColor"] = accent;
            var accentSoft = Color.FromArgb(0x26, preset.AccentR, preset.AccentG, preset.AccentB);
            var accentBorder = Color.FromArgb(0x90, preset.AccentR, preset.AccentG, preset.AccentB);
            var accentGlow = Color.FromArgb(0x48, preset.AccentR, preset.AccentG, preset.AccentB);

            res["ThemeAccentBrush"] = Freeze(new SolidColorBrush(accent));
            res["ThemeAccentSoftBrush"] = Freeze(new SolidColorBrush(accentSoft));
            res["ThemeAccentBorderBrush"] = Freeze(new SolidColorBrush(accentBorder));
            res["ThemeAccentGlowBrush"] = Freeze(new SolidColorBrush(accentGlow));

            res["ThemeTabIdleBrush"] = Freeze(new SolidColorBrush(ParseColor(preset.TabIdleHex)));
            res["ThemeTabActiveBrush"] = Freeze(new SolidColorBrush(ParseColor(preset.TabActiveHex)));

            res["BrowserSurfaceBrush"] = Freeze(new SolidColorBrush(ParseColor(preset.SurfaceHex)));
            res["BrowserChromeBrush"] = Freeze(new SolidColorBrush(ParseColor(preset.ChromeHex)));
            res["BrowserElevatedBrush"] = Freeze(new SolidColorBrush(ParseColor(preset.ElevatedHex)));
            res["BrowserBorderBrush"] = Freeze(new SolidColorBrush(ParseColor(preset.BorderHex)));

            var dividerBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            dividerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, preset.AccentR, preset.AccentG, preset.AccentB), 0.0));
            dividerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0xAA, preset.AccentR, preset.AccentG, preset.AccentB), 0.5));
            dividerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x33, preset.AccentR, preset.AccentG, preset.AccentB), 1.0));
            dividerBrush.Freeze();
            res["ThemeNeonDividerBrush"] = dividerBrush;

            try
            {
                ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Dark);
            }
            catch
            {
                // Bỏ qua nếu WPF-UI resource dictionary chưa sẵn sàng ở thời điểm rất sớm
            }

            ThemeChanged?.Invoke(null, preset);
        }

        if (Application.Current is null)
        {
            return;
        }

        if (Application.Current.Dispatcher.CheckAccess())
        {
            ApplyOnUi();
        }
        else
        {
            Application.Current.Dispatcher.InvokeAsync(ApplyOnUi);
        }
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
