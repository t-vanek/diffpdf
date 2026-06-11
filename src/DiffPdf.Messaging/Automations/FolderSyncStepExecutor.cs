using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Mirrors files from a configured <c>source</c> folder into each in-scope instance's <c>new/</c> (or
/// <c>old/</c>) folder, so an external drop location feeds the comparison inputs. The source is a local path,
/// a UNC path or a <c>share:&lt;name&gt;</c> alias and may contain <c>{branchKey}</c> / <c>{instanceKey}</c>
/// placeholders so one automation fans out across a branch. Files are copied when missing, larger/smaller, or
/// newer at the source; with <c>mirror=true</c> files absent at the source are also deleted from the target.
/// Both ends are reached through the same resolver/connector (credential profiles) the comparison pipeline
/// uses. Parameters: <c>source</c> (required), <c>target</c> (<c>new</c>/<c>old</c>, default new),
/// <c>pattern</c> (default <c>*.pdf</c>), <c>recursive</c> (false), <c>mirror</c> (false),
/// <c>credentialProfile</c> (optional, for the source).
/// </summary>
public sealed class FolderSyncStepExecutor(
    IBranchStore branches,
    IInstanceStore instances,
    INetworkShareResolver resolver,
    INetworkShareConnector connector,
    ILogger<FolderSyncStepExecutor> logger) : IAutomationStepExecutor
{
    public AutomationStepType Type => AutomationStepType.FolderSync;

    public async Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct)
    {
        string sourceTemplate = StepParameters.GetString(step, "source", string.Empty);
        if (string.IsNullOrWhiteSpace(sourceTemplate))
            return StepResult.Failed("missing 'source' parameter.");

        string targetFolderName = StepParameters.GetString(step, "target", InstanceFolders.NewName).ToLowerInvariant();
        if (targetFolderName != InstanceFolders.NewName && targetFolderName != InstanceFolders.OldName)
            return StepResult.Failed($"invalid 'target' '{targetFolderName}' (expected new or old).");

        string pattern = StepParameters.GetString(step, "pattern", "*.pdf");
        bool recursive = StepParameters.GetBool(step, "recursive", false);
        bool mirror = StepParameters.GetBool(step, "mirror", false);
        string? sourceProfile = step.Parameters.TryGetValue("credentialProfile", out var p) && !string.IsNullOrWhiteSpace(p) ? p.Trim() : null;

        var targets = await AutomationScope.ResolveEnabledInstancesAsync(automation, branches, instances, ct);
        if (targets.Count == 0)
            return StepResult.Warning("No instances in scope.");

        int totalCopied = 0, totalDeleted = 0, synced = 0;
        var problems = new List<string>();

        foreach (var (branchKey, instance) in targets)
        {
            ct.ThrowIfCancellationRequested();
            string sourcePath = sourceTemplate
                .Replace("{branchKey}", branchKey, StringComparison.Ordinal)
                .Replace("{instanceKey}", instance.Key, StringComparison.Ordinal);
            string targetPath = targetFolderName == InstanceFolders.OldName
                ? InstanceFolders.Old(instance.BasePath)
                : InstanceFolders.New(instance.BasePath);

            try
            {
                var srcResolved = resolver.Resolve(sourcePath, inlineCredentials: null, credentialProfile: sourceProfile ?? instance.CredentialProfile);
                var dstResolved = resolver.Resolve(targetPath, inlineCredentials: null, credentialProfile: instance.CredentialProfile);

                using var srcConn = connector.Connect(srcResolved.Path, srcResolved.Credentials);
                using var dstConn = connector.Connect(dstResolved.Path, dstResolved.Credentials);

                if (!Directory.Exists(srcConn.Path))
                {
                    problems.Add($"{branchKey}/{instance.Key}: source '{srcResolved.Path}' not found.");
                    continue;
                }

                var (copied, deleted) = SyncDirectory(srcConn.Path, dstConn.Path, pattern, recursive, mirror, ct);
                totalCopied += copied;
                totalDeleted += deleted;
                synced++;
                if (copied > 0 || deleted > 0)
                    logger.LogInformation(
                        "FolderSync {Branch}/{Instance}: copied {Copied}, deleted {Deleted} ({Source} -> {Target}).",
                        branchKey, instance.Key, copied, deleted, srcResolved.Path, targetFolderName);
            }
            catch (NetworkConfigurationException ex)
            {
                problems.Add($"{branchKey}/{instance.Key}: {ex.Message}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FolderSync failed for {Branch}/{Instance}.", branchKey, instance.Key);
                problems.Add($"{branchKey}/{instance.Key}: {ex.Message}");
            }
        }

        string summary = $"synced {synced}/{targets.Count} instance(s): copied {totalCopied}, deleted {totalDeleted}.";
        return problems.Count == 0
            ? StepResult.Ok(summary)
            : StepResult.Warning($"{summary} {problems.Count} problem(s):\n  - {string.Join("\n  - ", problems)}");
    }

    // Copies changed files from source into target; with mirror, removes target files absent at the source.
    private static (int Copied, int Deleted) SyncDirectory(
        string source, string target, string pattern, bool recursive, bool mirror, CancellationToken ct)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        Directory.CreateDirectory(target);

        int copied = 0;
        var sourceRelatives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(source, pattern, option))
        {
            ct.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file);
            sourceRelatives.Add(relative);
            string destFile = Path.Combine(target, relative);

            if (IsChanged(file, destFile))
            {
                string? destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);
                File.Copy(file, destFile, overwrite: true);
                copied++;
            }
        }

        int deleted = 0;
        if (mirror)
        {
            foreach (var destFile in Directory.EnumerateFiles(target, pattern, option))
            {
                ct.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(target, destFile);
                if (!sourceRelatives.Contains(relative))
                {
                    File.Delete(destFile);
                    deleted++;
                }
            }
        }

        return (copied, deleted);
    }

    private static bool IsChanged(string source, string dest)
    {
        if (!File.Exists(dest))
            return true;
        var s = new FileInfo(source);
        var d = new FileInfo(dest);
        return s.Length != d.Length || s.LastWriteTimeUtc > d.LastWriteTimeUtc;
    }
}
