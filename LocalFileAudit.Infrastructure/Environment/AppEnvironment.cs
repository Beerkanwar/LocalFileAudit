using LocalFileAudit.Core.Interfaces;

namespace LocalFileAudit.Infrastructure.Environment;

/// <summary>
/// Resolves application folders inside the designated local application data directory (%LocalAppData%\LocalFileAudit).
/// Supports an optional root path override for unit tests.
/// </summary>
public sealed class AppEnvironment : IAppEnvironment
{
    public AppEnvironment(string? rootPathOverride = null)
    {
        ApplicationDataDirectory = !string.IsNullOrWhiteSpace(rootPathOverride)
            ? rootPathOverride
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "LocalFileAudit");

        ConfigFilePath = Path.Combine(ApplicationDataDirectory, "config.json");
        LogsDirectory = Path.Combine(ApplicationDataDirectory, "logs");
        DatabaseDirectory = Path.Combine(ApplicationDataDirectory, "data");
        ReportsDirectory = Path.Combine(ApplicationDataDirectory, "reports");
        BackupsDirectory = Path.Combine(ApplicationDataDirectory, "backups");
    }

    public string ApplicationDataDirectory { get; }
    public string ConfigFilePath { get; }
    public string LogsDirectory { get; }
    public string DatabaseDirectory { get; }
    public string ReportsDirectory { get; }
    public string BackupsDirectory { get; }

    public void EnsureDirectoriesCreated()
    {
        Directory.CreateDirectory(ApplicationDataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(ReportsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}
