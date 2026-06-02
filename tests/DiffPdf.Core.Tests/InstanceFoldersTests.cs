using DiffPdf.Core.Comparison;

namespace DiffPdf.Core.Tests;

public class InstanceFoldersTests
{
    [Fact]
    public void LocalBase_DerivesSubfolders()
    {
        const string b = "/data/lama";
        Assert.Equal(Path.Combine(b, "old"), InstanceFolders.Old(b));
        Assert.Equal(Path.Combine(b, "new"), InstanceFolders.New(b));
        Assert.Equal(Path.Combine(b, "reports"), InstanceFolders.Reports(b));
    }

    [Fact]
    public void UncBase_KeepsBackslashSeparators()
    {
        Assert.Equal(@"\\srv\share\old", InstanceFolders.Old(@"\\srv\share"));
        Assert.Equal(@"\\srv\share\new", InstanceFolders.New(@"\\srv\share\")); // trailing separator trimmed
        Assert.Equal(@"\\srv\share\reports", InstanceFolders.Reports(@"\\srv\share"));
    }
}
