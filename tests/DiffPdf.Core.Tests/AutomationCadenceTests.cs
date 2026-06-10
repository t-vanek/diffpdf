using DiffPdf.Application.Abstractions;

namespace DiffPdf.Core.Tests;

public class AutomationCadenceTests
{
    private static readonly DateTimeOffset From = new(2026, 6, 10, 12, 30, 15, TimeSpan.Zero);

    [Fact]
    public void Cron_ComputesNextOccurrenceInUtc()
    {
        Assert.True(AutomationCadence.TryComputeNext("0 3 * * *", null, From, out var next, out var error));
        Assert.Null(error);
        Assert.Equal(new DateTimeOffset(2026, 6, 11, 3, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Cron_WinsOverInterval()
    {
        Assert.True(AutomationCadence.TryComputeNext("0 3 * * *", 60, From, out var next, out _));
        Assert.Equal(new DateTimeOffset(2026, 6, 11, 3, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Cron_Invalid_ReturnsFalseWithError()
    {
        Assert.False(AutomationCadence.TryComputeNext("not a cron", null, From, out var next, out var error));
        Assert.Null(next);
        Assert.Contains("not a cron", error);
    }

    [Fact]
    public void Interval_AddsSeconds()
    {
        Assert.True(AutomationCadence.TryComputeNext(null, 300, From, out var next, out _));
        Assert.Equal(From.AddSeconds(300), next);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Interval_NonPositive_ReturnsFalseWithError(int interval)
    {
        Assert.False(AutomationCadence.TryComputeNext(null, interval, From, out var next, out var error));
        Assert.Null(next);
        Assert.NotNull(error);
    }

    [Fact]
    public void NoSchedule_IsValidWithNullNext()
    {
        Assert.True(AutomationCadence.TryComputeNext(null, null, From, out var next, out var error));
        Assert.Null(next);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("0 3 * * *", null, true)]
    [InlineData(null, 60, true)]
    [InlineData(null, 0, false)]
    [InlineData("  ", null, false)]
    public void HasSchedule_DetectsAnyCadence(string? cron, int? interval, bool expected)
    {
        Assert.Equal(expected, AutomationCadence.HasSchedule(cron, interval));
    }
}
