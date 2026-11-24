using QUANGLNBrowser.Core.Models;

namespace QUANGLNBrowser.Core.Interfaces;

public sealed record VaultUnlockResult(bool Success, ProfileInfo? Profile, string? ErrorMessage = null);

public sealed record VaultOperationResult(bool Success, string? Message = null);

/// <summary>
/// Giao diện dịch vụ bảo mật &amp; cơ sở dữ liệu mã hóa SQLCipher (vault.db).
/// </summary>
public interface IVaultService : IDisposable
{
    bool IsInitialized { get; }
    bool IsUnlocked { get; }
    bool IsWindowsHelloEnabled { get; }
    bool HasDecoyProfile { get; }
    ProfileInfo? CurrentProfile { get; }

    Task<bool> IsWindowsHelloAvailableAsync();
    Task InitializeNewVaultAsync(string masterPin, string? decoyPin, bool enableWindowsHello, int autoLockMinutes = 5);
    Task<VaultUnlockResult> UnlockWithPinAsync(string pin);
    Task<VaultUnlockResult> UnlockWithWindowsHelloAsync();
    Task<VaultOperationResult> UpdateProfileNameAsync(string newName);
    Task<VaultOperationResult> ChangePinAsync(string currentPin, string newPin);
    Task<VaultOperationResult> SetDecoyPinAsync(string? newDecoyPin);
    Task<VaultOperationResult> SetWindowsHelloEnabledAsync(bool enabled);
    void Lock();

    Task RecordHistoryAsync(string url, string title);
    Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int limit = 100);
    Task DeleteHistoryEntryAsync(long historyId);
    Task ClearHistoryAsync();

    Task AddBookmarkAsync(string title, string url, string folderName = "Bookmarks Bar");
    Task RemoveBookmarkAsync(long bookmarkId);
    Task<IReadOnlyList<BookmarkEntry>> GetBookmarksAsync();
    Task<bool> IsBookmarkedAsync(string url);

    Task<IReadOnlyList<string>> GetWhitelistedDomainsAsync();
    Task SetDomainWhitelistAsync(string domain, bool isWhitelisted);

    Task UpsertDownloadAsync(DownloadItem item);
    Task<IReadOnlyList<DownloadItem>> GetDownloadsAsync();
    Task DeleteDownloadAsync(string downloadId);

    Task<IReadOnlyList<VpnNode>> GetVpnNodesAsync();
    Task UpsertVpnNodeAsync(VpnNode node);
    Task DeleteVpnNodeAsync(string nodeId);

    Task<VpnPreferences> GetVpnPreferencesAsync();
    Task SaveVpnPreferencesAsync(VpnPreferences preferences);
}
