using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DiffPdf.DesktopUI.Configuration;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;
using DiffPdf.DesktopUI.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.DesktopUI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = default!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services = ConfigureServices();
        ViewModelBase.ToastSink = Services.GetRequiredService<ToastService>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = Services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = mainViewModel };
            Services.GetRequiredService<DialogService>().Owner = window;
            // Auto-connect once the window is shown (config URL or LAN discovery); errors surface in the bar.
            window.Opened += async (_, _) => await mainViewModel.AutoConnectAsync();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Client configuration: appsettings.json next to the exe, overridable via DIFFPDF_ env vars.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            // User-saved connection settings (written by the connection gear) override appsettings.json.
            .AddJsonFile(ClientSettingsStore.DefaultFilePath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("DIFFPDF_")
            .Build();
        services.AddSingleton(configuration.Get<ClientConfig>() ?? new ClientConfig());

        // Core services (singletons shared across the app).
        services.AddSingleton<ServerSession>();
        services.AddSingleton<ClientSettingsStore>();
        services.AddSingleton<TokenSource>();
        services.AddSingleton<JobProgressHubClient>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<ServerDiscoveryClient>();

        // Shell.
        services.AddSingleton<MainViewModel>();

        // Section pages are registered below as they are implemented (each as PageViewModel).
        DiffPdf.DesktopUI.ViewModels.PageRegistration.Register(services);

        return services.BuildServiceProvider();
    }
}
