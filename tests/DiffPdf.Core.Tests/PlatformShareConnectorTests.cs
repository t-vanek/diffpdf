using DiffPdf.Pdf.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

public class PlatformShareConnectorTests
{
    private static PlatformShareConnector Connector() => new(NullLogger<PlatformShareConnector>.Instance);

    [Fact]
    public void LocalPath_PassesThrough()
    {
        using var connection = Connector().Connect("/mnt/reports/old", credentials: null);
        Assert.Equal("/mnt/reports/old", connection.Path);
    }

    [Fact]
    public void UncWithoutCredentials_PassesThrough()
    {
        using var connection = Connector().Connect(@"\\server\share\old", credentials: null);
        Assert.Equal(@"\\server\share\old", connection.Path);
    }
}
