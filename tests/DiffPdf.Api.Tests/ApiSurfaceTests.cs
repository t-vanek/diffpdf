using DiffPdf.Client;
using DiffPdf.Core.Models;

namespace DiffPdf.Api.Tests;

/// <summary>
/// End-to-end tests of the network/discovery surface, exercised through the
/// DiffPdf.Client SDK against a real (in-memory) server.
/// </summary>
public sealed class ApiSurfaceTests(DiffPdfApiFactory factory) : IClassFixture<DiffPdfApiFactory>
{
    private DiffPdfClient NewClient() => new(factory.CreateClient());

    [Fact]
    public async Task ServerInfo_DescribesTheServer()
    {
        using var client = NewClient();
        var info = await client.GetServerInfoAsync();

        Assert.Equal("diffpdf", info.Service);
        Assert.False(string.IsNullOrEmpty(info.InstanceId));
        Assert.False(info.AuthEnabled);
        Assert.Equal("/hubs/jobs", info.HubPath);
    }

    [Fact]
    public async Task DiscoverFolder_And_Preview_Work()
    {
        var root = CreateTree(out string oldDir, out string newDir);
        try
        {
            using var client = NewClient();

            var folder = await client.DiscoverFolderAsync(oldDir);
            Assert.True(folder.Reachable);
            Assert.Equal(2, folder.FileCount);

            var preview = await client.PreviewPairingAsync(oldDir, newDir);
            Assert.True(preview.Reachable);
            Assert.Equal(1, preview.Matched);
            Assert.Equal(1, preview.OnlyInOld);
            Assert.Equal(1, preview.OnlyInNew);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoverFolder_UnknownShare_ReportsError()
    {
        using var client = NewClient();
        var result = await client.DiscoverFolderAsync("share:ghost/x");
        Assert.False(result.Reachable);
        Assert.Contains("Unknown share", result.Error);
    }

    [Fact]
    public async Task SubmitBatch_EmptyFolders_CompletesWithZeroPairs()
    {
        string oldDir = Path.Combine(Path.GetTempPath(), "diffpdf-b-" + Guid.NewGuid().ToString("N"));
        string newDir = Path.Combine(Path.GetTempPath(), "diffpdf-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);
        try
        {
            using var client = NewClient();
            await client.CreateBusinessInstanceAsync("alfa", "Alfa");
            await client.CreateProjectAsync("alfa", "proj", "Project");

            var job = await client.SubmitBatchAsync(new BatchComparisonRequest
            {
                Scope = new JobScope("alfa", "proj"),
                OldFolder = oldDir,
                NewFolder = newDir,
            });

            Assert.NotEqual(Guid.Empty, job.Id);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var done = await client.WaitForJobAsync(job.Id, pollInterval: TimeSpan.FromMilliseconds(200), ct: cts.Token);

            Assert.Equal("Completed", done.Status);
            Assert.Equal(0, done.TotalCount);
        }
        finally
        {
            Directory.Delete(oldDir, recursive: true);
            Directory.Delete(newDir, recursive: true);
        }
    }

    [Fact]
    public async Task SubmitBatch_UnknownScope_Returns404()
    {
        using var client = NewClient();
        string dir = Path.Combine(Path.GetTempPath(), "diffpdf-s-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ex = await Assert.ThrowsAsync<DiffPdfClientException>(() =>
                client.SubmitBatchAsync(new BatchComparisonRequest
                {
                    Scope = new JobScope("nope", "nope"),
                    OldFolder = dir,
                    NewFolder = dir,
                }));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.StatusCode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CreateTree(out string oldDir, out string newDir)
    {
        string root = Path.Combine(Path.GetTempPath(), "diffpdf-tree-" + Guid.NewGuid().ToString("N"));
        oldDir = Path.Combine(root, "old");
        newDir = Path.Combine(root, "new");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);

        File.WriteAllText(Path.Combine(oldDir, "same.pdf"), "%PDF-1.4");
        File.WriteAllText(Path.Combine(oldDir, "only-old.pdf"), "%PDF-1.4");
        File.WriteAllText(Path.Combine(newDir, "same.pdf"), "%PDF-1.4");
        File.WriteAllText(Path.Combine(newDir, "only-new.pdf"), "%PDF-1.4");
        return root;
    }
}
