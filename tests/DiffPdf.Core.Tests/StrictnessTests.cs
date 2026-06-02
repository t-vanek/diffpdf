using DiffPdf.Core.Models;

namespace DiffPdf.Core.Tests;

public class StrictnessTests
{
    [Theory]
    [InlineData(Strictness.Exact, 0)]
    [InlineData(Strictness.Strict, 4)]
    [InlineData(Strictness.Balanced, 16)]
    [InlineData(Strictness.Lenient, 40)]
    public void PixelTolerance_DerivesFromStrictness(Strictness level, byte expected)
    {
        var options = new ComparisonOptions { Strictness = level };
        Assert.Equal(expected, options.EffectivePixelTolerance);
    }

    [Fact]
    public void ExplicitOverride_BeatsPreset()
    {
        var options = new ComparisonOptions { Strictness = Strictness.Lenient, PixelTolerance = 2 };
        Assert.Equal((byte)2, options.EffectivePixelTolerance);
    }

    [Fact]
    public void Lenient_IgnoresTinyTextChanges_ButExactDoesNot()
    {
        Assert.Equal(0.02, new ComparisonOptions { Strictness = Strictness.Lenient }.EffectiveTextDifferenceThreshold);
        Assert.Equal(0.0, new ComparisonOptions { Strictness = Strictness.Exact }.EffectiveTextDifferenceThreshold);
    }

    [Fact]
    public void Exact_HasZeroVisualThreshold()
    {
        Assert.Equal(0.0, new ComparisonOptions { Strictness = Strictness.Exact }.EffectiveVisualThreshold);
    }

    [Theory]
    [InlineData(Strictness.Exact, 0)]
    [InlineData(Strictness.Strict, 0)]
    [InlineData(Strictness.Balanced, 1)]
    [InlineData(Strictness.Lenient, 2)]
    public void ShiftTolerance_DerivesFromStrictness(Strictness level, int expected)
    {
        Assert.Equal(expected, new ComparisonOptions { Strictness = level }.EffectiveShiftTolerance);
    }
}
