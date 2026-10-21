using System.Globalization;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.App.Wpf.Services;
using Serilog;

namespace PLCSIM.UnitTest.App.Wpf;

public partial class App : Application
{
    private ServiceProvider? serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        CultureInfo.DefaultThreadCurrentCulture = WpfApplicationDefaults.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = WpfApplicationDefaults.Culture;

        var settingsService = WpfApplicationSettingsService.CreateDefault();

        var services = new ServiceCollection();
        var logger = WpfLogging.CreateLogger(settingsService.Current);

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(logger, dispose: true);
        });

        services.AddWpfApplicationServices(settingsService);

        serviceProvider = services.BuildServiceProvider();
        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

        var appLogger = serviceProvider.GetRequiredService<ILogger<App>>();
        appLogger.LogInformation("--------------------------------------------------------------------------------");
        appLogger.LogInformation("PLCSIM UnitTest WPF session started. ProcessId={ProcessId}", Environment.ProcessId);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        serviceProvider
            ?.GetService<ILogger<App>>()
            ?.LogInformation("WPF application is shutting down.");

        var provider = serviceProvider;
        serviceProvider = null;
        if (provider is not null)
        {
            DisposeServiceProvider(provider);
        }

        Log.CloseAndFlush();

        base.OnExit(e);
    }

    internal static void DisposeServiceProvider(ServiceProvider provider)
    {
        var synchronizationContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
        }
    }
}
