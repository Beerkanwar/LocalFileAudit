using System.Media;
using System.Windows;
using LocalFileAudit.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.Services;

public sealed class WpfNotificationService : INotificationService
{
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<WpfNotificationService> _logger;

    public WpfNotificationService(
        IConfigurationService configurationService,
        ILogger<WpfNotificationService> logger)
    {
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void NotifyScanCompleted(string title, string message, bool isSuccess)
    {
        try
        {
            var config = _configurationService.Current.Scanning;
            if (!config.CompletionNotificationEnabled)
            {
                return;
            }

            if (config.CompletionSoundEnabled)
            {
                try
                {
                    if (isSuccess)
                    {
                        SystemSounds.Asterisk.Play();
                    }
                    else
                    {
                        SystemSounds.Exclamation.Play();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to play completion sound.");
                }
            }

            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                try
                {
                    var toast = new ToastNotificationWindow(title, message, isSuccess);
                    toast.Show();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to display toast notification window.");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in NotifyScanCompleted");
        }
    }
}
