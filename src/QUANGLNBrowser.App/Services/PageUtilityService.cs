using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace QUANGLNBrowser.App.Services;

public sealed record PageSummaryResult(
    string Title,
    string Url,
    int WordCount,
    int EstimatedReadMinutes,
    IReadOnlyList<string> KeyPoints,
    string FormattedSummary,
    string AiPrompt);

/// <summary>
/// Các tiện ích chuột phải: Tạo mã QR (QRCoder), Tóm tắt nội dung trang web, và Chụp ảnh màn hình để dịch.
/// </summary>
public static class PageUtilityService
{
    private static readonly Regex SentenceSplitRegex = new(@"(?<=[\.\!\?…])\s+(?=[\p{L}\p{N}""“‘])", RegexOptions.Compiled);
    private static readonly Regex WordTokenRegex = new(@"[\p{L}\p{N}]{2,}", RegexOptions.Compiled);

    public static byte[] GenerateQrPngBytes(string content, int pixelsPerModule = 10)
    {
        string safeContent = string.IsNullOrWhiteSpace(content) ? "https://www.google.com" : content.Trim();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(safeContent, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(data);
        return qrCode.GetGraphic(pixelsPerModule);
    }

    public static BitmapImage CreateQrBitmap(string content, int pixelsPerModule = 10)
    {
        byte[] pngBytes = GenerateQrPngBytes(content, pixelsPerModule);
        using var ms = new MemoryStream(pngBytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static PageSummaryResult SummarizePageText(string title, string url, string rawText)
    {
        string cleanTitle = string.IsNullOrWhiteSpace(title) ? url : title.Trim();
        string cleaned = Regex.Replace(rawText ?? string.Empty, @"\s+", " ").Trim();

        int wordCount = WordTokenRegex.Matches(cleaned).Count;
        int readMinutes = Math.Max(1, (int)Math.Ceiling(wordCount / 220.0));

        if (string.IsNullOrWhiteSpace(cleaned) || wordCount < 15)
        {
            var fallbackBullets = new[] { "Trang hiện tại chưa có đủ văn bản bài viết để tóm tắt tự động (có thể là trang media, ứng dụng web hoặc trang đang tải)." };
            string prompt = $"Hãy tóm tắt ngắn gọn bằng tiếng Việt các ý chính của trang web này:\nTiêu đề: {cleanTitle}\nURL: {url}";
            return new PageSummaryResult(cleanTitle, url, wordCount, readMinutes, fallbackBullets, fallbackBullets[0], prompt);
        }

        // Xây dựng bảng tần suất từ khóa + ưu tiên từ khóa trong tiêu đề để chọn các câu cốt lõi nhất
        var titleTerms = new HashSet<string>(
            WordTokenRegex.Matches(cleanTitle.ToLowerInvariant()).Select(m => m.Value),
            StringComparer.OrdinalIgnoreCase);

        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in WordTokenRegex.Matches(cleaned.ToLowerInvariant()))
        {
            string token = m.Value;
            if (token.Length <= 2) continue;
            freq[token] = freq.TryGetValue(token, out int c) ? c + 1 : 1;
        }

        string[] rawSentences = SentenceSplitRegex.Split(cleaned);
        var scored = new List<(int Index, double Score, string Text)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rawSentences.Length; i++)
        {
            string s = rawSentences[i].Trim();
            if (s.Length < 40 || s.Length > 340 || !seen.Add(s))
            {
                continue;
            }

            var tokens = WordTokenRegex.Matches(s.ToLowerInvariant());
            if (tokens.Count < 7)
            {
                continue;
            }

            double score = 0;
            foreach (Match tok in tokens)
            {
                string w = tok.Value;
                if (freq.TryGetValue(w, out int f))
                {
                    score += Math.Min(f, 8);
                }
                if (titleTerms.Contains(w))
                {
                    score += 6.0;
                }
            }

            // Chuẩn hóa theo độ dài câu + cộng điểm cho đoạn mở đầu bài viết
            score /= Math.Sqrt(tokens.Count);
            if (i < 4)
            {
                score *= 1.25;
            }

            scored.Add((i, score, s));
        }

        var topPoints = scored
            .OrderByDescending(x => x.Score)
            .Take(5)
            .OrderBy(x => x.Index)
            .Select(x => x.Text)
            .ToList();

        if (topPoints.Count == 0)
        {
            topPoints.Add(cleaned.Length > 360 ? cleaned[..360] + "..." : cleaned);
        }

        string formatted = string.Join(Environment.NewLine + Environment.NewLine, topPoints.Select(p => $"• {p}"));
        string excerpt = cleaned.Length > 3500 ? cleaned[..3500] : cleaned;
        string aiPrompt = $"Hãy tóm tắt ngắn gọn, rõ ràng bằng tiếng Việt các ý chính của trang web sau:\nTiêu đề: {cleanTitle}\nURL: {url}\n\nNội dung trích xuất:\n{excerpt}";

        return new PageSummaryResult(cleanTitle, url, wordCount, readMinutes, topPoints, formatted, aiPrompt);
    }

    public static (string SavedPath, BitmapSource? Snapshot) CaptureElementScreenshotToPngAndClipboard(FrameworkElement element)
    {
        int width = Math.Max(1, (int)Math.Round(element.ActualWidth));
        int height = Math.Max(1, (int)Math.Round(element.ActualHeight));

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        rtb.Freeze();

        string picturesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "QUANGLNBrowser");
        Directory.CreateDirectory(picturesDir);

        string filePath = Path.Combine(picturesDir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(filePath))
        {
            encoder.Save(fs);
        }

        try
        {
            Clipboard.SetImage(rtb);
        }
        catch
        {
            // Bỏ qua nếu clipboard tạm thời bị khóa bởi tiến trình khác
        }

        return (filePath, rtb);
    }
}
