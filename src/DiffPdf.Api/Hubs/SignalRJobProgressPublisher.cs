using System.Collections.Concurrent;
using DiffPdf.Core.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace DiffPdf.Api.Hubs;

/// <summary>
/// Fans a <see cref="JobProgressChanged"/> out to the job, instance and branch groups (clients watching that
/// specific job/scope), plus the global "scope" group so the Jobs list updates live regardless of launch source.
/// <para>
/// A big batch (hundreds of fast pairs) emits one event per pair, so everything except the job group is
/// coalesced per job: the <c>job:</c> group gets every tick (it is the open detail view — few subscribers,
/// smooth progress bar); the <c>instance:</c>/<c>branch:</c> list groups get at most one push per
/// <see cref="ListThrottle"/>; the all-clients <c>scope</c> group at most one per <see cref="ScopeThrottle"/>.
/// The first event, a status change and a terminal event always push immediately to every group, so the final
/// counts are never coalesced away. Display-only, so a benign race (an extra push) is harmless.
/// </para>
/// </summary>
public sealed class SignalRJobProgressPublisher(IHubContext<JobsHub> hub) : IJobProgressPublisher
{
    private static readonly TimeSpan ListThrottle = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ScopeThrottle = TimeSpan.FromMilliseconds(750);
    private readonly ConcurrentDictionary<Guid, Tracker> _last = new();

    private sealed record Tracker(DateTimeOffset ListAt, DateTimeOffset ScopeAt, string Status);

    public Task PublishAsync(JobProgressChanged progress, CancellationToken ct = default)
    {
        var (sendLists, sendScope) = Coalesce(progress);

        var tasks = new List<Task>(4)
        {
            hub.Clients.Group($"job:{progress.JobId}").SendAsync("jobProgress", progress, ct),
        };
        if (sendLists)
        {
            tasks.Add(hub.Clients.Group($"instance:{progress.BranchKey}:{progress.InstanceKey}").SendAsync("jobProgress", progress, ct));
            tasks.Add(hub.Clients.Group($"branch:{progress.BranchKey}").SendAsync("jobProgress", progress, ct));
        }
        if (sendScope)
            tasks.Add(hub.Clients.Group("scope").SendAsync("jobProgress", progress, ct));
        return Task.WhenAll(tasks);
    }

    private (bool SendLists, bool SendScope) Coalesce(JobProgressChanged p)
    {
        if (p.Status is "Completed" or "Failed" or "Cancelled")
        {
            _last.TryRemove(p.JobId, out _); // terminal — push it everywhere and stop tracking the job
            return (true, true);
        }

        var now = DateTimeOffset.UtcNow;
        if (!_last.TryGetValue(p.JobId, out var prev) || prev.Status != p.Status)
        {
            _last[p.JobId] = new Tracker(now, now, p.Status); // first event or status change → push everywhere
            return (true, true);
        }

        bool sendLists = now - prev.ListAt >= ListThrottle;
        bool sendScope = now - prev.ScopeAt >= ScopeThrottle;
        if (sendLists || sendScope)
            _last[p.JobId] = new Tracker(sendLists ? now : prev.ListAt, sendScope ? now : prev.ScopeAt, p.Status);
        return (sendLists, sendScope);
    }
}
