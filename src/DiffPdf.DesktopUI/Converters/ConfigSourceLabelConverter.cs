using System.Globalization;
using Avalonia.Data.Converters;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.Converters;

/// <summary>Maps a <see cref="ConfigSource"/> to a Czech label for the source ComboBoxes.</summary>
public sealed class ConfigSourceLabelConverter : IValueConverter
{
    public static readonly ConfigSourceLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConfigSource.Global => "Globální konfigurace",
        ConfigSource.Branch => "Konfigurace větve",
        ConfigSource.Custom => "Vlastní konfigurace",
        _ => value?.ToString() ?? string.Empty,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
