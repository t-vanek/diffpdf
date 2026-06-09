using DiffPdf.Application.Subscriptions;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

/// <summary>Unit coverage for the e-mail notification-rule validation + CRUD logic in SubscriptionService.</summary>
public class SubscriptionServiceTests
{
    private static (SubscriptionService Svc, InMemorySubscriptionStore Store) Build()
    {
        var store = new InMemorySubscriptionStore();
        return (new SubscriptionService(store), store);
    }

    private static SubscriptionInput Input(
        IReadOnlyList<string>? recipients = null,
        IReadOnlyList<NotificationEvent>? events = null,
        IReadOnlyList<string>? branches = null,
        IReadOnlyList<string>? instances = null)
        => new("rule", recipients ?? ["qa@example.test"], events ?? [NotificationEvent.Completed],
               branches ?? [], instances ?? [], true);

    [Fact]
    public async Task Create_NoRecipients_Throws()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => svc.CreateAsync(Input(recipients: [])));
    }

    [Fact]
    public async Task Create_InvalidRecipient_Throws()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => svc.CreateAsync(Input(recipients: ["not-an-email"])));
    }

    [Fact]
    public async Task Create_NoEvents_Throws()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => svc.CreateAsync(Input(events: [])));
    }

    [Fact]
    public async Task Create_Valid_Persists_AndDedupesRecipients()
    {
        var (svc, store) = Build();
        var created = await svc.CreateAsync(Input(recipients: ["QA@Example.test", "qa@example.test", "dev@example.test"]));
        Assert.Equal(2, created.Recipients.Count); // case-insensitive de-dupe keeps the first spelling
        Assert.NotNull(await store.GetAsync(created.Id));
    }

    [Fact]
    public async Task Update_Unknown_ReturnsNull()
    {
        var (svc, _) = Build();
        Assert.Null(await svc.UpdateAsync(Guid.NewGuid(), Input(), version: 1));
    }

    [Fact]
    public async Task Delete_Unknown_ReturnsFalse()
    {
        var (svc, _) = Build();
        Assert.False(await svc.DeleteAsync(Guid.NewGuid()));
    }
}
