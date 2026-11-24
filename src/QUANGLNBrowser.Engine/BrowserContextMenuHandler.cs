using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace QUANGLNBrowser.Engine;

[Flags]
public enum ContextMenuEditState
{
    None = 0,
    CanUndo = 1,
    CanRedo = 2,
    CanCut = 4,
    CanCopy = 8,
    CanPaste = 16,
    CanDelete = 32,
    CanSelectAll = 64
}

/// <summary>
/// Thông tin ngữ cảnh tại điểm người dùng nhấn chuột phải trong WebView2.
/// </summary>
public sealed record BrowserContextMenuInfo(
    string PageUrl,
    string LinkUrl,
    string SourceUrl,
    string SelectionText,
    bool IsEditable,
    bool IsImage,
    bool CanGoBack,
    bool CanGoForward,
    int XCoord,
    int YCoord,
    ContextMenuEditState EditStateFlags);

/// <summary>
/// Chặn menu chuột phải Win32 mặc định của WebView2 và ủy quyền hiển thị menu Dark Mode WPF tùy biến (giống Cốc Cốc / Chrome).
/// </summary>
public sealed class BrowserContextMenuHandler
{
    private readonly Action<BrowserContextMenuInfo> _onShowContextMenu;

    public BrowserContextMenuHandler(Action<BrowserContextMenuInfo> onShowContextMenu)
    {
        _onShowContextMenu = onShowContextMenu;
    }

    public void Attach(CoreWebView2 coreWebView2, bool canGoBack, bool canGoForward)
    {
        coreWebView2.ContextMenuRequested += (sender, e) =>
        {
            e.Handled = true; // Chặn menu gốc của Chromium/Edge

            var target = e.ContextMenuTarget;
            string pageUrl = target.PageUri ?? string.Empty;
            string linkUrl = target.HasLinkUri ? target.LinkUri : string.Empty;
            string sourceUrl = target.HasSourceUri ? target.SourceUri : string.Empty;
            string selectionText = target.HasSelection ? target.SelectionText : string.Empty;
            bool isEditable = target.IsEditable;
            bool isImage = target.Kind == CoreWebView2ContextMenuTargetKind.Image;

            ContextMenuEditState editFlags = ContextMenuEditState.None;
            if (isEditable)
            {
                editFlags |= ContextMenuEditState.CanCut | ContextMenuEditState.CanPaste | ContextMenuEditState.CanSelectAll;
            }
            if (!string.IsNullOrEmpty(selectionText))
            {
                editFlags |= ContextMenuEditState.CanCopy;
            }

            var info = new BrowserContextMenuInfo(
                pageUrl,
                linkUrl,
                sourceUrl,
                selectionText,
                isEditable,
                isImage,
                canGoBack,
                canGoForward,
                e.Location.X,
                e.Location.Y,
                editFlags);

            _onShowContextMenu(info);
        };
    }
}
