using QUANGLNBrowser.Core.Interfaces;

namespace QUANGLNBrowser.Core.Services;

/// <summary>
/// Tự động ưu tiên nạp NativeRustAdBlockEngine nếu phát hiện adblock_rust.dll tại AppContext.BaseDirectory,
/// ngược lại khởi tạo ManagedAdBlockEngine (fallback thuần C#).
/// </summary>
public static class AdBlockEngineFactory
{
    public static IAdBlockEngine Create(AdBlockFilterListUpdater? updater = null)
    {
        if (NativeRustAdBlockEngine.IsNativeDllPresent())
        {
            var nativeEngine = new NativeRustAdBlockEngine(updater);
            if (nativeEngine.IsNativeEngine)
            {
                AppLogger.Info("AdBlockEngineFactory: Activated NativeRustAdBlockEngine (adblock_rust.dll).");
                return nativeEngine;
            }
            nativeEngine.Dispose();
        }

        AppLogger.Info("AdBlockEngineFactory: Activated ManagedAdBlockEngine (C# fallback).");
        return new ManagedAdBlockEngine(updater, loadBaseline: true);
    }
}
