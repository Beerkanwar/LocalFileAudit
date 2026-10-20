namespace LocalFileAudit.Core.Interfaces;

/// <summary>
/// Service providing desktop and audio notifications for background task completions.
/// </summary>
public interface INotificationService
{
    void NotifyScanCompleted(string title, string message, bool isSuccess);
}
