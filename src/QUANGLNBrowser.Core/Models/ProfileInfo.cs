namespace QUANGLNBrowser.Core.Models;

/// <summary>
/// Thông tin hồ sơ người dùng (bao gồm hồ sơ chính và hồ sơ ẩn Decoy).
/// </summary>
public sealed class ProfileInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = "Mặc định";
    public bool IsDecoy { get; set; }
    public string AuthSaltBase64 { get; set; } = string.Empty;
    public string PinVerifierHash { get; set; } = string.Empty;
    public string CacheFolderName { get; set; } = "cef_cache";
    public int AutoLockMinutes { get; set; } = 5;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
