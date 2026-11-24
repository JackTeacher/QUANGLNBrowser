using System.Windows;
using System.Windows.Threading;
using QUANGLNBrowser.App.Services;
using QUANGLNBrowser.Core.Services;

namespace QUANGLNBrowser.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLogger.Info("App started");
        base.OnStartup(e);
        BrowserThemeManager.Initialize();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Dispatcher unhandled exception", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"Đã xảy ra lỗi: {e.Exception.Message}\n\nChi tiết được lưu tại: {AppLogger.LogDirectory}",
            "QUANGLN Browser",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLogger.Error("AppDomain unhandled exception", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }
}
