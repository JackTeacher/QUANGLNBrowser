using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace QUANGLNBrowser.Core.Services;

/// <summary>
/// Đại diện một dòng phụ đề song ngữ (Gốc + Tiếng Việt) trong QUANGLN Player AI Studio.
/// </summary>
public sealed class SubtitleSegment
{
    public int Index { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public string Speaker { get; set; } = "Lời thoại";
    public string Gender { get; set; } = "male";
    public string OriginalText { get; set; } = string.Empty;
    public string VietnameseText { get; set; } = string.Empty;
    public string? CustomVietnameseText { get; set; }

    public string EffectiveVietnameseText =>
        !string.IsNullOrWhiteSpace(CustomVietnameseText)
            ? CustomVietnameseText!
            : (!string.IsNullOrWhiteSpace(VietnameseText) ? VietnameseText : OriginalText);
}

/// <summary>
/// Dịch vụ xử lý phụ đề (.SRT, .ASS, .VTT kể cả YouTube auto-captions) và xuất kịch bản song ngữ cho QUANGLN Player.
/// </summary>
public static class SubtitleTranscriptService
{
    // Hỗ trợ cả HH:MM:SS,mmm và MM:SS.mmm (chuẩn WebVTT của YouTube)
    private static readonly Regex TimecodeRegex = new(
        @"(?:(?<sh>\d{1,2}):)?(?<sm>\d{2}):(?<ss>\d{2})[,\.](?<sms>\d{1,3})\s*-->\s*(?:(?<eh>\d{1,2}):)?(?<em>\d{2}):(?<es>\d{2})[,\.](?<ems>\d{1,3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex AssOverrideTagRegex = new(@"\{[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);

    public static List<SubtitleSegment> ParseSubtitleContent(string? rawContent)
    {
        var result = new List<SubtitleSegment>();
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return result;
        }

        string normalized = rawContent.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var blocks = Regex.Split(normalized, @"\n\s*\n");

        foreach (string block in blocks)
        {
            var lines = block
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 &&
                            !l.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) &&
                            !l.StartsWith("Kind:", StringComparison.OrdinalIgnoreCase) &&
                            !l.StartsWith("Language:", StringComparison.OrdinalIgnoreCase) &&
                            !l.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (lines.Count == 0)
            {
                continue;
            }

            int timeLineIndex = lines.FindIndex(l => TimecodeRegex.IsMatch(l));
            if (timeLineIndex < 0)
            {
                continue;
            }

            var match = TimecodeRegex.Match(lines[timeLineIndex]);
            if (!match.Success)
            {
                continue;
            }

            double startSec = ParseTimeParts(
                match.Groups["sh"].Success ? match.Groups["sh"].Value : "0",
                match.Groups["sm"].Value,
                match.Groups["ss"].Value,
                match.Groups["sms"].Value);

            double endSec = ParseTimeParts(
                match.Groups["eh"].Success ? match.Groups["eh"].Value : "0",
                match.Groups["em"].Value,
                match.Groups["es"].Value,
                match.Groups["ems"].Value);

            var payloadLines = lines
                .Skip(timeLineIndex + 1)
                .Select(CleanCaptionLine)
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (payloadLines.Count == 0)
            {
                continue;
            }

            // Nhận diện tên người nói dạng "Vanessa:" ở dòng đầu của YouTube captions
            string? detectedSpeaker = null;
            if (payloadLines.Count >= 2 && payloadLines[0].EndsWith(':') && payloadLines[0].Length <= 24)
            {
                detectedSpeaker = payloadLines[0].TrimEnd(':').Trim();
                payloadLines.RemoveAt(0);
            }

            // Xác định phụ đề song ngữ (SRT có 2 dòng khác ngôn ngữ) hay một đoạn thoại gộp nhiều dòng (YouTube VTT)
            string viLine;
            string origLine;
            bool looksLikeBilingual =
                payloadLines.Count == 2 &&
                ContainsVietnameseDiacritics(payloadLines[0]) &&
                !ContainsVietnameseDiacritics(payloadLines[1]);

            if (looksLikeBilingual)
            {
                viLine = payloadLines[0];
                origLine = payloadLines[1];
            }
            else
            {
                string joined = MultiSpaceRegex.Replace(string.Join(" ", payloadLines), " ").Trim();
                origLine = joined;
                viLine = joined;
            }

            // Khử trùng lặp dòng cuộn (rolling captions) liền kề của YouTube auto-captions
            if (result.Count > 0)
            {
                var prev = result[^1];
                if (string.Equals(prev.OriginalText, origLine, StringComparison.OrdinalIgnoreCase) &&
                    startSec - prev.EndSeconds < 0.8)
                {
                    prev.EndSeconds = Math.Max(prev.EndSeconds, endSec);
                    continue;
                }

                // Bỏ qua cue chuyển tiếp siêu ngắn (< 0.15s) thường xuất hiện trong auto-caption YouTube
                if (endSec - startSec < 0.15 && prev.OriginalText.Contains(origLine, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            int idx = result.Count + 1;
            result.Add(new SubtitleSegment
            {
                Index = idx,
                StartSeconds = startSec,
                EndSeconds = Math.Max(startSec + 0.5, endSec),
                Speaker = !string.IsNullOrWhiteSpace(detectedSpeaker) ? detectedSpeaker! : $"Lời thoại {idx}",
                Gender = idx % 2 == 1 ? "male" : "female",
                OriginalText = origLine,
                VietnameseText = viLine
            });
        }

        if (result.Count == 0 && normalized.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase))
        {
            ParseAssDialogues(normalized, result);
        }

        return result;
    }

    private static string CleanCaptionLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string stripped = HtmlTagRegex.Replace(raw, string.Empty);
        string decoded = WebUtility.HtmlDecode(stripped).Replace('\u00A0', ' ');
        return MultiSpaceRegex.Replace(decoded, " ").Trim();
    }

    private static bool ContainsVietnameseDiacritics(string text)
    {
        foreach (char c in text)
        {
            if ("ăâđêôơưĂÂĐÊÔƠƯáàảãạấầẩẫậắằẳẵặéèẻẽẹếềểễệíìỉĩịóòỏõọốồổỗộớờởỡợúùủũụứừửữựýỳỷỹỵ".IndexOf(c) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static void ParseAssDialogues(string normalized, List<SubtitleSegment> output)
    {
        foreach (string rawLine in normalized.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length < 10)
            {
                continue;
            }

            double startSec = ParseAssTimestamp(parts[1]);
            double endSec = ParseAssTimestamp(parts[2]);
            string speaker = string.IsNullOrWhiteSpace(parts[4]) ? $"Lời thoại {output.Count + 1}" : parts[4].Trim();
            string rawText = string.Join(",", parts.Skip(9));
            string cleaned = AssOverrideTagRegex.Replace(rawText, string.Empty)
                .Replace("\\N", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("\\n", " ", StringComparison.OrdinalIgnoreCase)
                .Trim();

            if (string.IsNullOrEmpty(cleaned))
            {
                continue;
            }

            int idx = output.Count + 1;
            output.Add(new SubtitleSegment
            {
                Index = idx,
                StartSeconds = startSec,
                EndSeconds = Math.Max(startSec + 0.5, endSec),
                Speaker = speaker,
                Gender = idx % 2 == 1 ? "male" : "female",
                OriginalText = cleaned,
                VietnameseText = cleaned
            });
        }
    }

    public static string ExportToSrt(IEnumerable<SubtitleSegment> segments, string displayMode = "dual")
    {
        if (segments is null)
        {
            return string.Empty;
        }

        string mode = (displayMode ?? "dual").Trim().ToLowerInvariant();
        var sb = new StringBuilder();
        int index = 1;

        foreach (var seg in segments)
        {
            string vi = seg.EffectiveVietnameseText.Trim();
            string orig = (seg.OriginalText ?? string.Empty).Trim();

            string body = mode switch
            {
                "vi" => !string.IsNullOrEmpty(vi) ? vi : orig,
                "orig" => !string.IsNullOrEmpty(orig) ? orig : vi,
                _ => (!string.IsNullOrEmpty(vi) && !string.IsNullOrEmpty(orig) && !string.Equals(vi, orig, StringComparison.Ordinal))
                    ? $"{vi}\n{orig}"
                    : (!string.IsNullOrEmpty(vi) ? vi : orig)
            };

            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }

            sb.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(FormatSrtTimestamp(seg.StartSeconds))
              .Append(" --> ")
              .Append(FormatSrtTimestamp(Math.Max(seg.StartSeconds + 0.2, seg.EndSeconds)))
              .Append('\n');
            sb.Append(body);
            index++;
        }

        return sb.ToString();
    }

    public static string FormatSrtTimestamp(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2}:{2:D2},{3:D3}",
            (int)ts.TotalHours,
            ts.Minutes,
            ts.Seconds,
            ts.Milliseconds);
    }

    private static double ParseTimeParts(string h, string m, string s, string ms)
    {
        int hours = int.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hh) ? hh : 0;
        int minutes = int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm) ? mm : 0;
        int seconds = int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ss) ? ss : 0;
        string msPadded = (ms ?? "0").PadRight(3, '0');
        if (msPadded.Length > 3)
        {
            msPadded = msPadded[..3];
        }
        int millis = int.TryParse(msPadded, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mms) ? mms : 0;

        return hours * 3600.0 + minutes * 60.0 + seconds + millis / 1000.0;
    }

    private static double ParseAssTimestamp(string raw)
    {
        var parts = (raw ?? string.Empty).Trim().Split(':');
        if (parts.Length != 3)
        {
            return 0;
        }

        int h = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hh) ? hh : 0;
        int m = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm) ? mm : 0;
        double s = double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double ss) ? ss : 0;
        return h * 3600.0 + m * 60.0 + s;
    }
}
