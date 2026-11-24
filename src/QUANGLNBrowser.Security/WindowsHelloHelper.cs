using System.Security.Cryptography;
using Windows.Security.Credentials.UI;

namespace QUANGLNBrowser.Security;

/// <summary>
/// Tích hợp Windows Hello (UserConsentVerifier) và DPAPI để bảo vệ MasterKey trên thiết bị hiện tại.
/// </summary>
public static class WindowsHelloHelper
{
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            return availability == UserConsentVerifierAvailability.Available;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> RequestVerificationAsync(string message = "Mở khóa QUANGLN Browser")
    {
        try
        {
            var result = await UserConsentVerifier.RequestVerificationAsync(message);
            return result == UserConsentVerificationResult.Verified;
        }
        catch
        {
            return false;
        }
    }

    public static (string ProtectedKeyBase64, string EntropyBase64) ProtectMasterKey(byte[] masterKey)
    {
        byte[] entropy = RandomNumberGenerator.GetBytes(32);
        byte[] protectedBytes = ProtectedData.Protect(masterKey, entropy, DataProtectionScope.CurrentUser);
        return (Convert.ToBase64String(protectedBytes), Convert.ToBase64String(entropy));
    }

    public static byte[] UnprotectMasterKey(string protectedKeyBase64, string entropyBase64)
    {
        byte[] protectedBytes = Convert.FromBase64String(protectedKeyBase64);
        byte[] entropy = Convert.FromBase64String(entropyBase64);
        return ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
    }
}
