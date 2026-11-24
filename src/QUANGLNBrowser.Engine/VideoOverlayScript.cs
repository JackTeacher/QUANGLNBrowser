using System.IO;
using System.Reflection;

namespace QUANGLNBrowser.Engine;

/// <summary>
/// Cung cấp mã nguồn script video-overlay.js (hỗ trợ Shadow DOM) để tiêm vào FrameLoadEnd.
/// </summary>
public static class VideoOverlayScript
{
    private static string? _cachedScript;

    public static string GetScript()
    {
        if (!string.IsNullOrEmpty(_cachedScript))
        {
            return _cachedScript;
        }

        string filePath = Path.Combine(AppContext.BaseDirectory, "Resources", "video-overlay.js");
        if (File.Exists(filePath))
        {
            _cachedScript = File.ReadAllText(filePath);
            return _cachedScript;
        }

        var assembly = Assembly.GetExecutingAssembly();
        string? resName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("video-overlay.js", StringComparison.OrdinalIgnoreCase));
        if (resName is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resName);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                _cachedScript = reader.ReadToEnd();
                return _cachedScript;
            }
        }

        return string.Empty;
    }
}
