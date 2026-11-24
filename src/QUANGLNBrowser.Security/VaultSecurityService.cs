using System.Security.Cryptography;
using System.Text.Json;
using QUANGLNBrowser.Core.Interfaces;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;

namespace QUANGLNBrowser.Security;

/// <summary>
/// Quản lý khởi tạo Vault, xác thực Argon2id, Windows Hello + DPAPI, Decoy Profile và tự động xóa khóa khi Lock.
/// </summary>
public sealed class VaultSecurityService : IVaultService
{
    private sealed class VaultMetadata
    {
        public ProfileMeta? Primary { get; set; }
        public ProfileMeta? Decoy { get; set; }
        public bool WindowsHelloEnabled { get; set; }
        public string? DpapiProtectedKeyBase64 { get; set; }
        public string? DpapiEntropyBase64 { get; set; }
    }

    private sealed class ProfileMeta
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsDecoy { get; set; }
        public string AuthSaltBase64 { get; set; } = string.Empty;
        public string PinVerifierHash { get; set; } = string.Empty;
        public string CacheFolderName { get; set; } = "cef_cache";
        public int AutoLockMinutes { get; set; } = 5;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _appDataRoot;
    private readonly VaultDatabase _database = new();
    private readonly object _sync = new();

    private VaultMetadata? _metadata;
    private byte[]? _activeMasterKey;
    private int _failedAttempts;
    private DateTime _lockoutUntilUtc = DateTime.MinValue;
    private bool _disposed;

    public VaultSecurityService() : this(AppPaths.AppDataRoot)
    {
    }

    public VaultSecurityService(string appDataRoot)
    {
        _appDataRoot = appDataRoot;
        LoadMetadata();
    }

    private string MetaPath => Path.Combine(_appDataRoot, "vault_meta.json");

    private string GetProfileDbPath(string profileId) =>
        Path.Combine(_appDataRoot, "profiles", profileId, "vault.db");

    public bool IsInitialized => _metadata?.Primary is not null;

    public bool IsUnlocked
    {
        get
        {
            lock (_sync)
            {
                return _database.IsOpen && CurrentProfile is not null;
            }
        }
    }

    public bool IsWindowsHelloEnabled => _metadata?.WindowsHelloEnabled == true
        && !string.IsNullOrEmpty(_metadata.DpapiProtectedKeyBase64);

    public bool HasDecoyProfile => _metadata?.Decoy is not null;

    public ProfileInfo? CurrentProfile { get; private set; }

    public Task<bool> IsWindowsHelloAvailableAsync() => WindowsHelloHelper.IsAvailableAsync();

    public async Task InitializeNewVaultAsync(string masterPin, string? decoyPin, bool enableWindowsHello, int autoLockMinutes = 5)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterPin);

        if (!string.IsNullOrWhiteSpace(decoyPin) && string.Equals(masterPin, decoyPin, StringComparison.Ordinal))
        {
            throw new ArgumentException("Decoy PIN không được trùng với Master PIN.");
        }

        await Task.Run(() =>
        {
            byte[] primarySalt = Argon2Kdf.GenerateSalt();
            byte[] primaryKey = Argon2Kdf.DeriveMasterKey(masterPin, primarySalt);

            try
            {
                var primaryMeta = new ProfileMeta
                {
                    Id = "primary",
                    Name = "Hồ sơ chính",
                    IsDecoy = false,
                    AuthSaltBase64 = Convert.ToBase64String(primarySalt),
                    PinVerifierHash = Argon2Kdf.ComputeVerifierHash(primaryKey),
                    CacheFolderName = "cef_cache",
                    AutoLockMinutes = Math.Clamp(autoLockMinutes, 1, 120),
                    CreatedAt = DateTime.UtcNow
                };

                var meta = new VaultMetadata
                {
                    Primary = primaryMeta,
                    WindowsHelloEnabled = enableWindowsHello
                };

                if (enableWindowsHello)
                {
                    var (protectedKey, entropy) = WindowsHelloHelper.ProtectMasterKey(primaryKey);
                    meta.DpapiProtectedKeyBase64 = protectedKey;
                    meta.DpapiEntropyBase64 = entropy;
                }

                // Khởi tạo DB chính
                using (var db = new VaultDatabase())
                {
                    db.Open(GetProfileDbPath(primaryMeta.Id), primaryKey);
                    db.UpsertProfile(ToProfileInfo(primaryMeta));
                }

                // Khởi tạo Decoy Profile nếu có
                if (!string.IsNullOrWhiteSpace(decoyPin))
                {
                    byte[] decoySalt = Argon2Kdf.GenerateSalt();
                    byte[] decoyKey = Argon2Kdf.DeriveMasterKey(decoyPin, decoySalt);
                    try
                    {
                        var decoyMeta = new ProfileMeta
                        {
                            Id = "decoy",
                            Name = "Hồ sơ khách",
                            IsDecoy = true,
                            AuthSaltBase64 = Convert.ToBase64String(decoySalt),
                            PinVerifierHash = Argon2Kdf.ComputeVerifierHash(decoyKey),
                            CacheFolderName = "cef_cache",
                            AutoLockMinutes = Math.Clamp(autoLockMinutes, 1, 120),
                            CreatedAt = DateTime.UtcNow
                        };
                        meta.Decoy = decoyMeta;

                        using var decoyDb = new VaultDatabase();
                        decoyDb.Open(GetProfileDbPath(decoyMeta.Id), decoyKey);
                        decoyDb.UpsertProfile(ToProfileInfo(decoyMeta));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(decoyKey);
                    }
                }

                Directory.CreateDirectory(_appDataRoot);
                File.WriteAllText(MetaPath, JsonSerializer.Serialize(meta, JsonOptions));
                _metadata = meta;

                // Mở khóa luôn hồ sơ chính sau khi khởi tạo
                lock (_sync)
                {
                    ZeroActiveKey();
                    _activeMasterKey = primaryKey.ToArray();
                    _database.Open(GetProfileDbPath(primaryMeta.Id), _activeMasterKey);
                    CurrentProfile = ToProfileInfo(primaryMeta);
                    _failedAttempts = 0;
                    _lockoutUntilUtc = DateTime.MinValue;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(primaryKey);
            }
        });
    }

    public async Task<VaultUnlockResult> UnlockWithPinAsync(string pin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_metadata?.Primary is null)
        {
            return new VaultUnlockResult(false, null, "Vault chưa được khởi tạo.");
        }

        if (string.IsNullOrEmpty(pin))
        {
            return new VaultUnlockResult(false, null, "Vui lòng nhập PIN hoặc Master Password.");
        }

        var now = DateTime.UtcNow;
        if (now < _lockoutUntilUtc)
        {
            int seconds = (int)Math.Ceiling((_lockoutUntilUtc - now).TotalSeconds);
            return new VaultUnlockResult(false, null, $"Nhập sai quá nhiều lần. Vui lòng thử lại sau {seconds} giây.");
        }

        return await Task.Run(() =>
        {
            // Kiểm tra hồ sơ chính trước
            if (TryVerifyProfile(_metadata.Primary, pin, out byte[]? primaryKey) && primaryKey is not null)
            {
                try
                {
                    OpenProfileSession(_metadata.Primary, primaryKey);
                    return new VaultUnlockResult(true, CurrentProfile);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(primaryKey);
                }
            }

            // Kiểm tra hồ sơ Decoy
            if (_metadata.Decoy is not null &&
                TryVerifyProfile(_metadata.Decoy, pin, out byte[]? decoyKey) && decoyKey is not null)
            {
                try
                {
                    OpenProfileSession(_metadata.Decoy, decoyKey);
                    return new VaultUnlockResult(true, CurrentProfile);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(decoyKey);
                }
            }

            _failedAttempts++;
            if (_failedAttempts >= 3)
            {
                int backoffSeconds = Math.Min(60, (int)Math.Pow(2, _failedAttempts - 2) * 5);
                _lockoutUntilUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
            }

            return new VaultUnlockResult(false, null, "PIN hoặc Master Password không chính xác.");
        });
    }

    public async Task<VaultUnlockResult> UnlockWithWindowsHelloAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_metadata?.Primary is null || !IsWindowsHelloEnabled)
        {
            return new VaultUnlockResult(false, null, "Windows Hello chưa được kích hoạt.");
        }

        bool verified = await WindowsHelloHelper.RequestVerificationAsync("Mở khóa QUANGLN Browser");
        if (!verified)
        {
            return new VaultUnlockResult(false, null, "Xác thực Windows Hello không thành công hoặc đã bị hủy.");
        }

        return await Task.Run(() =>
        {
            byte[]? key = null;
            try
            {
                key = WindowsHelloHelper.UnprotectMasterKey(
                    _metadata.DpapiProtectedKeyBase64!,
                    _metadata.DpapiEntropyBase64!);

                if (!Argon2Kdf.VerifyMasterKey(key, _metadata.Primary.PinVerifierHash))
                {
                    return new VaultUnlockResult(false, null, "Khóa DPAPI không khớp với hồ sơ hiện tại.");
                }

                OpenProfileSession(_metadata.Primary, key);
                return new VaultUnlockResult(true, CurrentProfile);
            }
            catch (Exception ex)
            {
                return new VaultUnlockResult(false, null, $"Lỗi giải mã DPAPI: {ex.Message}");
            }
            finally
            {
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        });
    }

    public void Lock()
    {
        lock (_sync)
        {
            _database.Close();
            ZeroActiveKey();
            CurrentProfile = null;
        }
    }

    public Task<VaultOperationResult> UpdateProfileNameAsync(string newName) =>
        Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(newName))
            {
                return new VaultOperationResult(false, "Tên hồ sơ không được để trống.");
            }

            string cleanName = newName.Trim();
            if (cleanName.Length > 40)
            {
                cleanName = cleanName[..40];
            }

            lock (_sync)
            {
                if (_metadata is null || CurrentProfile is null || !_database.IsOpen)
                {
                    return new VaultOperationResult(false, "Vault chưa được mở khóa.");
                }

                var metaTarget = CurrentProfile.IsDecoy ? _metadata.Decoy : _metadata.Primary;
                if (metaTarget is null)
                {
                    return new VaultOperationResult(false, "Không tìm thấy thông tin hồ sơ.");
                }

                metaTarget.Name = cleanName;
                CurrentProfile = ToProfileInfo(metaTarget);
                _database.UpsertProfile(CurrentProfile);
                SaveMetadata();

                return new VaultOperationResult(true, $"✓ Đã đổi tên hồ sơ thành \"{cleanName}\".");
            }
        });

    public Task<VaultOperationResult> ChangePinAsync(string currentPin, string newPin) =>
        Task.Run(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(currentPin))
            {
                return new VaultOperationResult(false, "Vui lòng nhập mã PIN / Master Password hiện tại.");
            }

            if (string.IsNullOrWhiteSpace(newPin) || newPin.Length < 4)
            {
                return new VaultOperationResult(false, "PIN / Master Password mới phải có ít nhất 4 ký tự.");
            }

            lock (_sync)
            {
                if (_metadata is null || CurrentProfile is null || !_database.IsOpen)
                {
                    return new VaultOperationResult(false, "Vault chưa được mở khóa.");
                }

                var metaTarget = CurrentProfile.IsDecoy ? _metadata.Decoy : _metadata.Primary;
                if (metaTarget is null)
                {
                    return new VaultOperationResult(false, "Không tìm thấy hồ sơ đang hoạt động.");
                }

                if (!TryVerifyProfile(metaTarget, currentPin, out byte[]? oldKey) || oldKey is null)
                {
                    return new VaultOperationResult(false, "PIN / Master Password hiện tại không chính xác.");
                }
                CryptographicOperations.ZeroMemory(oldKey);

                if (!CurrentProfile.IsDecoy && _metadata.Decoy is not null &&
                    TryVerifyProfile(_metadata.Decoy, newPin, out byte[]? collisionDecoy) && collisionDecoy is not null)
                {
                    CryptographicOperations.ZeroMemory(collisionDecoy);
                    return new VaultOperationResult(false, "PIN mới không được trùng với Decoy PIN.");
                }

                if (CurrentProfile.IsDecoy && _metadata.Primary is not null &&
                    TryVerifyProfile(_metadata.Primary, newPin, out byte[]? collisionPrimary) && collisionPrimary is not null)
                {
                    CryptographicOperations.ZeroMemory(collisionPrimary);
                    return new VaultOperationResult(false, "Decoy PIN mới không được trùng với Master PIN.");
                }

                byte[] newSalt = Argon2Kdf.GenerateSalt();
                byte[] newKey = Argon2Kdf.DeriveMasterKey(newPin, newSalt);
                try
                {
                    _database.Rekey(newKey);

                    metaTarget.AuthSaltBase64 = Convert.ToBase64String(newSalt);
                    metaTarget.PinVerifierHash = Argon2Kdf.ComputeVerifierHash(newKey);

                    if (!CurrentProfile.IsDecoy && _metadata.WindowsHelloEnabled)
                    {
                        var (protectedKey, entropy) = WindowsHelloHelper.ProtectMasterKey(newKey);
                        _metadata.DpapiProtectedKeyBase64 = protectedKey;
                        _metadata.DpapiEntropyBase64 = entropy;
                    }

                    ZeroActiveKey();
                    _activeMasterKey = newKey.ToArray();

                    CurrentProfile = ToProfileInfo(metaTarget);
                    _database.UpsertProfile(CurrentProfile);
                    SaveMetadata();

                    return new VaultOperationResult(true, "✓ Đã cập nhật mã PIN / Master Password và mã hóa lại Vault.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(newKey);
                }
            }
        });

    public Task<VaultOperationResult> SetDecoyPinAsync(string? newDecoyPin) =>
        Task.Run(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            lock (_sync)
            {
                if (_metadata?.Primary is null || CurrentProfile is null || !_database.IsOpen)
                {
                    return new VaultOperationResult(false, "Vault chưa được mở khóa.");
                }

                if (CurrentProfile.IsDecoy)
                {
                    return new VaultOperationResult(false, "Chỉ Hồ sơ chính mới có quyền thay đổi Decoy PIN.");
                }

                if (string.IsNullOrWhiteSpace(newDecoyPin))
                {
                    _metadata.Decoy = null;
                    SaveMetadata();
                    return new VaultOperationResult(true, "✓ Đã gỡ bỏ Decoy PIN khỏi Vault.");
                }

                if (newDecoyPin.Length < 4)
                {
                    return new VaultOperationResult(false, "Decoy PIN phải có ít nhất 4 ký tự.");
                }

                if (TryVerifyProfile(_metadata.Primary, newDecoyPin, out byte[]? collisionKey) && collisionKey is not null)
                {
                    CryptographicOperations.ZeroMemory(collisionKey);
                    return new VaultOperationResult(false, "Decoy PIN không được trùng với Master PIN chính.");
                }

                byte[] decoySalt = Argon2Kdf.GenerateSalt();
                byte[] decoyKey = Argon2Kdf.DeriveMasterKey(newDecoyPin, decoySalt);
                try
                {
                    var decoyMeta = new ProfileMeta
                    {
                        Id = "decoy",
                        Name = _metadata.Decoy?.Name ?? "Hồ sơ khách",
                        IsDecoy = true,
                        AuthSaltBase64 = Convert.ToBase64String(decoySalt),
                        PinVerifierHash = Argon2Kdf.ComputeVerifierHash(decoyKey),
                        CacheFolderName = "cef_cache",
                        AutoLockMinutes = _metadata.Primary.AutoLockMinutes,
                        CreatedAt = _metadata.Decoy?.CreatedAt ?? DateTime.UtcNow
                    };

                    string decoyDbPath = GetProfileDbPath(decoyMeta.Id);
                    if (File.Exists(decoyDbPath))
                    {
                        File.Delete(decoyDbPath);
                    }

                    using (var decoyDb = new VaultDatabase())
                    {
                        decoyDb.Open(decoyDbPath, decoyKey);
                        decoyDb.UpsertProfile(ToProfileInfo(decoyMeta));
                    }

                    _metadata.Decoy = decoyMeta;
                    SaveMetadata();
                    return new VaultOperationResult(true, "✓ Đã thiết lập Decoy PIN mới cho hồ sơ khách.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(decoyKey);
                }
            }
        });

    public async Task<VaultOperationResult> SetWindowsHelloEnabledAsync(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsUnlocked || CurrentProfile is null)
        {
            return new VaultOperationResult(false, "Vault chưa được mở khóa.");
        }

        if (CurrentProfile.IsDecoy)
        {
            return new VaultOperationResult(false, "Chỉ Hồ sơ chính mới có quyền bật/tắt Windows Hello.");
        }

        if (!enabled)
        {
            lock (_sync)
            {
                if (_metadata is null)
                {
                    return new VaultOperationResult(false, "Vault chưa được khởi tạo.");
                }
                _metadata.WindowsHelloEnabled = false;
                _metadata.DpapiProtectedKeyBase64 = null;
                _metadata.DpapiEntropyBase64 = null;
                SaveMetadata();
            }
            return new VaultOperationResult(true, "✓ Đã tắt mở khóa bằng Windows Hello.");
        }

        bool available = await IsWindowsHelloAvailableAsync();
        if (!available)
        {
            return new VaultOperationResult(false, "Thiết bị chưa thiết lập Windows Hello.");
        }

        bool verified = await WindowsHelloHelper.RequestVerificationAsync("Bật mở khóa nhanh QUANGLN Browser");
        if (!verified)
        {
            return new VaultOperationResult(false, "Xác thực Windows Hello đã bị hủy.");
        }

        lock (_sync)
        {
            if (_metadata is null || _activeMasterKey is null)
            {
                return new VaultOperationResult(false, "Không có khóa hoạt động để bảo vệ.");
            }

            var (protectedKey, entropy) = WindowsHelloHelper.ProtectMasterKey(_activeMasterKey);
            _metadata.WindowsHelloEnabled = true;
            _metadata.DpapiProtectedKeyBase64 = protectedKey;
            _metadata.DpapiEntropyBase64 = entropy;
            SaveMetadata();
        }

        return new VaultOperationResult(true, "✓ Đã bật mở khóa nhanh bằng Windows Hello.");
    }

    private void SaveMetadata()
    {
        if (_metadata is null)
        {
            return;
        }
        Directory.CreateDirectory(_appDataRoot);
        File.WriteAllText(MetaPath, JsonSerializer.Serialize(_metadata, JsonOptions));
    }

    public Task RecordHistoryAsync(string url, string title) =>
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return;
                }
                _database.RecordHistory(CurrentProfile.Id, url, title);
            }
        });

    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int limit = 100) =>
        Task.Run<IReadOnlyList<HistoryEntry>>(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return Array.Empty<HistoryEntry>();
                }
                return _database.GetHistory(CurrentProfile.Id, limit);
            }
        });

    public Task ClearHistoryAsync() =>
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return;
                }
                _database.ClearHistory(CurrentProfile.Id);
            }
        });

    public Task DeleteHistoryEntryAsync(long historyId) =>
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return;
                }
                _database.DeleteHistoryEntry(historyId);
            }
        });

    public Task AddBookmarkAsync(string title, string url, string folderName = "Bookmarks Bar") =>
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return;
                }
                _database.AddBookmark(CurrentProfile.Id, title, url, folderName);
            }
        });

    public Task RemoveBookmarkAsync(long bookmarkId) =>
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return;
                }
                _database.RemoveBookmark(bookmarkId);
            }
        });

    public Task<IReadOnlyList<BookmarkEntry>> GetBookmarksAsync() =>
        Task.Run<IReadOnlyList<BookmarkEntry>>(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return Array.Empty<BookmarkEntry>();
                }
                return _database.GetBookmarks(CurrentProfile.Id);
            }
        });

    public Task<bool> IsBookmarkedAsync(string url) =>
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return false;
                }
                return _database.IsBookmarked(CurrentProfile.Id, url);
            }
        });

    public Task<IReadOnlyList<string>> GetWhitelistedDomainsAsync() =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return Array.Empty<string>();
                }
                return _database.GetWhitelistedDomains();
            }
        });

    public Task SetDomainWhitelistAsync(string domain, bool isWhitelisted) =>
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return;
                }
                _database.SetDomainWhitelist(domain, isWhitelisted);
            }
        });

    public Task UpsertDownloadAsync(DownloadItem item) =>
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return;
                }
                if (string.IsNullOrEmpty(item.ProfileId))
                {
                    item.ProfileId = CurrentProfile.Id;
                }
                _database.UpsertDownload(item);
            }
        });

    public Task<IReadOnlyList<DownloadItem>> GetDownloadsAsync() =>
        Task.Run<IReadOnlyList<DownloadItem>>(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return Array.Empty<DownloadItem>();
                }
                return _database.GetDownloads(CurrentProfile.Id);
            }
        });

    public Task DeleteDownloadAsync(string downloadId) =>
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return;
                }
                _database.DeleteDownload(downloadId);
            }
        });

    public Task<IReadOnlyList<VpnNode>> GetVpnNodesAsync() =>
        Task.Run<IReadOnlyList<VpnNode>>(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return Array.Empty<VpnNode>();
                }
                return _database.GetVpnNodes();
            }
        });

    public Task UpsertVpnNodeAsync(VpnNode node) =>
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return;
                }
                _database.UpsertVpnNode(node);
            }
        });

    public Task DeleteVpnNodeAsync(string nodeId) =>
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return;
                }
                _database.DeleteVpnNode(nodeId);
            }
        });

    public Task<VpnPreferences> GetVpnPreferencesAsync() =>
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return new VpnPreferences();
                }

                string? json = _database.GetVpnSetting("opera_vpn_preferences");
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new VpnPreferences();
                }

                try
                {
                    return JsonSerializer.Deserialize<VpnPreferences>(json) ?? new VpnPreferences();
                }
                catch
                {
                    return new VpnPreferences();
                }
            }
        });

    public Task SaveVpnPreferencesAsync(VpnPreferences preferences) =>
        Task.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(preferences);
            lock (_sync)
            {
                if (CurrentProfile is null || !_database.IsOpen)
                {
                    return;
                }

                string json = JsonSerializer.Serialize(preferences);
                _database.SetVpnSetting("opera_vpn_preferences", json);
            }
        });

    private static bool TryVerifyProfile(ProfileMeta meta, string pin, out byte[]? derivedKey)
    {
        derivedKey = null;
        byte[] salt = Convert.FromBase64String(meta.AuthSaltBase64);
        byte[] candidateKey = Argon2Kdf.DeriveMasterKey(pin, salt);
        if (Argon2Kdf.VerifyMasterKey(candidateKey, meta.PinVerifierHash))
        {
            derivedKey = candidateKey;
            return true;
        }

        CryptographicOperations.ZeroMemory(candidateKey);
        return false;
    }

    private void OpenProfileSession(ProfileMeta meta, byte[] masterKey)
    {
        lock (_sync)
        {
            ZeroActiveKey();
            _activeMasterKey = masterKey.ToArray();
            _database.Open(GetProfileDbPath(meta.Id), _activeMasterKey);
            CurrentProfile = ToProfileInfo(meta);
            _database.UpsertProfile(CurrentProfile);
            _failedAttempts = 0;
            _lockoutUntilUtc = DateTime.MinValue;
        }
    }

    private void LoadMetadata()
    {
        if (!File.Exists(MetaPath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(MetaPath);
            _metadata = JsonSerializer.Deserialize<VaultMetadata>(json, JsonOptions);
        }
        catch
        {
            _metadata = null;
        }
    }

    private static ProfileInfo ToProfileInfo(ProfileMeta meta) => new()
    {
        Id = meta.Id,
        Name = meta.Name,
        IsDecoy = meta.IsDecoy,
        AuthSaltBase64 = meta.AuthSaltBase64,
        PinVerifierHash = meta.PinVerifierHash,
        CacheFolderName = meta.CacheFolderName,
        AutoLockMinutes = meta.AutoLockMinutes,
        CreatedAt = meta.CreatedAt
    };

    private void ZeroActiveKey()
    {
        if (_activeMasterKey is not null)
        {
            CryptographicOperations.ZeroMemory(_activeMasterKey);
            _activeMasterKey = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Lock();
        _database.Dispose();
    }
}
