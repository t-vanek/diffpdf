using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class NetworkShareResolverTests
{
    private static NetworkShareResolver Resolver(NetworkOptions options) => new(Options.Create(options));

    private static NetworkOptions SampleOptions() => new()
    {
        CredentialProfiles =
        {
            ["corp"] = new NetworkCredentialProfile { Username = "svc_diff", Password = "s3cret", Domain = "CORP" },
        },
        Shares =
        {
            ["reports"] = new NetworkShareDefinition { Root = @"\\fileserver\reports", CredentialProfile = "corp" },
            ["mounted"] = new NetworkShareDefinition { LocalMountPath = "/mnt/reports" },
        },
    };

    [Fact]
    public void LocalPath_PassesThrough_NoCredentials()
    {
        var resolved = Resolver(new NetworkOptions()).Resolve("/data/old");
        Assert.Equal("/data/old", resolved.Path);
        Assert.Null(resolved.Credentials);
        Assert.Null(resolved.ShareName);
    }

    [Fact]
    public void ShareAlias_ExpandsUncRoot_AndAttachesProfileCredentials()
    {
        var resolved = Resolver(SampleOptions()).Resolve("share:reports/baseline/2026");

        Assert.Equal(@"\\fileserver\reports\baseline\2026", resolved.Path);
        Assert.Equal("reports", resolved.ShareName);
        Assert.NotNull(resolved.Credentials);
        Assert.Equal("svc_diff", resolved.Credentials!.Username);
        Assert.Equal(@"CORP\svc_diff", resolved.Credentials.QualifiedUsername);
    }

    [Fact]
    public void ShareAlias_WithoutSubPath_ReturnsRoot()
    {
        var resolved = Resolver(SampleOptions()).Resolve("share:reports");
        Assert.Equal(@"\\fileserver\reports", resolved.Path);
    }

    [Fact]
    public void LocalMountShare_ResolvesToLocalPath_WithoutCredentials()
    {
        var resolved = Resolver(SampleOptions()).Resolve("share:mounted/baseline");

        Assert.Equal(Path.Combine("/mnt/reports", "baseline"), resolved.Path);
        Assert.Null(resolved.Credentials); // local path needs no credentials
    }

    [Fact]
    public void InlineCredentials_WinOverProfile()
    {
        var inline = new NetworkCredentials { Username = "adhoc", Password = "p" };
        var resolved = Resolver(SampleOptions()).Resolve(@"\\srv\s", inline, credentialProfile: "corp");
        Assert.Equal("adhoc", resolved.Credentials!.Username);
    }

    [Fact]
    public void NamedProfile_OnPlainUnc_Resolves()
    {
        var resolved = Resolver(SampleOptions()).Resolve(@"\\srv\share\dir", credentialProfile: "corp");
        Assert.Equal("svc_diff", resolved.Credentials!.Username);
    }

    [Fact]
    public void UnknownShare_Throws()
    {
        var ex = Assert.Throws<NetworkConfigurationException>(() => Resolver(SampleOptions()).Resolve("share:nope/x"));
        Assert.Contains("Unknown share 'nope'", ex.Message);
    }

    [Fact]
    public void UnknownProfile_Throws()
    {
        var ex = Assert.Throws<NetworkConfigurationException>(
            () => Resolver(SampleOptions()).Resolve(@"\\srv\s", credentialProfile: "ghost"));
        Assert.Contains("Unknown credential profile 'ghost'", ex.Message);
    }

    [Fact]
    public void InlineCredentials_RejectedWhenDisabled()
    {
        var options = SampleOptions();
        options.AllowInlineCredentials = false;
        var inline = new NetworkCredentials { Username = "u", Password = "p" };

        Assert.Throws<NetworkConfigurationException>(() => Resolver(options).Resolve(@"\\srv\s", inline));
    }

    [Fact]
    public void ListShares_ReportsCredentialRequirement_WithoutSecrets()
    {
        var shares = Resolver(SampleOptions()).ListShares();

        var reports = Assert.Single(shares, s => s.Name == "reports");
        Assert.True(reports.RequiresCredentials);
        var mounted = Assert.Single(shares, s => s.Name == "mounted");
        Assert.False(mounted.RequiresCredentials);
    }

    [Fact]
    public void ListCredentialProfiles_ReturnsNamesOnly()
    {
        var names = Resolver(SampleOptions()).ListCredentialProfiles();
        Assert.Equal(["corp"], names);
    }
}
