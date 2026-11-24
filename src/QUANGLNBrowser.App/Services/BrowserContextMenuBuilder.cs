using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QUANGLNBrowser.Engine;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace QUANGLNBrowser.App.Services;

public sealed class BrowserContextMenuActions
{
    public required Action Back { get; init; }
    public required Action Forward { get; init; }
    public required Action Reload { get; init; }
    public required Action SavePageAs { get; init; }
    public required Action PrintPage { get; init; }
    public required Action CastOrShareDevice { get; init; }
    public required Action AddToReadingList { get; init; }
    public required Action ToggleReaderMode { get; init; }
    public required Action SendToYourDevices { get; init; }
    public required Action ShowQrCode { get; init; }
    public required Action SummarizePageWithAi { get; init; }
    public required Action TranslatePageToVietnamese { get; init; }
    public required Action OpenAiSettings { get; init; }
    public required Action TranslateScreenshot { get; init; }
    public required Action ReportUnsafeContent { get; init; }
    public required Action OpenHelpAndFeedback { get; init; }
    public required Action ViewPageSource { get; init; }
    public required Action<int, int> InspectElement { get; init; }

    // Ngữ cảnh đặc thù (Link / Ảnh / Chọn văn bản / Ô nhập liệu)
    public required Action<string> OpenUrlInNewTab { get; init; }
    public required Action<string> OpenUrlInNewWindow { get; init; }
    public required Action<string> OpenUrlInSplitView { get; init; }
    public required Action<string> OpenUrlInIncognitoWindow { get; init; }
    public required Action<string> SaveLinkAs { get; init; }
    public required Action<string> CopyText { get; init; }
    public required Action<string> SaveImageAs { get; init; }
    public required Action<string> SearchSelectionOnGoogle { get; init; }
    public required Action<string> TranslateSelectionToVietnamese { get; init; }
    public required Action CutSelection { get; init; }
    public required Action CopySelection { get; init; }
    public required Action PasteClipboard { get; init; }
    public required Action SelectAll { get; init; }
}

/// <summary>
/// Tạo ContextMenu chuột phải Fluent Dark Mode kiểu Cốc Cốc / Chrome:
/// - Khi chuột phải vào đối tượng gắn link: chỉ hiển thị gọn đúng nhóm tác vụ liên kết (giống Chrome/Cốc Cốc).
/// - Khi chuột phải vào vùng trang thường: hiển thị đầy đủ bộ tiện ích điều hướng, đọc, mã QR, Hỏi AI, dịch ảnh, kiểm tra.
/// </summary>
public static class BrowserContextMenuBuilder
{
    private static readonly SolidColorBrush MenuBg = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2B)));
    private static readonly SolidColorBrush MenuBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x48)));
    private static readonly SolidColorBrush SeparatorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0x34, 0x42)));
    private static readonly SolidColorBrush PrimaryText = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)));
    private static readonly SolidColorBrush IconBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)));
    private static readonly SolidColorBrush AiAccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)));
    private static readonly SolidColorBrush TranslateAccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly SolidColorBrush WarningAccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)));

    public static ContextMenu Build(BrowserContextMenuInfo info, BrowserContextMenuActions actions)
    {
        var preset = BrowserThemeManager.CurrentPreset;
        var menuBg = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset.ChromeHex)));
        var menuBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x90, preset.AccentR, preset.AccentG, preset.AccentB)));
        var accentBrush = Freeze(new SolidColorBrush(Color.FromRgb(preset.AccentR, preset.AccentG, preset.AccentB)));

        var menu = new ContextMenu
        {
            Background = menuBg,
            BorderBrush = menuBorder,
            BorderThickness = new Thickness(1),
            Foreground = PrimaryText,
            MinWidth = 275,
            Padding = new Thickness(4, 6, 4, 6)
        };

        // === CHẾ ĐỘ 1: CHUỘT PHẢI VÀO ĐỐI TƯỢNG GẮN LINK (Gọn đúng chuẩn Chrome / Cốc Cốc) ===
        if (!string.IsNullOrWhiteSpace(info.LinkUrl))
        {
            menu.Items.Add(CreateItem("Mở đường liên kết trong thẻ mới", null, SymbolRegular.TabAdd20, IconBrush, () => actions.OpenUrlInNewTab(info.LinkUrl)));
            menu.Items.Add(CreateItem("Mở đường liên kết trong cửa sổ mới", null, SymbolRegular.WindowNew20, IconBrush, () => actions.OpenUrlInNewWindow(info.LinkUrl)));
            menu.Items.Add(CreateItem("Mở liên kết ở chế độ xem phân tách", null, SymbolRegular.SplitVertical20, IconBrush, () => actions.OpenUrlInSplitView(info.LinkUrl)));
            menu.Items.Add(CreateItem("Mở đường liên kết bằng cửa sổ ẩn danh", null, SymbolRegular.EyeOff20, IconBrush, () => actions.OpenUrlInIncognitoWindow(info.LinkUrl)));

            menu.Items.Add(CreateSeparator());

            menu.Items.Add(CreateItem("Lưu liên kết dưới dạng...", null, SymbolRegular.ArrowDownload20, IconBrush, () => actions.SaveLinkAs(info.LinkUrl)));
            menu.Items.Add(CreateItem("Sao chép địa chỉ liên kết", null, SymbolRegular.Link20, IconBrush, () => actions.CopyText(info.LinkUrl)));

            // Nếu đối tượng link đồng thời chứa hình ảnh (ví dụ thumbnail) -> bổ sung gọn nhóm ảnh
            if (info.IsImage && !string.IsNullOrWhiteSpace(info.SourceUrl))
            {
                menu.Items.Add(CreateSeparator());
                menu.Items.Add(CreateItem("Mở hình ảnh trong thẻ mới", null, SymbolRegular.Image20, IconBrush, () => actions.OpenUrlInNewTab(info.SourceUrl)));
                menu.Items.Add(CreateItem("Lưu hình ảnh dưới dạng...", null, SymbolRegular.ArrowDownload20, IconBrush, () => actions.SaveImageAs(info.SourceUrl)));
                menu.Items.Add(CreateItem("Sao chép địa chỉ hình ảnh", null, SymbolRegular.Copy20, IconBrush, () => actions.CopyText(info.SourceUrl)));
            }

            menu.Items.Add(CreateSeparator());
            menu.Items.Add(CreateItem("Kiểm tra", null, SymbolRegular.DeveloperBoard20, IconBrush, () => actions.InspectElement(info.XCoord, info.YCoord)));
            return menu;
        }

        // === CHẾ ĐỘ 2: HÌNH ẢNH THUẦN / VĂN BẢN BÔI ĐEN / Ô NHẬP LIỆU + TRANG WEB ===
        if (info.IsImage && !string.IsNullOrWhiteSpace(info.SourceUrl))
        {
            menu.Items.Add(CreateItem("Mở hình ảnh trong thẻ mới", null, SymbolRegular.Image20, IconBrush, () => actions.OpenUrlInNewTab(info.SourceUrl)));
            menu.Items.Add(CreateItem("Lưu hình ảnh dưới dạng...", null, SymbolRegular.ArrowDownload20, IconBrush, () => actions.SaveImageAs(info.SourceUrl)));
            menu.Items.Add(CreateItem("Sao chép địa chỉ hình ảnh", null, SymbolRegular.Copy20, IconBrush, () => actions.CopyText(info.SourceUrl)));
            menu.Items.Add(CreateSeparator());
        }

        if (!string.IsNullOrWhiteSpace(info.SelectionText))
        {
            string trimmed = info.SelectionText.Trim();
            string snippet = trimmed.Length > 22 ? trimmed[..22] + "…" : trimmed;

            menu.Items.Add(CreateItem("Sao chép", "Ctrl+C", SymbolRegular.Copy20, IconBrush, actions.CopySelection));
            menu.Items.Add(CreateItem($"Tìm \"{snippet}\" trên Google", null, SymbolRegular.Search20, IconBrush, () => actions.SearchSelectionOnGoogle(trimmed)));
            menu.Items.Add(CreateItem($"Dịch \"{snippet}\" sang tiếng Việt", null, SymbolRegular.LocalLanguage20, TranslateAccentBrush, () => actions.TranslateSelectionToVietnamese(trimmed)));
            menu.Items.Add(CreateSeparator());
        }

        if (info.IsEditable)
        {
            menu.Items.Add(CreateItem("Cắt", "Ctrl+X", SymbolRegular.Cut20, IconBrush, actions.CutSelection,
                isEnabled: info.EditStateFlags.HasFlag(ContextMenuEditState.CanCut)));
            menu.Items.Add(CreateItem("Sao chép", "Ctrl+C", SymbolRegular.Copy20, IconBrush, actions.CopySelection,
                isEnabled: info.EditStateFlags.HasFlag(ContextMenuEditState.CanCopy)));
            menu.Items.Add(CreateItem("Dán", "Ctrl+V", SymbolRegular.ClipboardPaste20, IconBrush, actions.PasteClipboard,
                isEnabled: info.EditStateFlags.HasFlag(ContextMenuEditState.CanPaste)));
            menu.Items.Add(CreateItem("Chọn tất cả", "Ctrl+A", SymbolRegular.SelectAllOn20, IconBrush, actions.SelectAll,
                isEnabled: info.EditStateFlags.HasFlag(ContextMenuEditState.CanSelectAll)));
            menu.Items.Add(CreateSeparator());
        }

        // Nhóm Điều hướng
        menu.Items.Add(CreateItem("Quay lại", "Alt+Mũi tên trái", SymbolRegular.ArrowLeft20, IconBrush, actions.Back, info.CanGoBack));
        menu.Items.Add(CreateItem("Chuyển tiếp", "Alt+Mũi tên phải", SymbolRegular.ArrowRight20, IconBrush, actions.Forward, info.CanGoForward));
        menu.Items.Add(CreateItem("Tải lại", "Ctrl+R", SymbolRegular.ArrowClockwise20, IconBrush, actions.Reload));

        menu.Items.Add(CreateSeparator());

        // Nhóm Lưu / In / Truyền thiết bị
        menu.Items.Add(CreateItem("Lưu dưới dạng...", "Ctrl+S", SymbolRegular.ArrowDownload20, IconBrush, actions.SavePageAs));
        menu.Items.Add(CreateItem("In...", "Ctrl+P", SymbolRegular.Print20, IconBrush, actions.PrintPage));
        menu.Items.Add(CreateItem("Truyền đến thiết bị...", null, SymbolRegular.Cast20, IconBrush, actions.CastOrShareDevice));

        menu.Items.Add(CreateSeparator());

        // Nhóm Đọc (Danh sách đọc & Chế độ đọc)
        menu.Items.Add(CreateItem("Thêm vào danh sách đọc", null, SymbolRegular.BookmarkAdd20, IconBrush, actions.AddToReadingList));
        menu.Items.Add(CreateItem("Mở ở chế độ đọc", "Alt+Shift+R", SymbolRegular.BookOpen20, IconBrush, actions.ToggleReaderMode));

        menu.Items.Add(CreateSeparator());

        // Nhóm Thiết bị & Mã QR
        menu.Items.Add(CreateItem("Gửi đến thiết bị của bạn", null, SymbolRegular.PhoneLaptop20, IconBrush, actions.SendToYourDevices));
        menu.Items.Add(CreateItem("Tạo mã QR cho trang này", null, SymbolRegular.QrCode20, IconBrush, actions.ShowQrCode));

        menu.Items.Add(CreateSeparator());

        // Tiện ích dịch nhanh toàn trang (giống Chrome: Dịch sang Tiếng Việt)
        menu.Items.Add(CreateItem("Dịch sang Tiếng Việt", null, SymbolRegular.LocalLanguage20, accentBrush, actions.TranslatePageToVietnamese));

        menu.Items.Add(CreateSeparator());

        // Nhóm Hỏi AI về trang này (Submenu) & Dịch ảnh chụp màn hình
        var askAiParent = CreateItem("Hỏi AI về trang này", null, SymbolRegular.Sparkle20, accentBrush, null);
        askAiParent.Items.Add(CreateItem("Tóm tắt trang này", null, SymbolRegular.DocumentText20, IconBrush, actions.SummarizePageWithAi));
        askAiParent.Items.Add(CreateItem("Dịch trang này sang tiếng Việt", null, SymbolRegular.LocalLanguage20, IconBrush, actions.TranslatePageToVietnamese));
        askAiParent.Items.Add(CreateItem("Cài đặt tính năng AI", null, SymbolRegular.Settings20, IconBrush, actions.OpenAiSettings));
        menu.Items.Add(askAiParent);

        menu.Items.Add(CreateItem("Dịch ảnh chụp màn hình", null, SymbolRegular.LocalLanguage20, TranslateAccentBrush, actions.TranslateScreenshot));

        menu.Items.Add(CreateSeparator());

        // Nhóm An toàn & Phản hồi
        menu.Items.Add(CreateItem("Báo cáo nội dung không an toàn...", null, SymbolRegular.Warning20, WarningAccentBrush, actions.ReportUnsafeContent));
        menu.Items.Add(CreateItem("Trợ giúp và phản hồi", "Alt+Shift+I", SymbolRegular.QuestionCircle20, IconBrush, actions.OpenHelpAndFeedback));

        menu.Items.Add(CreateSeparator());

        // Nhóm Nhà phát triển
        menu.Items.Add(CreateItem("Xem nguồn trang", "Ctrl+U", SymbolRegular.Code20, IconBrush, actions.ViewPageSource));
        menu.Items.Add(CreateItem("Kiểm tra", null, SymbolRegular.DeveloperBoard20, IconBrush, () => actions.InspectElement(info.XCoord, info.YCoord)));

        return menu;
    }

    private static MenuItem CreateItem(
        string header,
        string? shortcutText,
        SymbolRegular symbol,
        Brush iconForeground,
        Action? onClick,
        bool isEnabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = shortcutText ?? string.Empty,
            IsEnabled = isEnabled,
            Foreground = PrimaryText,
            FontSize = 12.5,
            Padding = new Thickness(6, 5, 8, 5),
            Icon = new SymbolIcon
            {
                Symbol = symbol,
                FontSize = 15,
                Foreground = iconForeground
            }
        };

        if (onClick is not null)
        {
            item.Click += (_, _) => onClick();
        }

        return item;
    }

    private static Separator CreateSeparator() => new()
    {
        Background = SeparatorBrush,
        Margin = new Thickness(6, 4, 6, 4)
    };

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
