using System.Text.RegularExpressions;

namespace DiffPdf.Core.Storage;

/// <summary>
/// Validates business-instance / project keys that become path segments. Keeps
/// keys to a safe whitelist so they can never escape the storage root.
/// </summary>
public static partial class StorageKeyValidator
{
    [GeneratedRegex("^[a-zA-Z0-9_.-]{1,64}$")]
    private static partial Regex AllowedKey();

    public static bool IsValidKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Contains("..", StringComparison.Ordinal)) return false;
        return AllowedKey().IsMatch(value);
    }

    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="value"/> is not a valid key.</summary>
    public static void EnsureValid(string? value, string name)
    {
        if (!IsValidKey(value))
            throw new ArgumentException(
                $"Invalid key '{value}' for {name}: must be 1-64 chars of [a-zA-Z0-9_.-] and contain no '..'.", name);
    }
}
