namespace QUANGLNBrowser.Core.Services;

/// <summary>
/// Ghi nhật ký ứng dụng tối thiểu vào %AppData%\QUANGLNBrowser\logs\ (tuyệt đối không ghi thông tin nhạy cảm).
/// </summary>
public static class AppLogger
{
    private static readonly object SyncLock = new();

    public static string LogDirectory => Path.Combine(AppPaths.AppDataRoot, "logs");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (SyncLock)
            {
                Directory.CreateDirectory(LogDirectory);
                string filePath = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex is not null)
                {
                    line += Environment.NewLine + ex;
                }
                File.AppendAllText(filePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Không để lỗi ghi log làm gián đoạn ứng dụng
        }
    }
}
