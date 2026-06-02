using DiffPdf.Core.Storage;

namespace DiffPdf.Core.Tests;

public class StorageKeyValidatorTests
{
    [Theory]
    [InlineData("Alfa", true)]
    [InlineData("LamaEnergyAlfa", true)]
    [InlineData("a-b_c.d", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("..", false)]
    [InlineData("../secrets", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a:b", false)]
    public void IsValidKey(string key, bool expected) =>
        Assert.Equal(expected, StorageKeyValidator.IsValidKey(key));

    [Fact]
    public void EnsureValid_ThrowsOnTraversal() =>
        Assert.Throws<ArgumentException>(() => StorageKeyValidator.EnsureValid("../x", "key"));
}
