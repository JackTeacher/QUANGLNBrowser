using Microsoft.Data.Sqlite;
using QUANGLNBrowser.Core.Models;

namespace QUANGLNBrowser.Security;

/// <summary>
/// Quản lý kết nối SQLite mã hóa SQLCipher (AES-256) cho vault.db.
/// </summary>
public sealed class VaultDatabase : IDisposable
{
    private static int _sqliteInitialized;
    private SqliteConnection? _connection;
    private bool _disposed;

    static VaultDatabase()
    {
        EnsureSqliteInitialized();
    }

    public static void EnsureSqliteInitialized()
    {
        if (Interlocked.Exchange(ref _sqliteInitialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }
    }

    public bool IsOpen => _connection is not null && _connection.State == System.Data.ConnectionState.Open;

    public void Open(string dbPath, byte[] masterKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (masterKey.Length != 32)
        {
            throw new ArgumentException("MasterKey phải đúng 32 bytes (256-bit).", nameof(masterKey));
        }

        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        Close();

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        var conn = new SqliteConnection(connectionString);
        conn.Open();

        // Dùng raw 256-bit key từ Argon2id (x'<64 hex chars>') cho SQLCipher
        string hexKey = Convert.ToHexString(masterKey);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA key = \"x'{hexKey}'\";";
            cmd.ExecuteNonQuery();
        }

        // Kiểm tra key hợp lệ và bật khóa ngoại + WAL
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA cipher_compatibility = 4;
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = WAL;
                SELECT count(*) FROM sqlite_master;
                """;
            cmd.ExecuteScalar();
        }

        _connection = conn;
        EnsureSchema();
    }

    public void Rekey(byte[] newMasterKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (newMasterKey.Length != 32)
        {
            throw new ArgumentException("MasterKey phải đúng 32 bytes (256-bit).", nameof(newMasterKey));
        }

        var conn = GetConnection();
        string hexKey = Convert.ToHexString(newMasterKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA rekey = \"x'{hexKey}'\";";
        cmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Profiles (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                IsDecoy INTEGER NOT NULL DEFAULT 0,
                AuthSaltBase64 TEXT NOT NULL,
                PinVerifierHash TEXT NOT NULL,
                CacheFolderName TEXT NOT NULL,
                AutoLockMinutes INTEGER NOT NULL DEFAULT 5,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS History (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId TEXT NOT NULL,
                Url TEXT NOT NULL,
                Title TEXT,
                VisitTime TEXT NOT NULL,
                VisitCount INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_History_Profile_Time ON History(ProfileId, VisitTime DESC);

            CREATE TABLE IF NOT EXISTS Bookmarks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId TEXT NOT NULL,
                FolderName TEXT NOT NULL DEFAULT 'Bookmarks Bar',
                Title TEXT NOT NULL,
                Url TEXT NOT NULL,
                FaviconBlob BLOB,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Downloads (
                Id TEXT PRIMARY KEY,
                ProfileId TEXT NOT NULL,
                Url TEXT NOT NULL,
                FileName TEXT NOT NULL,
                SavePath TEXT NOT NULL,
                TotalBytes INTEGER NOT NULL DEFAULT 0,
                DownloadedBytes INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL,
                MediaType TEXT NOT NULL,
                HeadersJson TEXT,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AdBlockDomainRules (
                Domain TEXT PRIMARY KEY,
                IsWhitelisted INTEGER NOT NULL DEFAULT 0,
                CustomCss TEXT
            );

            CREATE TABLE IF NOT EXISTS VpnNodes (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                CountryCode TEXT NOT NULL,
                Protocol TEXT NOT NULL,
                ConfigJson TEXT NOT NULL,
                LatencyMs INTEGER NOT NULL DEFAULT -1,
                IsDefault INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS VpnSettings (
                SettingKey TEXT PRIMARY KEY,
                SettingValue TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void UpsertProfile(ProfileInfo profile)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Profiles (Id, Name, IsDecoy, AuthSaltBase64, PinVerifierHash, CacheFolderName, AutoLockMinutes, CreatedAt)
            VALUES ($id, $name, $isDecoy, $salt, $verifier, $cache, $autoLock, $createdAt)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                AutoLockMinutes = excluded.AutoLockMinutes;
            """;
        cmd.Parameters.AddWithValue("$id", profile.Id);
        cmd.Parameters.AddWithValue("$name", profile.Name);
        cmd.Parameters.AddWithValue("$isDecoy", profile.IsDecoy ? 1 : 0);
        cmd.Parameters.AddWithValue("$salt", profile.AuthSaltBase64);
        cmd.Parameters.AddWithValue("$verifier", profile.PinVerifierHash);
        cmd.Parameters.AddWithValue("$cache", profile.CacheFolderName);
        cmd.Parameters.AddWithValue("$autoLock", profile.AutoLockMinutes);
        cmd.Parameters.AddWithValue("$createdAt", profile.CreatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void RecordHistory(string profileId, string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var conn = GetConnection();
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT Id, VisitCount FROM History WHERE ProfileId = $pid AND Url = $url LIMIT 1;";
        checkCmd.Parameters.AddWithValue("$pid", profileId);
        checkCmd.Parameters.AddWithValue("$url", url);

        using var reader = checkCmd.ExecuteReader();
        if (reader.Read())
        {
            long id = reader.GetInt64(0);
            int count = reader.GetInt32(1) + 1;
            reader.Close();

            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = """
                UPDATE History
                SET Title = $title, VisitTime = $time, VisitCount = $count
                WHERE Id = $id;
                """;
            updateCmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? url : title);
            updateCmd.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
            updateCmd.Parameters.AddWithValue("$count", count);
            updateCmd.Parameters.AddWithValue("$id", id);
            updateCmd.ExecuteNonQuery();
        }
        else
        {
            reader.Close();
            using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO History (ProfileId, Url, Title, VisitTime, VisitCount)
                VALUES ($pid, $url, $title, $time, 1);
                """;
            insertCmd.Parameters.AddWithValue("$pid", profileId);
            insertCmd.Parameters.AddWithValue("$url", url);
            insertCmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? url : title);
            insertCmd.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
            insertCmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<HistoryEntry> GetHistory(string profileId, int limit = 100)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ProfileId, Url, Title, VisitTime, VisitCount
            FROM History
            WHERE ProfileId = $pid
            ORDER BY VisitTime DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$pid", profileId);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<HistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new HistoryEntry
            {
                Id = reader.GetInt64(0),
                ProfileId = reader.GetString(1),
                Url = reader.GetString(2),
                Title = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                VisitTime = DateTime.TryParse(reader.GetString(4), out var dt) ? dt : DateTime.UtcNow,
                VisitCount = reader.GetInt32(5)
            });
        }
        return list;
    }

    public void ClearHistory(string profileId)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM History WHERE ProfileId = $pid;";
        cmd.Parameters.AddWithValue("$pid", profileId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteHistoryEntry(long historyId)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM History WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", historyId);
        cmd.ExecuteNonQuery();
    }

    public void AddBookmark(string profileId, string title, string url, string folderName = "Bookmarks Bar")
    {
        var conn = GetConnection();
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM Bookmarks WHERE ProfileId = $pid AND Url = $url;";
        checkCmd.Parameters.AddWithValue("$pid", profileId);
        checkCmd.Parameters.AddWithValue("$url", url);
        long existing = (long)(checkCmd.ExecuteScalar() ?? 0L);
        if (existing > 0)
        {
            return;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Bookmarks (ProfileId, FolderName, Title, Url, CreatedAt)
            VALUES ($pid, $folder, $title, $url, $createdAt);
            """;
        cmd.Parameters.AddWithValue("$pid", profileId);
        cmd.Parameters.AddWithValue("$folder", folderName);
        cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? url : title);
        cmd.Parameters.AddWithValue("$url", url);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void RemoveBookmark(long bookmarkId)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Bookmarks WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", bookmarkId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<BookmarkEntry> GetBookmarks(string profileId)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ProfileId, FolderName, Title, Url, FaviconBlob, CreatedAt
            FROM Bookmarks
            WHERE ProfileId = $pid
            ORDER BY CreatedAt DESC;
            """;
        cmd.Parameters.AddWithValue("$pid", profileId);

        var list = new List<BookmarkEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new BookmarkEntry
            {
                Id = reader.GetInt64(0),
                ProfileId = reader.GetString(1),
                FolderName = reader.GetString(2),
                Title = reader.GetString(3),
                Url = reader.GetString(4),
                FaviconBlob = reader.IsDBNull(5) ? null : (byte[])reader[5],
                CreatedAt = DateTime.TryParse(reader.GetString(6), out var dt) ? dt : DateTime.UtcNow
            });
        }
        return list;
    }

    public bool IsBookmarked(string profileId, string url)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Bookmarks WHERE ProfileId = $pid AND Url = $url;";
        cmd.Parameters.AddWithValue("$pid", profileId);
        cmd.Parameters.AddWithValue("$url", url);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    public IReadOnlyList<string> GetWhitelistedDomains()
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Domain FROM AdBlockDomainRules WHERE IsWhitelisted = 1 ORDER BY Domain ASC;";

        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    public void SetDomainWhitelist(string domain, bool isWhitelisted)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return;
        }

        string clean = domain.Trim().ToLowerInvariant();
        if (clean.StartsWith("www.", StringComparison.Ordinal))
        {
            clean = clean[4..];
        }

        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AdBlockDomainRules (Domain, IsWhitelisted)
            VALUES ($domain, $whitelisted)
            ON CONFLICT(Domain) DO UPDATE SET
                IsWhitelisted = excluded.IsWhitelisted;
            """;
        cmd.Parameters.AddWithValue("$domain", clean);
        cmd.Parameters.AddWithValue("$whitelisted", isWhitelisted ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void UpsertDownload(DownloadItem item)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Downloads (Id, ProfileId, Url, FileName, SavePath, TotalBytes, DownloadedBytes, Status, MediaType, HeadersJson, CreatedAt)
            VALUES ($id, $pid, $url, $fileName, $savePath, $total, $downloaded, $status, $mediaType, $headers, $createdAt)
            ON CONFLICT(Id) DO UPDATE SET
                FileName = excluded.FileName,
                SavePath = excluded.SavePath,
                TotalBytes = excluded.TotalBytes,
                DownloadedBytes = excluded.DownloadedBytes,
                Status = excluded.Status;
            """;
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$pid", item.ProfileId);
        cmd.Parameters.AddWithValue("$url", item.Url);
        cmd.Parameters.AddWithValue("$fileName", item.FileName);
        cmd.Parameters.AddWithValue("$savePath", item.SavePath);
        cmd.Parameters.AddWithValue("$total", item.TotalBytes);
        cmd.Parameters.AddWithValue("$downloaded", item.DownloadedBytes);
        cmd.Parameters.AddWithValue("$status", item.Status);
        cmd.Parameters.AddWithValue("$mediaType", item.MediaType);
        cmd.Parameters.AddWithValue("$headers", (object?)item.HeadersJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<DownloadItem> GetDownloads(string profileId)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ProfileId, Url, FileName, SavePath, TotalBytes, DownloadedBytes, Status, MediaType, HeadersJson, CreatedAt
            FROM Downloads
            WHERE ProfileId = $pid
            ORDER BY CreatedAt DESC;
            """;
        cmd.Parameters.AddWithValue("$pid", profileId);

        var list = new List<DownloadItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new DownloadItem
            {
                Id = reader.GetString(0),
                ProfileId = reader.GetString(1),
                Url = reader.GetString(2),
                FileName = reader.GetString(3),
                SavePath = reader.GetString(4),
                TotalBytes = reader.GetInt64(5),
                DownloadedBytes = reader.GetInt64(6),
                Status = reader.GetString(7),
                MediaType = reader.GetString(8),
                HeadersJson = reader.IsDBNull(9) ? null : reader.GetString(9),
                CreatedAt = DateTime.TryParse(reader.GetString(10), out var dt) ? dt : DateTime.UtcNow
            });
        }
        return list;
    }

    public void DeleteDownload(string downloadId)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Downloads WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", downloadId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<VpnNode> GetVpnNodes()
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, CountryCode, Protocol, ConfigJson, LatencyMs, IsDefault
            FROM VpnNodes
            ORDER BY IsDefault DESC, Name ASC;
            """;

        var list = new List<VpnNode>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new VpnNode
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                CountryCode = reader.GetString(2),
                Protocol = reader.GetString(3),
                ConfigJson = reader.GetString(4),
                LatencyMs = reader.GetInt32(5),
                IsDefault = reader.GetInt32(6) != 0
            });
        }
        return list;
    }

    public void UpsertVpnNode(VpnNode node)
    {
        var conn = GetConnection();
        if (node.IsDefault)
        {
            using var resetCmd = conn.CreateCommand();
            resetCmd.CommandText = "UPDATE VpnNodes SET IsDefault = 0;";
            resetCmd.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO VpnNodes (Id, Name, CountryCode, Protocol, ConfigJson, LatencyMs, IsDefault)
            VALUES ($id, $name, $country, $protocol, $config, $latency, $isDefault)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                CountryCode = excluded.CountryCode,
                Protocol = excluded.Protocol,
                ConfigJson = excluded.ConfigJson,
                LatencyMs = excluded.LatencyMs,
                IsDefault = excluded.IsDefault;
            """;
        cmd.Parameters.AddWithValue("$id", node.Id);
        cmd.Parameters.AddWithValue("$name", node.Name);
        cmd.Parameters.AddWithValue("$country", node.CountryCode);
        cmd.Parameters.AddWithValue("$protocol", node.Protocol);
        cmd.Parameters.AddWithValue("$config", node.ConfigJson);
        cmd.Parameters.AddWithValue("$latency", node.LatencyMs);
        cmd.Parameters.AddWithValue("$isDefault", node.IsDefault ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void DeleteVpnNode(string nodeId)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM VpnNodes WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", nodeId);
        cmd.ExecuteNonQuery();
    }

    public string? GetVpnSetting(string key)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SettingValue FROM VpnSettings WHERE SettingKey = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : Convert.ToString(result);
    }

    public void SetVpnSetting(string key, string value)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO VpnSettings (SettingKey, SettingValue)
            VALUES ($key, $val)
            ON CONFLICT(SettingKey) DO UPDATE SET
                SettingValue = excluded.SettingValue;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$val", value);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection GetConnection()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("Vault chưa được mở khóa.");
        }
        return _connection;
    }

    public void Close()
    {
        if (_connection is not null)
        {
            _connection.Close();
            _connection.Dispose();
            _connection = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Close();
    }
}
