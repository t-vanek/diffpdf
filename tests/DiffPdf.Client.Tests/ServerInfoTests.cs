using DiffPdf.Client;

namespace DiffPdf.Client.Tests;

/// <summary>
/// Verifies the anonymous <c>/health</c> surface the desktop client relies on for auto-connect: the SDK's
/// <see cref="DiffPdfClient.GetServerInfoAsync"/> reads the liveness body, including the <c>authEnabled</c>
/// flag that drives whether the client prompts for credentials. The in-memory factory runs anonymously,
/// so the flag is false here.
/// </summary>
public class ServerInfoTests(InMemoryApiFactory factory) : IClassFixture<InMemoryApiFactory>
{
    [Fact]
    public async Task GetServerInfo_ReadsHealthBody_WithAuthDisabled()
    {
        var diff = new DiffPdfClient(factory.CreateClient());

        var info = await diff.GetServerInfoAsync();

        Assert.NotNull(info);
        Assert.Equal("healthy", info!.Status);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.False(info.AuthEnabled); // auth is off (in-memory fallback, no relational DB)
    }
}
