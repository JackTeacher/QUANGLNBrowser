using System.Windows.Input;
using Microsoft.Web.WebView2.Wpf;

namespace QUANGLNBrowser.Engine;

/// <summary>
/// Bắt phím tắt toàn cục (Ctrl+Shift+L, Ctrl+T, Ctrl+W) ngay cả khi focus đang nằm trong trang web WebView2.
/// </summary>
public sealed class BrowserKeyboardHandler
{
    private readonly Action _onPanicLock;
    private readonly Action _onNewTab;
    private readonly Action _onCloseTab;

    public BrowserKeyboardHandler(Action onPanicLock, Action onNewTab, Action onCloseTab)
    {
        _onPanicLock = onPanicLock;
        _onNewTab = onNewTab;
        _onCloseTab = onCloseTab;
    }

    public void Attach(WebView2 webView)
    {
        webView.PreviewKeyDown += OnPreviewKeyDown;
    }

    public void Detach(WebView2 webView)
    {
        try
        {
            webView.PreviewKeyDown -= OnPreviewKeyDown;
        }
        catch
        {
            // Bỏ qua nếu webView đã disposed
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && shift && e.Key == Key.L)
        {
            _onPanicLock();
            e.Handled = true;
            return;
        }

        if (ctrl && !shift && e.Key == Key.T)
        {
            _onNewTab();
            e.Handled = true;
            return;
        }

        if (ctrl && !shift && e.Key == Key.W)
        {
            _onCloseTab();
            e.Handled = true;
            return;
        }
    }
}
