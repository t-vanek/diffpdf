using DiffPdf.Messaging.ControlPlane;

namespace DiffPdf.Core.Tests;

/// <summary>No-op <see cref="IControlCheckProvisioner"/> for tests that construct services depending on it
/// but do not exercise auto-provisioning (e.g. the scope-sync tests).</summary>
internal sealed class NoopControlCheckProvisioner : IControlCheckProvisioner
{
    public Task EnsureBranchChecksAsync(string branchKey, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveBranchChecksAsync(string branchKey, CancellationToken ct = default) => Task.CompletedTask;
    public Task ProvisionBaselineAndExistingAsync(CancellationToken ct = default) => Task.CompletedTask;
}
