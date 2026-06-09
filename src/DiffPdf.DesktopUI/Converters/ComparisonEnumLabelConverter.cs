using System.Globalization;
using Avalonia.Data.Converters;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.Converters;

/// <summary>Maps the comparison-option enums (mode, strictness, highlight layout/style) to Czech labels
/// for the editor ComboBoxes. Renderer backends (Ghostscript/Pdfium) are product names and stay as-is.</summary>
public sealed class ComparisonEnumLabelConverter : IValueConverter
{
    public static readonly ComparisonEnumLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ComparisonMode.None => "Žádné",
        ComparisonMode.Text => "Textové",
        ComparisonMode.Visual => "Vizuální",
        ComparisonMode.Both => "Textové i vizuální",

        Strictness.Exact => "Přesná",
        Strictness.Strict => "Přísná",
        Strictness.Balanced => "Vyvážená",
        Strictness.Lenient => "Mírná",

        HighlightLayout.SideBySide => "Vedle sebe",
        HighlightLayout.Single => "Jen změněná strana",

        HighlightStyle.Raster => "Rastr (obrázek)",
        HighlightStyle.VectorOverlay => "Vektorová vrstva",

        _ => value?.ToString() ?? string.Empty,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
