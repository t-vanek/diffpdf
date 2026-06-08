using Avalonia;
using Avalonia.Logging;
using DiffPdf.DesktopUI.Infrastructure;

namespace DiffPdf.DesktopUI;

internal static class Program
{
    // Avalonia configuration; don't use any Avalonia, third-party APIs or any SynchronizationContext
    // before AppMain is called: things aren't initialized yet and stuff will break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        // LogToTrace eagerly installs Logger.Sink; wrap it to silence the benign transient
        // "Value is null" binding warnings (DataGrid virtualization, unselected detail panels)
        // while keeping real binding errors visible. See BindingNoiseLogSink.
        Logger.Sink = new BindingNoiseLogSink(Logger.Sink);
        return builder;
    }
}
