namespace QUANGLNBrowser.Core.Services;

/// <summary>
/// Quản lý đường dẫn chuẩn của QUANGLN Browser trên Windows (dữ liệu người dùng cô lập &amp; binary native).
/// </summary>
public static class AppPaths
{
    public static string AppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QUANGLNBrowser");

    public static string ProfilesRoot => Path.Combine(AppDataRoot, "profiles");

    public static string FiltersRoot => Path.Combine(AppDataRoot, "filters");

    public static string VaultMetaPath => Path.Combine(AppDataRoot, "vault_meta.json");

    public static string GetProfileDirectory(string profileId) =>
        Path.Combine(ProfilesRoot, profileId);

    public static string GetVaultDatabasePath(string profileId) =>
        Path.Combine(GetProfileDirectory(profileId), "vault.db");

    public static string GetCefCacheDirectory(string profileId, string cacheFolderName = "cef_cache") =>
        Path.Combine(GetProfileDirectory(profileId), cacheFolderName);

    public static string GetNativeExePath(string exeName) =>
        Path.Combine(AppContext.BaseDirectory, "native", exeName);

    public static string GetNativeDllPath(string dllName) =>
        Path.Combine(AppContext.BaseDirectory, dllName);
}
