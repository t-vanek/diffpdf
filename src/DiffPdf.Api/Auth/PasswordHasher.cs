using System.Security.Cryptography;
using System.Text;

namespace DiffPdf.Api.Auth;

/// <summary>
/// PBKDF2 password hashing for interactive-login users, with a constant-time
/// fallback for plaintext (dev) passwords. Hash format:
/// <c>pbkdf2$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;</c>.
/// </summary>
public static class PasswordHasher
{
    private const string Prefix = "pbkdf2$";
    private const int DefaultIterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}{iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Verifies a password against a PBKDF2-encoded hash.</summary>
    public static bool VerifyHash(string password, string encoded)
    {
        if (!encoded.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        string[] parts = encoded.Split('$');
        if (parts.Length != 4
            || !int.TryParse(parts[1], out int iterations)
            || !TryFromBase64(parts[2], out byte[] salt)
            || !TryFromBase64(parts[3], out byte[] expected))
            return false;

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Constant-time comparison of two plaintext strings (dev fallback).</summary>
    public static bool VerifyPlaintext(string password, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(expected));

    private static bool TryFromBase64(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
