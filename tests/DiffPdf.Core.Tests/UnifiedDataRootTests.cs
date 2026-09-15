using DiffPdf.Application;
using DiffPdf.Application.Abstractions;
using DiffPdf.Application.Files;
using DiffPdf.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class UnifiedDataRootTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetRoot_PreservesLegacyConfiguration(string? root)
    {
        var services = new ServiceCollection();
        services.Configure<ScopeSyncOptions>(o => o.RootPath = "legacy-data");
        services.Configure<StorageOptions>(o => o.RootPath = "legacy-storage");
        services.Configure<FileManagerOptions>(o => o.RootPath = "legacy-files");
        services.AddUnifiedDataRoot(root);
        using var provider = services.BuildServiceProvider();
        Assert.Equal("legacy-data", provider.GetRequiredService<IOptions<ScopeSyncOptions>>().Value.RootPath);
        Assert.Equal("legacy-storage", provider.GetRequiredService<IOptions<StorageOptions>>().Value.RootPath);
        Assert.Equal("legacy-files", provider.GetRequiredService<IOptions<FileManagerOptions>>().Value.RootPath);
    }

    [Fact]
    public void ExplicitRoot_OverridesLegacyBindings_WithoutCreatingDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "diffpdf-config-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddUnifiedDataRoot(root + Path.DirectorySeparatorChar);
        services.Configure<ScopeSyncOptions>(o => { o.RootPath = "wrong-data"; o.CredentialProfile = "corp"; });
        services.Configure<StorageOptions>(o => o.RootPath = "wrong-storage");
        services.Configure<FileManagerOptions>(o => o.RootPath = "wrong-files");
        using var provider = services.BuildServiceProvider();
        var scope = provider.GetRequiredService<IOptions<ScopeSyncOptions>>();
        var files = provider.GetRequiredService<IOptions<FileManagerOptions>>();
        Assert.Equal(Path.Combine(root, "data"), scope.Value.RootPath);
        Assert.Equal("corp", scope.Value.CredentialProfile);
        Assert.Equal(Path.Combine(root, "storage"), provider.GetRequiredService<IOptions<StorageOptions>>().Value.RootPath);
        Assert.Equal("", files.Value.RootPath);
        using var manager = new FileManagerService(files, scope);
        var status = manager.GetStatus();
        Assert.Equal("DataRoot", status.ResolvedFrom);
        Assert.Equal(scope.Value.RootPath, status.RootPath);
        Assert.False(status.RootExists);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("relative/data")]
    [InlineData("share:reports")]
    [InlineData(".")]
    public void RelativeRoot_IsRejected(string root) =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUnifiedDataRoot(root));
}
