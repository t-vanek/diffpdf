using Avalonia.Logging;
using DiffPdf.DesktopUI.Infrastructure;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// The log-sink filter that silences the benign transient "Value is null" binding warnings
/// (DataGrid virtualization, unselected master-detail panels) while keeping real binding errors
/// (typo'd property → "Could not find a matching property accessor", failed conversions) visible.
/// </summary>
public class BindingNoiseLogSinkTests
{
    // The real Avalonia binding-error template; {Message} is the last value and carries the detail.
    private const string Template = "An error occurred binding {Property} to {Expression} at {ExpressionErrorPoint}: {Message}";

    private sealed class RecordingSink : ILogSink
    {
        public List<string> Forwarded { get; } = [];
        public bool Enabled { get; set; } = true;

        public bool IsEnabled(LogEventLevel level, string area) => Enabled;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Forwarded.Add(messageTemplate);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Forwarded.Add(messageTemplate);
    }

    private static (RecordingSink Inner, BindingNoiseLogSink Sink) New()
    {
        var inner = new RecordingSink();
        return (inner, new BindingNoiseLogSink(inner));
    }

    [Fact]
    public void Drops_binding_warning_whose_detail_is_value_is_null()
    {
        var (inner, sink) = New();
        sink.Log(LogEventLevel.Warning, LogArea.Binding, null, Template,
            "Key", "SelectedBranch.Key", "SelectedBranch", "Value is null.");
        Assert.Empty(inner.Forwarded);
    }

    [Fact]
    public void Passes_through_real_binding_error_missing_property()
    {
        var (inner, sink) = New();
        sink.Log(LogEventLevel.Warning, LogArea.Binding, null, Template,
            "Foo", "Foo", "Foo", "Could not find a matching property accessor for 'Foo' on 'Bar'.");
        Assert.Single(inner.Forwarded);
    }

    [Fact]
    public void Passes_through_real_binding_error_failed_conversion()
    {
        var (inner, sink) = New();
        sink.Log(LogEventLevel.Warning, LogArea.Binding, null, Template,
            "X", "X", "X", "Could not convert 'abc' to 'System.Int32'.");
        Assert.Single(inner.Forwarded);
    }

    [Fact]
    public void Passes_through_non_binding_warning_even_when_it_mentions_null()
    {
        var (inner, sink) = New();
        // e.g. "Cannot raise an event whose RoutedEvent is null." — different area, must survive.
        sink.Log(LogEventLevel.Warning, LogArea.Visual, null, "{Message}", "RoutedEvent is null.");
        Assert.Single(inner.Forwarded);
    }

    [Fact]
    public void Passes_through_binding_null_at_error_level()
    {
        var (inner, sink) = New();
        // Only Warnings are the benign transients; an Error must never be hidden.
        sink.Log(LogEventLevel.Error, LogArea.Binding, null, Template, "X", "X", "X", "Value is null.");
        Assert.Single(inner.Forwarded);
    }

    [Fact]
    public void Parameterless_overload_always_forwards()
    {
        var (inner, sink) = New();
        sink.Log(LogEventLevel.Warning, LogArea.Binding, null, "a templated message with no values");
        Assert.Single(inner.Forwarded);
    }

    [Fact]
    public void IsEnabled_delegates_to_inner()
    {
        var (inner, sink) = New();
        inner.Enabled = false;
        Assert.False(sink.IsEnabled(LogEventLevel.Warning, LogArea.Binding));
        inner.Enabled = true;
        Assert.True(sink.IsEnabled(LogEventLevel.Warning, LogArea.Binding));
    }

    [Fact]
    public void Null_inner_never_throws()
    {
        var sink = new BindingNoiseLogSink(null);
        sink.Log(LogEventLevel.Warning, LogArea.Binding, null, Template, "X", "X", "X", "Value is null.");
        sink.Log(LogEventLevel.Warning, LogArea.Layout, null, "passes");
        Assert.False(sink.IsEnabled(LogEventLevel.Warning, LogArea.Binding));
    }
}
