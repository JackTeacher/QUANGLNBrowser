using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace QUANGLNBrowser.Security;

/// <summary>
/// Dẫn xuất khóa 256-bit bằng Argon2id (64 MB, 3 iterations, 4 threads) và tạo/kiểm tra verifier hash.
/// </summary>
public static class Argon2Kdf
{
    public const int SaltSize = 32;
    public const int KeySize = 32;
    public const int DegreeOfParallelism = 4;
    public const int MemorySizeKb = 65536; // 64 MB
    public const int Iterations = 3;

    private static readonly byte[] VerifierContext = Encoding.UTF8.GetBytes("QUANGLNBrowser.Vault.Verifier.v1");

    public static byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static byte[] DeriveMasterKey(string secret, byte[] salt)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (salt.Length != SaltSize)
        {
            throw new ArgumentException($"Salt phải đúng {SaltSize} bytes.", nameof(salt));
        }

        byte[] passwordBytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                DegreeOfParallelism = DegreeOfParallelism,
                MemorySize = MemorySizeKb,
                Iterations = Iterations
            };
            return argon2.GetBytes(KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public static string ComputeVerifierHash(byte[] masterKey)
    {
        using var hmac = new HMACSHA256(masterKey);
        byte[] hash = hmac.ComputeHash(VerifierContext);
        return Convert.ToBase64String(hash);
    }

    public static bool VerifyMasterKey(byte[] masterKey, string expectedVerifierBase64)
    {
        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromBase64String(expectedVerifierBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(masterKey);
        byte[] actualBytes = hmac.ComputeHash(VerifierContext);
        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}
