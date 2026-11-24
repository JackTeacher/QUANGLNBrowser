using Microsoft.Data.Sqlite;
using QUANGLNBrowser.Security;
using Xunit;

namespace QUANGLNBrowser.Tests;

public sealed class VaultSecurityTests : IDisposable
{
    private readonly string _tempRoot;

    public VaultSecurityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "QUANGLNBrowser_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Argon2Kdf_DerivesConsistent256BitKey_AndVerifies()
    {
        byte[] salt = Argon2Kdf.GenerateSalt();
        Assert.Equal(32, salt.Length);

        byte[] key1 = Argon2Kdf.DeriveMasterKey("MySecretPin123", salt);
        byte[] key2 = Argon2Kdf.DeriveMasterKey("MySecretPin123", salt);
        byte[] wrongKey = Argon2Kdf.DeriveMasterKey("WrongPin456", salt);

        Assert.Equal(32, key1.Length);
        Assert.Equal(key1, key2);
        Assert.NotEqual(key1, wrongKey);

        string verifier = Argon2Kdf.ComputeVerifierHash(key1);
        Assert.True(Argon2Kdf.VerifyMasterKey(key2, verifier));
        Assert.False(Argon2Kdf.VerifyMasterKey(wrongKey, verifier));
    }

    [Fact]
    public async Task VaultSecurityService_SupportsPrimaryAndDecoyProfiles_WithSqlCipherEncryption()
    {
        using var vault = new VaultSecurityService(_tempRoot);
        Assert.False(vault.IsInitialized);

        await vault.InitializeNewVaultAsync("Primary#2026", "Decoy#0000", enableWindowsHello: false, autoLockMinutes: 5);
        Assert.True(vault.IsInitialized);
        Assert.True(vault.IsUnlocked);
        Assert.NotNull(vault.CurrentProfile);
        Assert.False(vault.CurrentProfile!.IsDecoy);

        await vault.RecordHistoryAsync("https://example.com", "Example");
        await vault.AddBookmarkAsync("Example Bookmark", "https://example.com");

        vault.Lock();
        Assert.False(vault.IsUnlocked);
        Assert.Null(vault.CurrentProfile);

        // Sai PIN bị từ chối
        var wrongResult = await vault.UnlockWithPinAsync("WrongPin");
        Assert.False(wrongResult.Success);
        Assert.False(vault.IsUnlocked);

        // Decoy PIN mở hồ sơ trống hoàn toàn
        var decoyResult = await vault.UnlockWithPinAsync("Decoy#0000");
        Assert.True(decoyResult.Success);
        Assert.NotNull(decoyResult.Profile);
        Assert.True(decoyResult.Profile!.IsDecoy);
        Assert.Empty(await vault.GetHistoryAsync());
        Assert.Empty(await vault.GetBookmarksAsync());

        vault.Lock();

        // Master PIN mở lại đúng dữ liệu đã lưu
        var primaryResult = await vault.UnlockWithPinAsync("Primary#2026");
        Assert.True(primaryResult.Success);
        Assert.False(primaryResult.Profile!.IsDecoy);
        Assert.Single(await vault.GetHistoryAsync());
        Assert.Single(await vault.GetBookmarksAsync());

        vault.Lock();

        // Xác minh file vault.db thực sự được mã hóa SQLCipher (không thể đọc nếu thiếu khóa)
        string dbPath = Path.Combine(_tempRoot, "profiles", "primary", "vault.db");
        Assert.True(File.Exists(dbPath));

        VaultDatabase.EnsureSqliteInitialized();
        using var rawConn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        rawConn.Open();
        using var cmd = rawConn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
        Assert.Throws<SqliteException>(() => cmd.ExecuteScalar());
    }

    [Fact]
    public async Task UnlockWithPin_LocksOutAfterThreeFailures()
    {
        using var vault = new VaultSecurityService(_tempRoot);
        await vault.InitializeNewVaultAsync("ValidPin#123", decoyPin: null, enableWindowsHello: false);
        vault.Lock();

        for (int i = 0; i < 3; i++)
        {
            var fail = await vault.UnlockWithPinAsync($"WrongPin{i}");
            Assert.False(fail.Success);
        }

        // Lần thứ 4 dù nhập đúng PIN vẫn bị khóa tạm thời
        var lockedOut = await vault.UnlockWithPinAsync("ValidPin#123");
        Assert.False(lockedOut.Success);
        Assert.NotNull(lockedOut.ErrorMessage);
        Assert.Contains("thử lại sau", lockedOut.ErrorMessage);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Bỏ qua nếu file đang nhả handle
        }
    }
}
