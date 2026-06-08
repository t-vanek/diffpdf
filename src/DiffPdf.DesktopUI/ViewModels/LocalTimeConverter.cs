using System.Globalization;
using Avalonia.Data.Converters;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Formats a server UTC timestamp (<see cref="DateTimeOffset"/> / <see cref="DateTime"/>) in the machine's
/// LOCAL time zone, so a user in e.g. Czech time (CET/CEST) sees local wall-clock rather than UTC. Server
/// timestamps are stored as <c>DateTimeOffset.UtcNow</c>; binding one straight to a date format would render
/// UTC.
///
/// <para><c>ConverterParameter</c> is <c>"[prefix|]format"</c> — the format defaults to
/// <c>"dd.MM.yyyy HH:mm"</c>, and an optional prefix before the <c>'|'</c> is prepended as plain text (e.g.
/// <c>"Vytvořeno|dd.MM HH:mm"</c>, which avoids quoting letters that are date-format specifiers). Null or an
/// unsupported value yields "". Durations are timezone-independent and need no conversion.</para>
/// </summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public static readonly LocalTimeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string prefix = "", format = "dd.MM.yyyy HH:mm";
        if (parameter is string p)
        {
            int bar = p.IndexOf('|');
            if (bar >= 0) { prefix = p[..bar]; format = p[(bar + 1)..]; }
            else format = p;
        }

        DateTime? local = value switch
        {
            DateTimeOffset dto => dto.ToLocalTime().DateTime,
            DateTime dt => dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt,
            _ => null,
        };
        if (local is not { } l) return string.Empty;
        string text = l.ToString(format, culture);
        return prefix.Length > 0 ? $"{prefix} {text}" : text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
