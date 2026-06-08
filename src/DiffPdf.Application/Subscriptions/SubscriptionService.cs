using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Application.Subscriptions;

/// <summary>Fields for creating/updating a notification subscription (the Version for an update is passed separately).</summary>
public sealed record SubscriptionInput(
    string Channel,
    string Target,
    IReadOnlyList<NotificationEvent> Events,
    string? BranchKey,
    string? InstanceKey,
    bool Enabled);

/// <summary>Invalid subscription input (unknown channel, empty target, no events). Maps to 400 at the endpoint.</summary>
public sealed class SubscriptionValidationException(string message) : Exception(message);

/// <summary>
/// Notification-subscription CRUD. Validation and channel normalization live here; a concurrency conflict on
/// update propagates and is mapped to 409 by the endpoint. A missing subscription is a null/false return.
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
    private static readonly string[] ValidChannels = ["webhook", "smtp"];

    public async Task<IReadOnlyList<NotificationSubscription>> ListAsync(CancellationToken ct = default) => await store.ListAsync(ct);

    public async Task<NotificationSubscription?> GetAsync(Guid id, CancellationToken ct = default) => await store.GetAsync(id, ct);

    public async Task<NotificationSubscription> CreateAsync(SubscriptionInput input, CancellationToken ct = default)
    {
        Validate(input);
        var sub = new NotificationSubscription
        {
            Id = Guid.NewGuid(),
            Channel = input.Channel.ToLowerInvariant(),
            Target = input.Target,
            Events = input.Events,
            BranchKey = input.BranchKey,
            InstanceKey = input.InstanceKey,
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
            Channel = input.Channel.ToLowerInvariant(),
            Target = input.Target,
            Events = input.Events,
            BranchKey = input.BranchKey,
            InstanceKey = input.InstanceKey,
            Enabled = input.Enabled,
        };
        return await store.UpdateAsync(updated, version, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => await store.DeleteAsync(id, ct);

    private static void Validate(SubscriptionInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Channel) || !ValidChannels.Contains(input.Channel.ToLowerInvariant()))
            throw new SubscriptionValidationException($"Channel must be one of: {string.Join(", ", ValidChannels)}.");
        if (string.IsNullOrWhiteSpace(input.Target))
            throw new SubscriptionValidationException("Target must not be empty.");
        if (input.Events is null || input.Events.Count == 0)
            throw new SubscriptionValidationException("Events must not be empty.");
    }
}
