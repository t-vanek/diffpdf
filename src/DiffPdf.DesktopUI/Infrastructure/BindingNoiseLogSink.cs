using System;
using System.Linq;
using Avalonia.Logging;

namespace DiffPdf.DesktopUI.Infrastructure;

/// <summary>
/// Wraps Avalonia's default log sink and drops the benign, transient binding warnings that fire
/// when an intermediate link in a binding chain is momentarily <c>null</c>. These read
/// "… : Value is null" and recover on the next layout pass, so they are pure console noise:
/// <list type="bullet">
///   <item>DataGrid cell/header templates evaluating <c>#Root.DataContext.…</c> while rows are
///         recycled during virtualization (e.g. the per-row transport commands, the
///         "select all" header checkbox).</item>
///   <item>Master-detail panels binding to <c>SelectedItem.…</c> while nothing is selected.</item>
/// </list>
/// Genuine binding bugs are passed through untouched, because their detail message reads
/// "Could not find a matching property accessor …" (typo'd property) or "Could not convert …"
/// (type mismatch) — never "… is null". We discriminate on exactly that phrase, so a real
/// mistake still surfaces in the debug output.
/// </summary>
internal sealed class BindingNoiseLogSink(ILogSink? inner) : ILogSink
{
    public bool IsEnabled(LogEventLevel level, string area) => inner?.IsEnabled(level, area) ?? false;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (IsBenignBindingNull(level, area, null)) return;
        inner?.Log(level, area, source, messageTemplate);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (IsBenignBindingNull(level, area, propertyValues)) return;
        inner?.Log(level, area, source, messageTemplate, propertyValues);
    }

    // The binding-error template is "An error occurred binding {Property} to {Expression} at
    // {ExpressionErrorPoint}: {Message}". For a null link the {Message} value is "Value is null";
    // we match the " is null" phrase (with the space, so property names like "IsNullable" are
    // never caught) only on Binding-area Warnings.
    private static bool IsBenignBindingNull(LogEventLevel level, string area, object?[]? propertyValues)
    {
        if (level != LogEventLevel.Warning || area != LogArea.Binding || propertyValues is null)
            return false;

        return propertyValues.Any(v => v is string s && s.Contains("is null", StringComparison.OrdinalIgnoreCase));
    }
}
