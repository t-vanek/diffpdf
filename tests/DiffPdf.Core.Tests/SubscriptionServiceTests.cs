using DiffPdf.Application.Subscriptions;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

/// <summary>Unit coverage for the subscription validation + CRUD logic moved out of the /subscriptions endpoints into SubscriptionService.</summary>
public class SubscriptionServiceTests
{
    private static (SubscriptionService Svc, InMemorySubscriptionStore Store) Build()
    {
        var store = new InMemorySubscriptionStore();
        return (new SubscriptionService(store), store);
    }

    private static SubscriptionInput Input(
        string channel = "webhook", string target = "https://example.test/hook", IReadOnlyList<NotificationEvent>? events = null)
        => new(channel, target, events ?? [NotificationEvent.Completed], null, null, true);

    [Fact]
    public async Task Create_InvalidChannel_Throws()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => svc.CreateAsync(Input(channel: "carrier-pigeon")));
    }

    [Fact]
    public async Task Create_EmptyTarget_Throws()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => svc.CreateAsync(Input(target: "  ")));
    }

    [Fact]
    public async Task Create_NoEvents_Throws()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => svc.CreateAsync(Input(events: [])));
    }

    [Fact]
    public async Task Create_Valid_Persists_AndLowercasesChannel()
    {
        var (svc, store) = Build();
        var created = await svc.CreateAsync(Input(channel: "WebHook"));
        Assert.Equal("webhook", created.Channel);
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
