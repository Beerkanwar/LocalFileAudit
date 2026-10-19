namespace LocalFileAudit.Core.Interfaces;

/// <summary>
/// Provides application data directories and path resolution.
/// Ensures all application files are placed inside designated application data storage.
/// </summary>
public interface IAppEnvironment
{
    /// <summary>
    /// Base application directory (e.g., %LocalAppData%\LocalFileAudit).
    /// </summary>
    string ApplicationDataDirectory { get; }

    /// <summary>
    /// Path to config.json.
    /// </summary>
    string ConfigFilePath { get; }

    /// <summary>
    /// Path to logs directory.
    /// </summary>
    string LogsDirectory { get; }

    /// <summary>
    /// Path to database storage directory.
    /// </summary>
    string DatabaseDirectory { get; }

    /// <summary>
    /// Path to exported reports directory.
    /// </summary>
    string ReportsDirectory { get; }

    /// <summary>
    /// Path to backups directory.
    /// </summary>
    string BackupsDirectory { get; }

    /// <summary>
    /// Ensures all required application folders exist.
    /// </summary>
    void EnsureDirectoriesCreated();
}
