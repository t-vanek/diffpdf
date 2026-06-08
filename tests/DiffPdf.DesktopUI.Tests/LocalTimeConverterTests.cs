using System.Globalization;
using Avalonia.Data;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>The UTC→local timestamp converter used across the views (server stores DateTimeOffset.UtcNow).</summary>
public class LocalTimeConverterTests
{
    private static string Run(object? value, string? param) =>
        (string)LocalTimeConverter.Instance.Convert(value, typeof(string), param, CultureInfo.InvariantCulture);

    [Fact]
    public void Converts_utc_to_local_wall_clock_with_format()
    {
        var utc = new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero);
        // The converter must render the LOCAL time (what ToLocalTime gives), not the raw UTC value.
        Assert.Equal(utc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture), Run(utc, "dd.MM HH:mm"));
    }

    [Fact]
    public void Prepends_prefix_before_the_local_time()
    {
        var utc = new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero);
        var expected = "Vytvořeno " + utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal(expected, Run(utc, "Vytvořeno|dd.MM.yyyy HH:mm"));
    }

    [Fact]
    public void Null_yields_empty_string()
    {
        Assert.Equal(string.Empty, Run(null, null));
    }

    [Fact]
    public void ConvertBack_returns_DoNothing_instead_of_throwing()
    {
        // The DataGrid can call ConvertBack on a read-only bound column; it must not throw (that crashed the grid).
        var result = LocalTimeConverter.Instance.ConvertBack("x", typeof(DateTimeOffset), null, CultureInfo.InvariantCulture);
        Assert.Same(BindingOperations.DoNothing, result);
    }
}
