using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QUANGLNBrowser.App.Services;
using Wpf.Ui.Controls;

namespace QUANGLNBrowser.App.Views;

public partial class QrCodeWindow : FluentWindow
{
    public QrCodeWindow(string initialUrl)
    {
        InitializeComponent();
        UrlInputBox.Text = string.IsNullOrWhiteSpace(initialUrl) ? "https://www.google.com" : initialUrl;
        RenderQr(UrlInputBox.Text);
    }

    private void RenderQr(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            QrImage.Source = PageUtilityService.CreateQrBitmap(text, pixelsPerModule: 10);
        }
        catch
        {
            // Bỏ qua nếu URL quá dài vượt giới hạn QR
        }
    }

    private void OnUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        StatusText.Text = string.Empty;
        RenderQr(UrlInputBox.Text);
    }

    private void OnCopyUrlClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlInputBox.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(UrlInputBox.Text);
            StatusText.Text = "✓ Đã sao chép liên kết vào bộ nhớ tạm.";
        }
        catch
        {
            StatusText.Text = "Không thể truy cập Clipboard.";
        }
    }

    private void OnSavePngClicked(object sender, RoutedEventArgs e)
    {
        string url = UrlInputBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Lưu mã QR thành ảnh PNG",
            FileName = $"QUANGLN_QR_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            Filter = "PNG Image (*.png)|*.png"
        };

        if (dlg.ShowDialog() == true)
        {
            byte[] bytes = PageUtilityService.GenerateQrPngBytes(url, pixelsPerModule: 12);
            File.WriteAllBytes(dlg.FileName, bytes);
            StatusText.Text = $"✓ Đã lưu mã QR: {Path.GetFileName(dlg.FileName)}";
        }
    }
}
