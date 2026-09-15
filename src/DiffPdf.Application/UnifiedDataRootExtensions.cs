using DiffPdf.Application.Abstractions;
using DiffPdf.Application.Files;
using DiffPdf.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Application;

/// <summary>Opt-in single-root configuration. Legacy installations keep their existing paths until migrated.</summary>
public static class UnifiedDataRootExtensions
{
    public static IServiceCollection AddUnifiedDataRoot(this IServiceCollection services, string? configured)
    {
        _ = NormalizeRoot(configured);
        return services.AddUnifiedDataRoot(() => configured);
    }

    /// <summary>Reads the final configuration when options are resolved, after host configuration providers are installed.</summary>
    public static IServiceCollection AddUnifiedDataRoot(this IServiceCollection services, Func<string?> configured)
    {
        // PostConfigure runs after every legacy options binding, regardless of registration order.
        services.PostConfigure<ScopeSyncOptions>(o =>
        {
            if (NormalizeRoot(configured()) is { } root) o.RootPath = Path.Combine(root, "data");
        });
        services.PostConfigure<StorageOptions>(o =>
        {
            if (NormalizeRoot(configured()) is { } root) o.RootPath = Path.Combine(root, "storage");
        });
        services.PostConfigure<FileManagerOptions>(o =>
        {
            if (NormalizeRoot(configured()) is null) return;
            o.RootPath = ""; // use the same path AND credential profile as ScopeSync
            o.RootConfigurationKey = "DataRoot";
        });
        return services;
    }

    private static string? NormalizeRoot(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        string root = configured.Trim();
        if (!Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("DataRoot must be an absolute local or UNC directory path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
}
