using DiffPdf.Core.Models;
using DiffPdf.Core.Security;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Security;

/// <summary>The created key plus its one-time plaintext (never persisted; shown to the caller once).</summary>
public sealed record ApiKeyCreated(ApiKey Key, string Plaintext);

/// <summary>Manages third-party API keys: generation/hashing, lifecycle and security audit.</summary>
public interface IApiKeyService
{
    Task<ApiKeyCreated> CreateAsync(string name, Role role, DateTimeOffset? expiresAt, string? actor, CancellationToken ct = default);
    Task<ApiKey?> UpdateAsync(Guid id, string? name, Role? role, bool? enabled, DateTimeOffset? expiresAt, long version, string? actor, CancellationToken ct = default);
    Task<ApiKey?> RevokeAsync(Guid id, string? actor, CancellationToken ct = default);
    Task<ApiKey?> DeleteAsync(Guid id, string? actor, CancellationToken ct = default);
    Task<IReadOnlyList<ApiKey>> ListAsync(bool includeDeleted, CancellationToken ct = default);
    Task<ApiKey?> GetAsync(Guid id, CancellationToken ct = default);
}

public sealed class ApiKeyService(IApiKeyStore store, IAuditLogStore audit, ILogger<ApiKeyService> logger) : IApiKeyService
{
    public async Task<ApiKeyCreated> CreateAsync(string name, Role role, DateTimeOffset? expiresAt, string? actor, CancellationToken ct = default)
    {
        var (plaintext, hash, prefix) = ApiKeyHasher.Generate();
        var created = await store.CreateAsync(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            KeyHash = hash,
            Prefix = prefix,
            Role = role,
            Enabled = true,
            ExpiresAt = expiresAt,
            CreatedBy = actor,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);

        await AuditAsync("apikey.created", created.Id, actor, $"name={created.Name}; role={created.Role}", ct);
        logger.LogInformation("API key {Id} '{Name}' created (role {Role}) by {Actor}", created.Id, created.Name, created.Role, actor);
        return new ApiKeyCreated(created, plaintext);
    }

    public async Task<ApiKey?> UpdateAsync(Guid id, string? name, Role? role, bool? enabled, DateTimeOffset? expiresAt, long version, string? actor, CancellationToken ct = default)
    {
        var existing = await store.GetAsync(id, ct);
        if (existing is null || existing.IsDeleted) return null;

        var updated = existing with
        {
            Name = name?.Trim() is { Length: > 0 } n ? n : existing.Name,
            Role = role ?? existing.Role,
            Enabled = enabled ?? existing.Enabled,
            ExpiresAt = expiresAt ?? existing.ExpiresAt,
            UpdatedBy = actor,
        };
        var saved = await store.UpdateAsync(updated, version, ct);
        await AuditAsync("apikey.updated", id, actor, $"role={saved.Role}; enabled={saved.Enabled}", ct);
        return saved;
    }

    public async Task<ApiKey?> RevokeAsync(Guid id, string? actor, CancellationToken ct = default)
    {
        var key = await store.RevokeAsync(id, actor, ct);
        if (key is not null) await AuditAsync("apikey.revoked", id, actor, null, ct);
        return key;
    }

    public async Task<ApiKey?> DeleteAsync(Guid id, string? actor, CancellationToken ct = default)
    {
        var key = await store.SoftDeleteAsync(id, actor, ct);
        if (key is not null) await AuditAsync("apikey.deleted", id, actor, null, ct);
        return key;
    }

    public Task<IReadOnlyList<ApiKey>> ListAsync(bool includeDeleted, CancellationToken ct = default) =>
        store.ListAsync(new ApiKeyQuery { IncludeDeleted = includeDeleted }, ct);

    public Task<ApiKey?> GetAsync(Guid id, CancellationToken ct = default) => store.GetAsync(id, ct);

    private Task AuditAsync(string action, Guid id, string? actor, string? detail, CancellationToken ct) =>
        audit.AddAsync(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Action = action,
            EntityType = "ApiKey",
            EntityId = id.ToString(),
            Actor = actor,
            Source = nameof(JobSource.RestApi),
            Detail = detail,
        }, ct);
}
