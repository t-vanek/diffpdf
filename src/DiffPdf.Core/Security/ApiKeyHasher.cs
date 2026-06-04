using System.Security.Cryptography;
using System.Text;

namespace DiffPdf.Core.Security;

/// <summary>
/// Generates and hashes third-party API keys. Keys are high-entropy random tokens, so a fast SHA-256
/// (not a slow password hash) is sufficient. Only the hash + a short display prefix are ever stored.
/// </summary>
public static class ApiKeyHasher
{
    /// <summary>Human-recognizable prefix on every generated key.</summary>
    public const string KeyPrefix = "dpdf_";

    /// <summary>Generates a new key: the one-time plaintext, its SHA-256 hash, and a short display prefix.</summary>
    public static (string Plaintext, string Hash, string Prefix) Generate()
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var plaintext = KeyPrefix + token;
        var display = plaintext[..Math.Min(12, plaintext.Length)];
        return (plaintext, Hash(plaintext), display);
    }

    /// <summary>SHA-256 (lowercase hex) of a plaintext key — the value stored and looked up at auth time.</summary>
    public static string Hash(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
