using System.Net.Mail;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Application.Subscriptions;

/// <summary>Fields for creating/updating an e-mail notification rule (the Version for an update is passed separately).</summary>
public sealed record SubscriptionInput(
    string Name,
    IReadOnlyList<string> Recipients,
    IReadOnlyList<NotificationEvent> Events,
    IReadOnlyList<string> BranchKeys,
    IReadOnlyList<string> InstanceKeys,
    bool Enabled);

/// <summary>Invalid rule input (no/invalid recipient, no events). Maps to 400 at the endpoint.</summary>
public sealed class SubscriptionValidationException(string message) : Exception(message);

/// <summary>
/// E-mail notification-rule CRUD. Validation and normalization live here; a concurrency conflict on update
/// propagates and is mapped to 409 by the endpoint. A missing rule is a null/false return.
/// </summary>
public interface ISubscriptionService
{
    Task<IReadOnlyList<NotificationSubscription>> ListAsync(CancellationToken ct = default);
    Task<NotificationSubscription?> GetAsync(Guid id, CancellationToken ct = default);
    Task<NotificationSubscription> CreateAsync(SubscriptionInput input, CancellationToken ct = default);
    Task<NotificationSubscription?> UpdateAsync(Guid id, SubscriptionInput input, long version, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class SubscriptionService(ISubscriptionStore store) : ISubscriptionService
{
    public async Task<IReadOnlyList<NotificationSubscription>> ListAsync(CancellationToken ct = default) => await store.ListAsync(ct);

    public async Task<NotificationSubscription?> GetAsync(Guid id, CancellationToken ct = default) => await store.GetAsync(id, ct);

    public async Task<NotificationSubscription> CreateAsync(SubscriptionInput input, CancellationToken ct = default)
    {
        Validate(input);
        var sub = new NotificationSubscription
        {
            Id = Guid.NewGuid(),
            Name = input.Name?.Trim() ?? string.Empty,
            Recipients = Normalize(input.Recipients),
            Events = input.Events,
            BranchKeys = Normalize(input.BranchKeys),
            InstanceKeys = Normalize(input.InstanceKeys),
            Enabled = input.Enabled,
        };
        return await store.CreateAsync(sub, ct);
    }

    public async Task<NotificationSubscription?> UpdateAsync(Guid id, SubscriptionInput input, long version, CancellationToken ct = default)
    {
        Validate(input);
        var existing = await store.GetAsync(id, ct);
        if (existing is null) return null;

        var updated = existing with
        {
            Name = input.Name?.Trim() ?? string.Empty,
            Recipients = Normalize(input.Recipients),
            Events = input.Events,
            BranchKeys = Normalize(input.BranchKeys),
            InstanceKeys = Normalize(input.InstanceKeys),
            Enabled = input.Enabled,
        };
        return await store.UpdateAsync(updated, version, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => await store.DeleteAsync(id, ct);

    /// <summary>Trim, drop blanks, de-dupe (case-insensitive, keeping first spelling).</summary>
    private static IReadOnlyList<string> Normalize(IReadOnlyList<string>? items) =>
        items is null
            ? []
            : items.Select(x => x?.Trim() ?? string.Empty)
                   .Where(x => x.Length > 0)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToList();

    private static void Validate(SubscriptionInput input)
    {
        var recipients = Normalize(input.Recipients);
        if (recipients.Count == 0)
            throw new SubscriptionValidationException("At least one recipient e-mail address is required.");
        var invalid = recipients.Where(r => !MailAddress.TryCreate(r, out _)).ToList();
        if (invalid.Count > 0)
            throw new SubscriptionValidationException($"Invalid recipient e-mail address(es): {string.Join(", ", invalid)}.");
        if (input.Events is null || input.Events.Count == 0)
            throw new SubscriptionValidationException("At least one event is required.");
    }
}
