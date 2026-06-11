using DiffPdf.Core.Storage;

namespace DiffPdf.Core.Tests;

public class VirtualPathTests
{
    private const string Root = @"C:\diffpdf-root";

    [Theory]
    [InlineData("smlouva.pdf", true)]
    [InlineData("Faktury 2026", true)]
    [InlineData("příliš žluťoučký kůň.pdf", true)]
    [InlineData("a..b.pdf", true)]   // ".." inside a name is a legal Windows name — only the exact segment is traversal
    [InlineData(".gitignore", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData(@"a\b", false)]
    [InlineData("a:b", false)]
    [InlineData("a*b", false)]
    [InlineData("a?b", false)]
    [InlineData("a<b>", false)]
    [InlineData("a|b", false)]
    [InlineData("name.", false)]     // Windows strips a trailing dot — the stored name would differ
    [InlineData("name ", false)]
    [InlineData(" name", false)]
    [InlineData("CON", false)]
    [InlineData("con.pdf", false)]   // reserved device names are reserved with any extension
    [InlineData("LPT3", false)]
    [InlineData("LPT3.pdf", false)]
    [InlineData("CONSOLE.pdf", true)] // only the exact device names are reserved
    public void IsValidName(string name, bool expected) =>
        Assert.Equal(expected, VirtualPath.IsValidName(name));

    [Fact]
    public void IsValidName_RejectsOverlongNames()
    {
        Assert.True(VirtualPath.IsValidName(new string('x', VirtualPath.MaxNameLength)));
        Assert.False(VirtualPath.IsValidName(new string('x', VirtualPath.MaxNameLength + 1)));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("a", "a")]
    [InlineData("a/b/c.pdf", "a/b/c.pdf")]
    [InlineData(@"a\b\c.pdf", "a/b/c.pdf")]   // backslash input is normalized
    [InlineData("a//b", "a/b")]               // duplicate separators collapse
    [InlineData("a/b/", "a/b")]
    public void TryNormalize_AcceptsAndCanonicalises(string? input, string expected)
    {
        Assert.True(VirtualPath.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("..")]
    [InlineData("/rooted")]
    [InlineData(@"\rooted")]
    [InlineData(@"C:\absolute")]
    [InlineData("C:relative-to-drive")]
    [InlineData(@"\\server\share")]
    [InlineData("a/b:c")]
    [InlineData("a/name. /b")]
    public void TryNormalize_RejectsTraversalAndRootedPaths(string input) =>
        Assert.False(VirtualPath.TryNormalize(input, out _));

    [Fact]
    public void TryNormalize_RejectsTooManySegments()
    {
        string ok = string.Join('/', Enumerable.Repeat("a", VirtualPath.MaxSegments));
        string tooMany = string.Join('/', Enumerable.Repeat("a", VirtualPath.MaxSegments + 1));
        Assert.True(VirtualPath.TryNormalize(ok, out _));
        Assert.False(VirtualPath.TryNormalize(tooMany, out _));
    }

    [Fact]
    public void TryResolve_RootItself()
    {
        Assert.True(VirtualPath.TryResolve(Root, "", out var abs, out var normalized));
        Assert.Equal(Root, abs);
        Assert.Equal("", normalized);
    }

    [Fact]
    public void TryResolve_NestedPath()
    {
        Assert.True(VirtualPath.TryResolve(Root, "faktury/2026/smlouva.pdf", out var abs, out var normalized));
        Assert.Equal(@"C:\diffpdf-root\faktury\2026\smlouva.pdf", abs);
        Assert.Equal("faktury/2026/smlouva.pdf", normalized);
    }

    [Fact]
    public void TryResolve_TrailingSeparatorOnRootIsHarmless()
    {
        Assert.True(VirtualPath.TryResolve(Root + @"\", "a", out var abs, out _));
        Assert.Equal(@"C:\diffpdf-root\a", abs);
    }

    [Fact]
    public void TryResolve_UncRoot()
    {
        Assert.True(VirtualPath.TryResolve(@"\\server\share\diffpdf", "a/b.pdf", out var abs, out _));
        Assert.Equal(@"\\server\share\diffpdf\a\b.pdf", abs);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData(@"..\escape")]
    [InlineData(@"C:\windows\system32")]
    [InlineData(@"\\server\share")]
    public void TryResolve_RejectsEscapes(string input) =>
        Assert.False(VirtualPath.TryResolve(Root, input, out _, out _));

    [Fact]
    public void TryResolve_RejectsEmptyRoot()
    {
        Assert.False(VirtualPath.TryResolve("", "a", out _, out _));
        Assert.False(VirtualPath.TryResolve("   ", "a", out _, out _));
    }

    [Fact]
    public void TryResolve_SiblingRootWithSharedPrefixDoesNotConfusePrefixCheck()
    {
        // "C:\diffpdf-root-evil" starts with "C:\diffpdf-root" as a raw string — the separator-aware
        // check must not treat it as inside the root. Lexically unreachable via segments anyway.
        Assert.True(VirtualPath.TryResolve(Root, "a", out var abs, out _));
        Assert.StartsWith(Root + @"\", abs, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("a", "")]
    [InlineData("a/b", "a")]
    [InlineData("a/b/c.pdf", "a/b")]
    public void GetParent(string path, string expected) =>
        Assert.Equal(expected, VirtualPath.GetParent(path));

    [Theory]
    [InlineData("", "x.pdf", "x.pdf")]
    [InlineData("a/b", "x.pdf", "a/b/x.pdf")]
    public void Combine(string directory, string name, string expected) =>
        Assert.Equal(expected, VirtualPath.Combine(directory, name));
}
