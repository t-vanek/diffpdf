using DiffPdf.Core.Comparison;

namespace DiffPdf.Core.Tests;

public class UncPathTests
{
    [Theory]
    [InlineData(@"\\server\share\dir", true)]
    [InlineData("//server/share/dir", true)]
    [InlineData(@"C:\local\dir", false)]
    [InlineData("/mnt/share/dir", false)]
    [InlineData("", false)]
    public void IsUnc_RecognizesNetworkPaths(string path, bool expected) =>
        Assert.Equal(expected, UncPath.IsUnc(path));

    [Fact]
    public void Split_BackslashUnc()
    {
        var (server, share, sub) = UncPath.Split(@"\\fileserver\reports\old\2026");
        Assert.Equal("fileserver", server);
        Assert.Equal("reports", share);
        Assert.Equal("old/2026", sub);
    }

    [Fact]
    public void Split_ForwardSlashUnc_NoSubPath()
    {
        var (server, share, sub) = UncPath.Split("//fileserver/reports");
        Assert.Equal("fileserver", server);
        Assert.Equal("reports", share);
        Assert.Equal("", sub);
    }

    [Fact]
    public void Split_NonUnc_Throws() =>
        Assert.Throws<ArgumentException>(() => UncPath.Split(@"C:\local"));
}
