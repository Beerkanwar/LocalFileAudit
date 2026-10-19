using LocalFileAudit.Core.Configuration;

namespace LocalFileAudit.Core.Interfaces;

/// <summary>
/// Service for loading, validating, and persisting application configuration.
/// </summary>
public interface IConfigurationService
{
    /// <summary>
    /// Current application configuration snapshot.
    /// </summary>
    AppConfiguration Current { get; }

    /// <summary>
    /// Loads configuration from disk, validating against safety rules.
    /// Falls back to safe defaults if missing or corrupted.
    /// </summary>
    Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically persists configuration to disk after validating safety rules.
    /// </summary>
    Task SaveAsync(AppConfiguration config, CancellationToken cancellationToken = default);
}
