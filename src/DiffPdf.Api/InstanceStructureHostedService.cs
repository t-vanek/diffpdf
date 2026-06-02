using DiffPdf.Core.Network;
using DiffPdf.Persistence;

namespace DiffPdf.Api;

/// <summary>
/// On startup, ensures the <c>old/</c>, <c>new/</c> and <c>reports/</c> skeleton
/// exists for every registered instance. Best-effort: an unreachable base is logged
/// and skipped, never failing startup. No-op when no instances are persisted (the
/// dev in-memory fallback starts empty).
/// </summary>
public sealed class InstanceStructureHostedService(
    IServiceScopeFactory scopeFactory,
    IInstanceStructureService structure,
    ILogger<InstanceStructureHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var branches = scope.ServiceProvider.GetRequiredService<IBranchStore>();
            var instances = scope.ServiceProvider.GetRequiredService<IInstanceStore>();

            int ensured = 0, problems = 0;
            foreach (var branch in await branches.ListAsync(cancellationToken))
            {
                foreach (var instance in await instances.ListAsync(branch.Id, cancellationToken))
                {
                    var report = await structure.EnsureAsync(instance.BasePath, instance.CredentialProfile, ct: cancellationToken);
                    if (report.Ok)
                    {
                        ensured++;
                    }
                    else
                    {
                        problems++;
                        logger.LogWarning("Instance {Branch}/{Instance} structure not OK: {Error}",
                            branch.Key, instance.Key, report.Error ?? "one or more subfolders could not be provisioned");
                    }
                }
            }

            if (ensured > 0 || problems > 0)
                logger.LogInformation(
                    "Instance structure ensured on startup: {Ensured} ok, {Problems} with problems", ensured, problems);
        }
        catch (Exception ex)
        {
            // Provisioning must never block the application from starting.
            logger.LogWarning(ex, "Startup instance-structure scan failed; continuing");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
