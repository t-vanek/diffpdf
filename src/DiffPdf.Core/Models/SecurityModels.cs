namespace DiffPdf.Core.Models;

/// <summary>
/// Coarse access role of a principal (OAuth client or API key). Ordered: <see cref="Admin"/> implies
/// <see cref="Operator"/> implies <see cref="Viewer"/>. Maps to the read / write / admin policies.
/// </summary>
public enum Role
{
    /// <summary>Read-only access (GET endpoints, subscribe to live updates).</summary>
    Viewer = 0,
    /// <summary>Read + write: create/update/delete scope entities, run comparisons and triggers.</summary>
    Operator = 1,
    /// <summary>Everything, including security administration (API keys, audit).</summary>
    Admin = 2,
}

/// <summary>
/// A third-party API key principal. Authenticates via the <c>X-Api-Key</c> header. Only the SHA-256
/// <see cref="KeyHash"/> (+ a short display <see cref="Prefix"/>) is stored — the plaintext key is shown
/// once at creation and never persisted. Soft-deleted (never hard-deleted) so audit/history survive.
/// </summary>
public sealed record ApiKey
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>SHA-256 (hex) of the plaintext key; the unique lookup value at authentication time.</summary>
    public required string KeyHash { get; init; }
    /// <summary>First few characters of the plaintext key, kept for display (e.g. <c>dpdf_ab12…</c>).</summary>
    public required string Prefix { get; init; }

    public Role Role { get; init; } = Role.Viewer;

    /// <summary>Whether the key may authenticate. A disabled key is rejected but kept for audit.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Optional expiry; once passed the key is rejected.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Last successful authentication time (best-effort, updated asynchronously).</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Set when the key is explicitly revoked (rejected thereafter, distinct from disabled).</summary>
    public DateTimeOffset? RevokedAt { get; init; }
    public string? RevokedBy { get; init; }

    public bool IsDeleted { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }

    public long Version { get; init; } = 1;

    /// <summary>True when the key can currently authenticate (enabled, not revoked/deleted, not expired).</summary>
    public bool IsUsable(DateTimeOffset now) =>
        Enabled && !IsDeleted && RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}
