using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace QUANGLNBrowser.Core.Services;

public sealed record WeatherSnapshot(
    string TemperatureText,
    string CityName,
    string ConditionAndHumidityText,
    string IconGlyph,
    string ConditionText = "Nắng nhẹ");

/// <summary>
/// Tính toán lịch Âm Việt Nam (ChineseLunisolarCalendar) và lấy thời tiết trực tiếp cho màn hình khóa Vault &amp; Trang chủ.
/// </summary>
public static class VietnameseCalendarService
{
    private static readonly ChineseLunisolarCalendar LunarCalendar = new();
    private static readonly HttpClient SharedWeatherClient = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static WeatherSnapshot _latestWeather = new("31°C", "TP. Hồ Chí Minh", "Nắng nhẹ · Độ ẩm 68%", "⛅", "Nắng nhẹ");
    private static DateTime _latestWeatherFetchedUtc = DateTime.MinValue;

    public static WeatherSnapshot LatestWeather => _latestWeather;

    public static string GetVietnameseDayOfWeek(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Sunday => "Chủ Nhật",
        DayOfWeek.Monday => "Thứ Hai",
        DayOfWeek.Tuesday => "Thứ Ba",
        DayOfWeek.Wednesday => "Thứ Tư",
        DayOfWeek.Thursday => "Thứ Năm",
        DayOfWeek.Friday => "Thứ Sáu",
        DayOfWeek.Saturday => "Thứ Bảy",
        _ => string.Empty
    };

    public static string FormatSolarDate(DateTime date) =>
        $"{GetVietnameseDayOfWeek(date.DayOfWeek)}, {date:dd/MM/yyyy}";

    public static (int Day, int Month, bool IsLeapMonth) GetLunarDate(DateTime date)
    {
        DateTime clamped = date;
        if (clamped < LunarCalendar.MinSupportedDateTime)
        {
            clamped = LunarCalendar.MinSupportedDateTime;
        }
        else if (clamped > LunarCalendar.MaxSupportedDateTime)
        {
            clamped = LunarCalendar.MaxSupportedDateTime;
        }

        int lunarYear = LunarCalendar.GetYear(clamped);
        int rawMonth = LunarCalendar.GetMonth(clamped);
        int leapMonth = LunarCalendar.GetLeapMonth(lunarYear);
        bool isLeap = leapMonth > 0 && rawMonth == leapMonth;
        int displayMonth = leapMonth > 0 && rawMonth >= leapMonth ? rawMonth - 1 : rawMonth;
        int lunarDay = LunarCalendar.GetDayOfMonth(clamped);

        return (lunarDay, displayMonth, isLeap);
    }

    public static string FormatLunarDateBadge(DateTime date)
    {
        var (day, month, isLeap) = GetLunarDate(date);
        return isLeap
            ? $"{day:00}/{month:00} (Nhuận) Âm lịch"
            : $"{day:00}/{month:00} Âm lịch";
    }

    public static string FormatHomeLunarText(DateTime date)
    {
        var (day, month, isLeap) = GetLunarDate(date);
        int lunarYear = LunarCalendar.GetYear(
            date < LunarCalendar.MinSupportedDateTime ? LunarCalendar.MinSupportedDateTime :
            date > LunarCalendar.MaxSupportedDateTime ? LunarCalendar.MaxSupportedDateTime : date);

        string[] can = ["Canh", "Tân", "Nhâm", "Quý", "Giáp", "Ất", "Bính", "Đinh", "Mậu", "Kỷ"];
        string[] chi = ["Thân", "Dậu", "Tuất", "Hợi", "Tý", "Sửu", "Dần", "Mão", "Thìn", "Tỵ", "Ngọ", "Mùi"];
        string canChi = $"{can[lunarYear % 10]} {chi[lunarYear % 12]}";
        string leapTag = isLeap ? " Nhuận" : string.Empty;
        return $"Âm lịch: {day:00}/{month:00}{leapTag} {canChi}";
    }

    public static (string ConditionText, string IconGlyph) MapWmoWeatherCode(int wmoCode) => wmoCode switch
    {
        0 => ("Trời quang", "☀️"),
        1 or 2 => ("Nắng nhẹ", "⛅"),
        3 => ("Nhiều mây", "☁️"),
        45 or 48 => ("Sương mù", "🌫️"),
        >= 51 and <= 67 => ("Có mưa", "🌧️"),
        >= 71 and <= 77 => ("Mưa tuyết", "❄️"),
        >= 80 and <= 82 => ("Mưa rào", "🌦️"),
        >= 95 and <= 99 => ("Giông bão", "⛈️"),
        _ => ("Nắng nhẹ", "⛅")
    };

    public static Task<WeatherSnapshot> GetCachedOrFetchLiveWeatherAsync(
        TimeSpan? maxAge = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan ttl = maxAge ?? TimeSpan.FromMinutes(5);
        if (DateTime.UtcNow - _latestWeatherFetchedUtc < ttl)
        {
            return Task.FromResult(_latestWeather);
        }

        return FetchLiveWeatherAsync(null, cancellationToken);
    }

    public static async Task<WeatherSnapshot> FetchLiveWeatherAsync(
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = httpClient ?? SharedWeatherClient;
            const string url =
                "https://api.open-meteo.com/v1/forecast?latitude=10.8231&longitude=106.6297&current=temperature_2m,relative_humidity_2m,weather_code&timezone=Asia%2FHo_Chi_Minh";

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return _latestWeather;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("current", out var current))
            {
                return _latestWeather;
            }

            int temp = current.TryGetProperty("temperature_2m", out var tEl)
                ? (int)Math.Round(tEl.GetDouble())
                : 31;
            int humidity = current.TryGetProperty("relative_humidity_2m", out var hEl)
                ? (int)Math.Round(hEl.GetDouble())
                : 68;
            int code = current.TryGetProperty("weather_code", out var cEl)
                ? cEl.GetInt32()
                : 1;

            var (condition, icon) = MapWmoWeatherCode(code);
            var snapshot = new WeatherSnapshot(
                $"{temp}°C",
                "TP. Hồ Chí Minh",
                $"{condition} · Độ ẩm {humidity}%",
                icon,
                condition);

            _latestWeather = snapshot;
            _latestWeatherFetchedUtc = DateTime.UtcNow;
            return snapshot;
        }
        catch
        {
            return _latestWeather;
        }
    }
}
