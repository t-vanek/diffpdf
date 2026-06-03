using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;
using DiffPdf.DesktopUI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.DesktopUI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = default!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services = ConfigureServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = Services.GetRequiredService<MainViewModel>() };
            Services.GetRequiredService<DialogService>().Owner = window;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core services (singletons shared across the app).
        services.AddSingleton<ServerSession>();
        services.AddSingleton<TokenSource>();
        services.AddSingleton<JobProgressHubClient>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<NavigationService>();

        // Shell.
        services.AddSingleton<MainViewModel>();

        // Section pages are registered below as they are implemented (each as PageViewModel).
        DiffPdf.DesktopUI.ViewModels.PageRegistration.Register(services);

        return services.BuildServiceProvider();
    }
}
