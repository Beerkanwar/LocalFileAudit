using System.Windows;
using System.Windows.Threading;
using LocalFileAudit.App.ViewModels;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Infrastructure.DependencyInjection;
using LocalFileAudit.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App;

/// <summary>
/// Application startup and lifecycle coordinator.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SetupGlobalExceptionHandling();

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices((context, services) =>
            {
                // Infrastructure services (AppEnvironment, ConfigurationValidator, ConfigurationService)
                services.AddInfrastructureServices();

                // ViewModels
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<ScansViewModel>();
                services.AddSingleton<InventoryViewModel>();
                services.AddSingleton<StorageAnalysisViewModel>();
                services.AddSingleton<DuplicateAnalysisViewModel>();
                services.AddSingleton<AiReportsViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainViewModel>();

                // Views
                services.AddSingleton<MainWindow>();
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.AddLocalFileLogger();
            })
            .Build();

        await _host.StartAsync();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("LocalFileAudit application starting. Version 1.0.0 (Phase 1). Safe read-only mode active.");

        // Ensure application directories and configuration are initialized
        var configService = _host.Services.GetRequiredService<IConfigurationService>();
        await configService.LoadAsync();

        // Ensure SQLite database schema and WAL mode are initialized
        var dbInit = _host.Services.GetRequiredService<IDatabaseInitializer>();
        await dbInit.InitializeAsync();

        // Launch main window
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            var logger = _host.Services.GetService<ILogger<App>>();
            logger?.LogInformation("LocalFileAudit application stopping.");

            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private void SetupGlobalExceptionHandling()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            LogFatalException(e.Exception, "WPF Dispatcher Unhandled Exception");
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe application will continue running in safe mode. Details have been logged.",
                "LocalFileAudit Notice",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogFatalException(ex, "AppDomain Unhandled Exception");
            }
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogFatalException(e.Exception, "Unobserved Task Exception");
            e.SetObserved();
        };
    }

    private void LogFatalException(Exception ex, string context)
    {
        try
        {
            var logger = _host?.Services.GetService<ILogger<App>>();
            logger?.LogCritical(ex, "Critical unhandled exception occurred in {Context}", context);
        }
        catch
        {
            // Logging failure fallback should never mask or throw
        }
    }
}
