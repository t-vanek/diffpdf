using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>Display formatting: a similarity that never rounds a differing pair up to "100 %", and Czech plurals.</summary>
public class FormatTests
{
    [Fact]
    public void Percent_keeps_exact_100_but_never_rounds_a_lower_value_up_to_it()
    {
        Assert.Equal("100 %", Format.Percent(1.0));
        Assert.Equal("100 %", Format.Percent(1.5));      // >= 1.0 → "100 %"
        Assert.NotEqual("100 %", Format.Percent(0.996)); // the analyst trap: a differing pair must NOT read "100 %"
        Assert.StartsWith("99", Format.Percent(0.996));
    }

    [Theory]
    [InlineData(1, "1 odlišný")]
    [InlineData(2, "2 odlišné")]
    [InlineData(4, "4 odlišné")]
    [InlineData(5, "5 odlišných")]
    [InlineData(0, "0 odlišných")]
    public void Plural_agrees_in_czech(int n, string expected) =>
        Assert.Equal(expected, Format.Plural(n, "odlišný", "odlišné", "odlišných"));
}
