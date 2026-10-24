using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
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
    private static Mutex? _singleInstanceMutex;
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        const string mutexName = @"Local\LocalFileAudit_SingleInstance_Mutex";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool isNewInstance);

        if (!isNewInstance)
        {
            // Another instance is already running or starting up!
            BringExistingWindowToFront();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        SetupGlobalExceptionHandling();

        try
        {
            _host = Host.CreateDefaultBuilder(e.Args)
                .ConfigureServices((context, services) =>
                {
                    // Infrastructure services (AppEnvironment, ConfigurationValidator, ConfigurationService)
                    services.AddInfrastructureServices();

                    // Notification service
                    services.AddSingleton<INotificationService, LocalFileAudit.App.Services.WpfNotificationService>();

                    // ViewModels
                    services.AddSingleton<DashboardViewModel>();
                    services.AddSingleton<ScansViewModel>();
                    services.AddSingleton<InventoryViewModel>();
                    services.AddSingleton<StorageAnalysisViewModel>();
                    services.AddSingleton<DuplicateAnalysisViewModel>();
                    services.AddSingleton<AiReportsViewModel>();
                    services.AddSingleton<CompareRunsViewModel>();
                    services.AddSingleton<ArchivePlanViewModel>();
                    services.AddSingleton<ArchiveVerificationViewModel>();
                    services.AddSingleton<SettingsViewModel>();
                    services.AddSingleton<MainViewModel>();

                    // Views
                    services.AddSingleton<MainWindow>();
                })
                .ConfigureLogging((context, logging) =>
                {
                    logging.ClearProviders();
                    logging.AddLocalFileLogger();
                })
                .Build();

            await _host.StartAsync();

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("LocalFileAudit application starting. Version {Version}. Safe read-only mode active.", LocalFileAudit.Core.Common.AppVersion.Current);

            // Ensure application directories and configuration are initialized
            var configService = _host.Services.GetRequiredService<IConfigurationService>();
            await configService.LoadAsync();

            var loggerProvider = _host.Services.GetService<FileLoggerProvider>();
            loggerProvider?.ApplyConfiguration(configService.Current.Logging);

            // Ensure SQLite database schema and WAL mode are initialized
            var dbInit = _host.Services.GetRequiredService<IDatabaseInitializer>();
            await dbInit.InitializeAsync();

            // Launch main window
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"A critical error occurred while initializing LocalFileAudit:\n\n{ex.Message}\n\nThe application will now close. Please inspect diagnostic logs for more details.",
                "LocalFileAudit — Fatal Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            var logger = _host.Services.GetService<ILogger<App>>();
            logger?.LogInformation("LocalFileAudit application stopping.");

            try
            {
                _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }
            catch { }
            _host.Dispose();
        }

        if (_singleInstanceMutex != null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    private static void BringExistingWindowToFront()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            var other = Process.GetProcessesByName(current.ProcessName)
                .FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);

            if (other != null)
            {
                ShowWindow(other.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(other.MainWindowHandle);
            }
        }
        catch
        {
            // Best effort activation
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    private void SetupGlobalExceptionHandling()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            LogFatalException(e.Exception, "WPF Dispatcher Unhandled Exception");

            if (e.Exception is OutOfMemoryException or StackOverflowException or AccessViolationException)
            {
                e.Handled = false;
                return;
            }

            var safeMessage = PathRedactor.Redact(e.Exception.Message);
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{safeMessage}\n\nThe application will continue running in safe mode. Details have been logged.",
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
