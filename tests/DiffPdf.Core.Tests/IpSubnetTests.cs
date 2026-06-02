using System.Net;
using DiffPdf.Core.Discovery;

namespace DiffPdf.Core.Tests;

public class IpSubnetTests
{
    [Theory]
    [InlineData("10.0.0.0/8", "10.1.2.3", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("192.168.1.0/24", "192.168.1.42", true)]
    [InlineData("192.168.1.0/24", "192.168.2.42", false)]
    [InlineData("192.168.1.128/25", "192.168.1.200", true)]
    [InlineData("192.168.1.128/25", "192.168.1.100", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("127.0.0.1/32", "127.0.0.1", true)]
    [InlineData("127.0.0.1/32", "127.0.0.2", false)]
    public void Contains_Ipv4(string cidr, string ip, bool expected)
    {
        Assert.True(IpSubnet.TryParse(cidr, out var subnet));
        Assert.Equal(expected, subnet!.Contains(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Ipv4MappedClient_MatchesIpv4Subnet()
    {
        Assert.True(IpSubnet.TryParse("10.0.0.0/8", out var subnet));
        var mapped = IPAddress.Parse("10.1.2.3").MapToIPv6();
        Assert.True(subnet!.Contains(mapped));
    }

    [Theory]
    [InlineData("not-a-subnet")]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("")]
    public void TryParse_RejectsMalformed(string cidr) =>
        Assert.False(IpSubnet.TryParse(cidr, out _));
}
