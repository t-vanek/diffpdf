using System.Text;
using DiffPdf.Core.Discovery;

namespace DiffPdf.Core.Tests;

public class DiscoveryProtocolTests
{
    [Fact]
    public void Probe_IsRecognized()
    {
        var probe = DiscoveryProtocol.CreateProbe();
        Assert.True(DiscoveryProtocol.IsProbe(probe));
    }

    [Fact]
    public void Probe_WithTrailingPadding_IsRecognized()
    {
        var padded = Encoding.UTF8.GetBytes(DiscoveryProtocol.ProbeMagic + "\n");
        Assert.True(DiscoveryProtocol.IsProbe(padded));
    }

    [Fact]
    public void Garbage_IsNotProbe()
    {
        Assert.False(DiscoveryProtocol.IsProbe("hello"u8));
        Assert.False(DiscoveryProtocol.IsProbe([]));
    }

    [Fact]
    public void Response_RoundTrips()
    {
        var descriptor = new DiffPdfServerDescriptor
        {
            InstanceId = "abc123",
            Name = "build-server",
            Version = "1.2.3",
            BaseUrl = "http://192.168.1.10:8080",
            AuthEnabled = true,
            TokenEndpoint = "/connect/token",
            GrantTypes = ["client_credentials", "authorization_code", "refresh_token"],
        };

        var bytes = DiscoveryProtocol.CreateResponse(descriptor);
        var parsed = DiscoveryProtocol.TryParseResponse(bytes);

        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed!.InstanceId);
        Assert.Equal("build-server", parsed.Name);
        Assert.Equal("http://192.168.1.10:8080", parsed.BaseUrl);
        Assert.True(parsed.AuthEnabled);
        Assert.Equal(3, parsed.GrantTypes.Count);
    }

    [Fact]
    public void TryParseResponse_RejectsNonResponse()
    {
        Assert.Null(DiscoveryProtocol.TryParseResponse(DiscoveryProtocol.CreateProbe()));
        Assert.Null(DiscoveryProtocol.TryParseResponse("DIFFPDF_SERVER_v1 not-json"u8.ToArray()));
        Assert.Null(DiscoveryProtocol.TryParseResponse([1, 2, 3]));
    }

    [Fact]
    public void Descriptor_ComputesApiAndHubUrls()
    {
        var descriptor = new DiffPdfServerDescriptor
        {
            InstanceId = "i", Name = "n", Version = "1", BaseUrl = "http://host:8080/",
        };

        Assert.Equal("http://host:8080/api/v1", descriptor.ApiBaseUrl);
        Assert.Equal("http://host:8080/hubs/jobs", descriptor.HubUrl);
    }
}
