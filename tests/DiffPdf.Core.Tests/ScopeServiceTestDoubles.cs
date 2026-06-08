using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;

namespace DiffPdf.Core.Tests;

/// <summary>Records the realtime events the branch/instance services publish, for assertions.</summary>
internal sealed class CapturingTriggerEvents : ITriggerEventPublisher
{
    public List<TriggerEvent> Events { get; } = [];
    public Task PublishAsync(TriggerEvent evt, CancellationToken ct = default) { Events.Add(evt); return Task.CompletedTask; }
}

/// <summary>No-op scope-configuration provisioning for tests that do not exercise per-scope config rows.</summary>
internal sealed class NoopScopeConfigurationProvisioner : IScopeConfigurationProvisioner
{
    public Task EnsureGlobalAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureBranchConfigAsync(Guid branchId, CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureInstanceConfigAsync(Guid branchId, Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveBranchConfigAsync(Guid branchId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveInstanceConfigAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ProvisionExistingAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>No-op trigger provisioning for tests that do not exercise default-trigger creation.</summary>
internal sealed class NoopTriggerProvisioner : ITriggerProvisioner
{
    public Task EnsureDefaultTriggerAsync(Guid branchId, string branchKey, Guid instanceId, string instanceKey, string? actor, CancellationToken ct = default) => Task.CompletedTask;
    public Task SoftDeleteInstanceTriggersAsync(Guid instanceId, string? actor, CancellationToken ct = default) => Task.CompletedTask;
    public Task ProvisionExistingAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>No-op folder structure service; <see cref="EnsureFolderAsync"/> reports success without touching disk.</summary>
internal sealed class NoopInstanceStructureService : IInstanceStructureService
{
    public Task<InstanceStructureReport> InspectAsync(string basePath, string? credentialProfile, bool includeFiles = false, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<InstanceStructureReport> EnsureAsync(string basePath, string? credentialProfile, bool includeFiles = false, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> EnsureFolderAsync(string path, string? credentialProfile, CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>No-op queue dispatcher: empty state, releases nothing (the CRUD tests do not drive the queue).</summary>
internal sealed class NoopBranchQueueDispatcher : IBranchQueueDispatcher
{
    public Task DispatchBranchAsync(Guid branchId, CancellationToken ct = default) => Task.CompletedTask;
    public Task PublishStateAsync(Guid branchId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<BranchQueueState?> GetStateAsync(Guid branchId, CancellationToken ct = default) => Task.FromResult<BranchQueueState?>(null);
    public Task<IReadOnlyDictionary<Guid, BranchQueueState>> GetStatesAsync(IReadOnlyList<Branch> branches, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, BranchQueueState>>(new Dictionary<Guid, BranchQueueState>());
}

/// <summary>No-op share resolver; only the readiness path resolves shares, which these tests do not exercise.</summary>
internal sealed class NoopNetworkShareResolver : INetworkShareResolver
{
    public ResolvedFolder Resolve(string folder, NetworkCredentials? inlineCredentials = null, string? credentialProfile = null) => throw new NotSupportedException();
    public IReadOnlyList<ShareInfo> ListShares() => [];
    public IReadOnlyList<string> ListCredentialProfiles() => [];
}

/// <summary>No-op discovery service; only the readiness path previews pairing, which these tests do not exercise.</summary>
internal sealed class NoopNetworkDiscoveryService : INetworkDiscoveryService
{
    public Task<PairingPreview> PreviewPairingAsync(
        string oldFolder, string newFolder,
        NetworkCredentials? oldInlineCredentials = null, string? oldCredentialProfile = null,
        NetworkCredentials? newInlineCredentials = null, string? newCredentialProfile = null,
        string searchPattern = "*.pdf", bool recursive = true, int sampleSize = 20, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
