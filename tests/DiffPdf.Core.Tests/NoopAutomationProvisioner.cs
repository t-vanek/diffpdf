using DiffPdf.Application.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>No-op <see cref="IAutomationProvisioner"/> for tests that construct services depending on it
/// but do not exercise auto-provisioning (e.g. the scope-sync tests).</summary>
internal sealed class NoopAutomationProvisioner : IAutomationProvisioner
{
    public Task EnsureBranchAutomationsAsync(string branchKey, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveBranchAutomationsAsync(string branchKey, CancellationToken ct = default) => Task.CompletedTask;
    public Task ProvisionBaselineAndExistingAsync(CancellationToken ct = default) => Task.CompletedTask;
}
