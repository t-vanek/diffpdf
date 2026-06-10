using DiffPdf.Core.Models;
using DiffPdf.Messaging.Automations;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

public class AutomationEventSinkTests
{
    private static Automation Auto(
        string key,
        IReadOnlyList<NotificationEvent> triggers,
        AutomationScopeKind scope = AutomationScopeKind.Global,
        string? branchKey = null,
        string? instanceKey = null,
        int debounceSeconds = 60,
        bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Name = key,
        ScopeKind = scope,
        BranchKey = branchKey,
        InstanceKey = instanceKey,
        EventTriggers = triggers,
        EventDebounceSeconds = debounceSeconds,
        Steps = [new AutomationStep { Type = AutomationStepType.Readiness }],
        Enabled = enabled,
    };

    private static (AutomationEventSink Sink, InMemoryAutomationStore Store, RecordingMessageBus Bus) Build()
    {
        var store = new InMemoryAutomationStore();
        var bus = new RecordingMessageBus();
        var sink = new AutomationEventSink(store, bus, NullLogger<AutomationEventSink>.Instance);
        return (sink, store, bus);
    }

    [Fact]
    public async Task MatchingEvent_PublishesRunAutomation()
    {
        var (sink, store, bus) = Build();
        var automation = await store.CreateAsync(Auto("react", [NotificationEvent.Failed]));

        await sink.PublishAsync(NotificationEvent.Failed, "Alfa", "X", "batch failed", null, 0);

        var cmd = Assert.IsType<RunAutomation>(Assert.Single(bus.Published));
        Assert.Equal(automation.Id, cmd.AutomationId);
        Assert.StartsWith("event:Failed", cmd.TriggerDetail);
        Assert.Equal(0, cmd.ChainDepth);
    }

    [Fact]
    public async Task NonMatchingEvent_DoesNothing()
    {
        var (sink, store, bus) = Build();
        await store.CreateAsync(Auto("react", [NotificationEvent.Failed]));

        await sink.PublishAsync(NotificationEvent.Completed, "Alfa", "X", "done", null, 0);

        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task BranchScope_MatchesOwnBranchOnly_CaseInsensitive()
    {
        var (sink, store, bus) = Build();
        await store.CreateAsync(Auto("alfa-only", [NotificationEvent.Failed],
            AutomationScopeKind.Branch, branchKey: "Alfa"));

        await sink.PublishAsync(NotificationEvent.Failed, "ALFA", null, "x", null, 0);
        Assert.Single(bus.Published);

        await sink.PublishAsync(NotificationEvent.Failed, "Beta", null, "x", null, 0);
        Assert.Single(bus.Published); // unchanged — Beta does not match

        await sink.PublishAsync(NotificationEvent.Failed, null, null, "x", null, 0);
        Assert.Single(bus.Published); // unchanged — a global event has no branch
    }

    [Fact]
    public async Task InstanceScope_RequiresBranchAndInstance()
    {
        var (sink, store, bus) = Build();
        await store.CreateAsync(Auto("one-instance", [NotificationEvent.Failed],
            AutomationScopeKind.Instance, branchKey: "Alfa", instanceKey: "X", debounceSeconds: 0));

        await sink.PublishAsync(NotificationEvent.Failed, "Alfa", "Y", "x", null, 0);
        Assert.Empty(bus.Published);

        await sink.PublishAsync(NotificationEvent.Failed, "Alfa", "X", "x", null, 0);
        Assert.Single(bus.Published);
    }

    [Fact]
    public async Task SourceAutomation_NeverTriggersItself()
    {
        var (sink, store, bus) = Build();
        var automation = await store.CreateAsync(Auto("self", [NotificationEvent.HealthDegraded]));

        await sink.PublishAsync(NotificationEvent.HealthDegraded, null, null, "x",
            sourceAutomationId: automation.Id, chainDepth: 1);

        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task Debounce_SuppressesRapidRepeats()
    {
        var (sink, store, bus) = Build();
        await store.CreateAsync(Auto("debounced", [NotificationEvent.Failed], debounceSeconds: 3600));

        await sink.PublishAsync(NotificationEvent.Failed, null, null, "first", null, 0);
        await sink.PublishAsync(NotificationEvent.Failed, null, null, "second", null, 0);

        Assert.Single(bus.Published);
    }

    [Fact]
    public async Task ChainDepth_AboveCap_StopsTheCascade()
    {
        var (sink, store, bus) = Build();
        await store.CreateAsync(Auto("deep", [NotificationEvent.Failed]));

        await sink.PublishAsync(NotificationEvent.Failed, null, null, "x", null,
            chainDepth: AutomationEventSink.MaxChainDepth + 1);

        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task DisabledAutomation_IsNotTriggered()
    {
        var (sink, store, bus) = Build();
        await store.CreateAsync(Auto("off", [NotificationEvent.Failed], enabled: false));

        await sink.PublishAsync(NotificationEvent.Failed, null, null, "x", null, 0);

        Assert.Empty(bus.Published);
    }
}
