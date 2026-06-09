using System.Collections.Concurrent;
using DiffPdf.Core.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace DiffPdf.Api.Hubs;

/// <summary>
/// Fans a <see cref="JobProgressChanged"/> out to the job, instance and branch groups (clients watching that
/// specific job/scope), plus the global "scope" group so the Jobs list updates live regardless of launch source.
/// <para>
/// The "scope" broadcast goes to EVERY connected client, so a big batch (hundreds of pairs) would otherwise fan
/// one push per pair to everyone. It is therefore coalesced per job: a status change or a terminal event pushes
/// immediately; routine per-pair progress is throttled to at most one scope push per <see cref="ScopeThrottle"/>
/// (the list's progress bar needs no finer refresh). The job/instance/branch groups always get every tick, so an
/// open job's detail stays smooth, and the terminal event always carries the final counts.
/// </para>
/// </summary>
public sealed class SignalRJobProgressPublisher(IHubContext<JobsHub> hub) : IJobProgressPublisher
{
    private static readonly TimeSpan ScopeThrottle = TimeSpan.FromMilliseconds(750);
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, string Status)> _lastScope = new();

    public Task PublishAsync(JobProgressChanged progress, CancellationToken ct = default)
    {
        var tasks = new List<Task>(4)
        {
            hub.Clients.Group($"job:{progress.JobId}").SendAsync("jobProgress", progress, ct),
            hub.Clients.Group($"instance:{progress.BranchKey}:{progress.InstanceKey}").SendAsync("jobProgress", progress, ct),
            hub.Clients.Group($"branch:{progress.BranchKey}").SendAsync("jobProgress", progress, ct),
        };
        if (ShouldBroadcastToScope(progress))
            tasks.Add(hub.Clients.Group("scope").SendAsync("jobProgress", progress, ct));
        return Task.WhenAll(tasks);
    }

    // Coalesce the all-clients scope broadcast: terminal/status-change pushes now; routine progress at most once
    // per throttle window. Display-only, so a benign race across threads (an extra push) is harmless.
    private bool ShouldBroadcastToScope(JobProgressChanged p)
    {
        if (p.Status is "Completed" or "Failed" or "Cancelled")
        {
            _lastScope.TryRemove(p.JobId, out _); // terminal — push it and stop tracking the job
            return true;
        }
        var now = DateTimeOffset.UtcNow;
        var prev = _lastScope.TryGetValue(p.JobId, out var v) ? v : default;
        if (prev.Status == p.Status && now - prev.At < ScopeThrottle)
            return false; // routine progress within the window → coalesced
        _lastScope[p.JobId] = (now, p.Status);
        return true; // first event, status change, or the throttle window elapsed
    }
}
