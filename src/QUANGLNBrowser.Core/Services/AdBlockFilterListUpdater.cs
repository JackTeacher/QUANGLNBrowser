namespace QUANGLNBrowser.Core.Services;

/// <summary>
/// Tự động tải và cache các danh sách bộ lọc kiểu Cốc Cốc (EasyList, EasyPrivacy, ABPVN, Anti-Annoyance)
/// vào %AppData%\QUANGLNBrowser\filters\.
/// </summary>
public sealed class AdBlockFilterListUpdater
{
    public sealed record FilterSubscription(string Key, string Name, string FileName, string Url, string Description = "");

    public static readonly IReadOnlyList<FilterSubscription> DefaultSubscriptions =
    [
        new("easylist", "EasyList", "easylist.txt", "https://easylist.to/easylist/easylist.txt", "Chặn quảng cáo quốc tế (banner, iframe, script quảng cáo)"),
        new("easyprivacy", "EasyPrivacy", "easyprivacy.txt", "https://easylist.to/easylist/easyprivacy.txt", "Chặn trình theo dõi, phân tích hành vi & thu thập dữ liệu"),
        new("abpvn", "ABPVN (Việt Nam)", "abpvn.txt", "https://raw.githubusercontent.com/abpvn/abpvn/master/filter/abpvn.txt", "Chặn quảng cáo báo Việt Nam, phim, truyện, Admicro, Eclick, Ambient"),
        new("annoyances", "Anti-Annoyance & Pop-up", "fanboy-annoyance.txt", "https://easylist.to/easylist/fanboy-annoyance.txt", "Chặn pop-up phiền nhiễu, thông báo đẩy, banner cookie che màn hình")
    ];

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly string _filtersDir;
    private readonly TimeSpan _cacheTtl;

    public AdBlockFilterListUpdater(string? filtersDirectory = null, TimeSpan? cacheTtl = null)
    {
        _filtersDir = filtersDirectory ?? AppPaths.FiltersRoot;
        _cacheTtl = cacheTtl ?? TimeSpan.FromDays(3);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) QUANGLNBrowser/1.0");
        return client;
    }

    public DateTime? GetLatestFilterUpdateUtc()
    {
        if (!Directory.Exists(_filtersDir))
        {
            return null;
        }

        DateTime? latest = null;
        foreach (var sub in DefaultSubscriptions)
        {
            string localPath = Path.Combine(_filtersDir, sub.FileName);
            if (!File.Exists(localPath))
            {
                continue;
            }

            DateTime dt = File.GetLastWriteTimeUtc(localPath);
            if (latest is null || dt > latest.Value)
            {
                latest = dt;
            }
        }
        return latest;
    }

    public Task<IReadOnlyList<string>> EnsureFilterFilesAsync(bool forceDownload = false, CancellationToken cancellationToken = default) =>
        EnsureFilterFilesAsync(forceDownload, enabledKeys: null, cancellationToken);

    public async Task<IReadOnlyList<string>> EnsureFilterFilesAsync(
        bool forceDownload,
        IReadOnlySet<string>? enabledKeys,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_filtersDir);
        var validFiles = new List<string>();

        foreach (var sub in DefaultSubscriptions)
        {
            if (enabledKeys is not null && !enabledKeys.Contains(sub.Key))
            {
                continue;
            }

            string localPath = Path.Combine(_filtersDir, sub.FileName);
            bool needDownload = forceDownload || !File.Exists(localPath);

            if (!needDownload)
            {
                var info = new FileInfo(localPath);
                if (info.Length < 128 || (DateTime.UtcNow - info.LastWriteTimeUtc) > _cacheTtl)
                {
                    needDownload = true;
                }
            }

            if (needDownload)
            {
                try
                {
                    using var response = await SharedHttpClient.GetAsync(sub.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        string content = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (!string.IsNullOrWhiteSpace(content) && content.Length > 128)
                        {
                            string tempPath = localPath + ".tmp";
                            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
                            File.Move(tempPath, localPath, overwrite: true);
                            AppLogger.Info($"Updated AdBlock filter list: {sub.Name} ({content.Length} chars)");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLogger.Error($"Failed to download filter list {sub.Name} from {sub.Url}", ex);
                }
            }

            if (File.Exists(localPath))
            {
                validFiles.Add(localPath);
            }
        }

        return validFiles;
    }
}
