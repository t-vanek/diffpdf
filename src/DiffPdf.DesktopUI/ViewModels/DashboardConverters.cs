using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Humanises a server uptime in seconds into "3 d 4 h" / "5 h 16 min" / "12 min 3 s" / "8 s".</summary>
public sealed class UptimeConverter : IValueConverter
{
    public static readonly UptimeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double seconds = value switch { double d => d, int i => i, long l => l, _ => double.NaN };
        if (double.IsNaN(seconds) || seconds < 0) return "—";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d {t.Hours} h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} min {t.Seconds} s";
        return $"{t.Seconds} s";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>Maps a health boolean to a semantic <see cref="Palette"/> brush: true→Good, false→Bad, null→Muted.</summary>
public sealed class OkBrushConverter : IValueConverter
{
    public static readonly OkBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch { true => Palette.Good, false => Palette.Bad, _ => Palette.Muted };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>Maps a boolean to one of two labels. <c>ConverterParameter</c> <c>"trueLabel|falseLabel"</c> overrides
/// the default <c>"OK|Chyba"</c> (e.g. <c>"Vedoucí (leader)|Následovník"</c>).</summary>
public sealed class OkLabelConverter : IValueConverter
{
    public static readonly OkLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string trueLabel = "OK", falseLabel = "Chyba";
        if (parameter is string p)
        {
            int bar = p.IndexOf('|');
            if (bar >= 0) { trueLabel = p[..bar]; falseLabel = p[(bar + 1)..]; }
        }
        return value is true ? trueLabel : falseLabel;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>Maps an error string to a status brush: present (non-empty)→Bad, absent→Good — for per-service health dots.</summary>
public sealed class ErrorBrushConverter : IValueConverter
{
    public static readonly ErrorBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Palette.Good : Palette.Bad;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>Maps an automation run outcome (<see cref="AutomationRunOutcome"/>?) to a status brush; not-yet-run (null) → Muted.</summary>
public sealed class AutomationOutcomeBrushConverter : IValueConverter
{
    public static readonly AutomationOutcomeBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AutomationRunOutcome.Ok => Palette.Good,
        AutomationRunOutcome.Warning => Palette.Warning,
        AutomationRunOutcome.Failed => Palette.Bad,
        _ => Palette.Muted,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>Maps an automation run outcome to a Czech label; not-yet-run (null) → "—".</summary>
public sealed class AutomationOutcomeLabelConverter : IValueConverter
{
    public static readonly AutomationOutcomeLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AutomationRunOutcome.Ok => "OK",
        AutomationRunOutcome.Warning => "Varování",
        AutomationRunOutcome.Failed => "Selhalo",
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>Maps an <see cref="AutomationResponse"/> row to its schedule label: cron, interval, or manual/event-only.</summary>
public sealed class AutomationCadenceLabelConverter : IValueConverter
{
    public static readonly AutomationCadenceLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AutomationResponse { Cron: { } cron } when !string.IsNullOrWhiteSpace(cron) => cron,
        AutomationResponse { IntervalSeconds: { } interval and > 0 } => $"{interval} s",
        AutomationResponse { EventTriggers.Count: > 0 } => "událost",
        AutomationResponse => "ručně",
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>Maps an <see cref="AutomationTriggerKind"/> to a Czech label.</summary>
public sealed class AutomationTriggerLabelConverter : IValueConverter
{
    public static readonly AutomationTriggerLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AutomationTriggerKind.Schedule => "Plán",
        AutomationTriggerKind.Event => "Událost",
        AutomationTriggerKind.Manual => "Ručně",
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
