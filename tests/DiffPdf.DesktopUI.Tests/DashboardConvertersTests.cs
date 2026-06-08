using System.Globalization;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>The dashboard's value converters: uptime humanisation + the health brush / label mappings.</summary>
public class DashboardConvertersTests
{
    private static object? Convert(Avalonia.Data.Converters.IValueConverter c, object? value, object? param = null) =>
        c.Convert(value, typeof(object), param, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(8.0, "8 s")]
    [InlineData(75.0, "1 min 15 s")]
    [InlineData(316.0, "5 min 16 s")]
    [InlineData(3661.0, "1 h 1 min")]
    [InlineData(90000.0, "1 d 1 h")]
    public void Uptime_humanises_seconds(double seconds, string expected) =>
        Assert.Equal(expected, Convert(UptimeConverter.Instance, seconds));

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Uptime_falls_back_for_invalid_input(double seconds) =>
        Assert.Equal("—", Convert(UptimeConverter.Instance, seconds));

    [Fact]
    public void OkBrush_maps_true_false_null()
    {
        Assert.Same(Palette.Good, Convert(OkBrushConverter.Instance, true));
        Assert.Same(Palette.Bad, Convert(OkBrushConverter.Instance, false));
        Assert.Same(Palette.Muted, Convert(OkBrushConverter.Instance, null));
    }

    [Fact]
    public void OkLabel_uses_defaults_then_parameter_overrides()
    {
        Assert.Equal("OK", Convert(OkLabelConverter.Instance, true));
        Assert.Equal("Chyba", Convert(OkLabelConverter.Instance, false));
        Assert.Equal("Vedoucí", Convert(OkLabelConverter.Instance, true, "Vedoucí|Následovník"));
        Assert.Equal("Následovník", Convert(OkLabelConverter.Instance, false, "Vedoucí|Následovník"));
    }

    [Fact]
    public void ErrorBrush_is_good_when_blank_and_bad_when_present()
    {
        Assert.Same(Palette.Good, Convert(ErrorBrushConverter.Instance, null));
        Assert.Same(Palette.Good, Convert(ErrorBrushConverter.Instance, "  "));
        Assert.Same(Palette.Bad, Convert(ErrorBrushConverter.Instance, "boom"));
    }
}
