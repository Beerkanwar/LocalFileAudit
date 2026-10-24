using System.Text.Json;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Validation;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Configuration;

/// <summary>
/// Persists and loads typed application configuration via System.Text.Json.
/// Uses atomic write operations to prevent corruption upon abrupt termination.
/// </summary>
public sealed class JsonConfigurationService : IConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly IAppEnvironment _environment;
    private readonly IConfigurationValidator _validator;
    private readonly ILogger<JsonConfigurationService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private volatile AppConfiguration _current;

    public AppConfiguration Current => _current;

    public JsonConfigurationService(
        IAppEnvironment environment,
        IConfigurationValidator validator,
        ILogger<JsonConfigurationService> logger)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _current = new AppConfiguration();
    }

    public async Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _environment.EnsureDirectoriesCreated();

            if (!File.Exists(_environment.ConfigFilePath))
            {
                _logger.LogInformation("Configuration file not found. Creating default configuration at {ConfigPath}", _environment.ConfigFilePath);
                var defaultConfig = new AppConfiguration();
                await SaveInternalAsync(defaultConfig, cancellationToken).ConfigureAwait(false);
                _current = defaultConfig;
                return _current;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_environment.ConfigFilePath, cancellationToken).ConfigureAwait(false);
                var loaded = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions);

                if (loaded == null)
                {
                    _logger.LogWarning("Deserialized configuration was null. Backing up and falling back to default configuration.");
                    BackupCorruptedConfig();
                    _current = new AppConfiguration();
                    return _current;
                }

                var validation = _validator.Validate(loaded);
                if (!validation.IsValid)
                {
                    _logger.LogWarning("Loaded configuration had invalid fields: {Errors}. Backing up corrupted file and repairing per-field with safe defaults.", string.Join("; ", validation.Errors));
                    BackupCorruptedConfig();
                    RepairInvalidFields(loaded);

                    // Re-validate repaired config
                    var revalidation = _validator.Validate(loaded);
                    if (!revalidation.IsValid)
                    {
                        _logger.LogError("Repaired configuration still failed validation: {Errors}. Reverting to fresh defaults.", string.Join("; ", revalidation.Errors));
                        _current = new AppConfiguration();
                    }
                    else
                    {
                        _current = loaded;
                        try
                        {
                            await SaveInternalAsync(_current, cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation("Successfully saved repaired configuration to {ConfigPath}", _environment.ConfigFilePath);
                        }
                        catch (Exception saveEx)
                        {
                            _logger.LogWarning(saveEx, "Failed to persist repaired configuration.");
                        }
                    }

                    return _current;
                }

                _current = loaded;
                _logger.LogInformation("Configuration loaded successfully from {ConfigPath}", _environment.ConfigFilePath);
                return _current;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read configuration from {ConfigPath}. Backing up and falling back to safe defaults.", _environment.ConfigFilePath);
                BackupCorruptedConfig();
                _current = new AppConfiguration();
                return _current;
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static void RepairInvalidFields(AppConfiguration config)
    {
        var defaults = new AppConfiguration();

        // Safety flags are immutable invariants
        config.Safety.AutomaticDeletionEnabled = false;
        config.Safety.AutomaticMoveEnabled = false;
        config.Safety.AutomaticCopyEnabled = false;

        // Scanning bounds
        if (config.Scanning.MaxConcurrentDirectoryWorkers < 1 || config.Scanning.MaxConcurrentDirectoryWorkers > 16)
        {
            config.Scanning.MaxConcurrentDirectoryWorkers = defaults.Scanning.MaxConcurrentDirectoryWorkers;
        }

        if (config.Scanning.DatabaseBatchSize < 10 || config.Scanning.DatabaseBatchSize > 50000)
        {
            config.Scanning.DatabaseBatchSize = defaults.Scanning.DatabaseBatchSize;
        }

        // Analysis bounds
        if (config.Analysis.LargeFileThresholdBytes <= 0)
        {
            config.Analysis.LargeFileThresholdBytes = defaults.Analysis.LargeFileThresholdBytes;
        }

        if (config.Analysis.OldFileAgeDays < 0)
        {
            config.Analysis.OldFileAgeDays = defaults.Analysis.OldFileAgeDays;
        }

        // AI bounds & endpoint
        if (string.IsNullOrWhiteSpace(config.AI.Endpoint) || !Uri.TryCreate(config.AI.Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            config.AI.Endpoint = defaults.AI.Endpoint;
        }
        else if (config.AI.RequireLocalEndpoint)
        {
            var isLoopback = uri.IsLoopback ||
                             string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
            if (!isLoopback)
            {
                config.AI.Endpoint = defaults.AI.Endpoint;
            }
        }

        if (config.AI.TimeoutSeconds < 1 || config.AI.TimeoutSeconds > 600)
        {
            config.AI.TimeoutSeconds = defaults.AI.TimeoutSeconds;
        }

        if (config.AI.MaxPayloadCandidates < 1 || config.AI.MaxPayloadCandidates > 1000)
        {
            config.AI.MaxPayloadCandidates = defaults.AI.MaxPayloadCandidates;
        }

        // Logging & exclusions
        if (config.Logging.RetainDays <= 0)
        {
            config.Logging.RetainDays = defaults.Logging.RetainDays;
        }

        var validLevels = new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" };
        if (string.IsNullOrWhiteSpace(config.Logging.LogLevel) || !validLevels.Contains(config.Logging.LogLevel, StringComparer.OrdinalIgnoreCase))
        {
            config.Logging.LogLevel = defaults.Logging.LogLevel;
        }

        if (config.Scanning.ExcludedPaths != null)
        {
            var invalidChars = Path.GetInvalidPathChars();
            config.Scanning.ExcludedPaths = config.Scanning.ExcludedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p) && p.IndexOfAny(invalidChars) < 0)
                .ToList();
        }
    }

    private void BackupCorruptedConfig()
    {
        try
        {
            if (File.Exists(_environment.ConfigFilePath))
            {
                var backupPath = _environment.ConfigFilePath + $".corrupted.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                File.Copy(_environment.ConfigFilePath, backupPath, overwrite: true);
                _logger.LogWarning("Backed up corrupted or invalid configuration file to {BackupPath}", backupPath);

                // Prune old backups, keeping only 5 recent
                var dir = Path.GetDirectoryName(_environment.ConfigFilePath);
                var baseName = Path.GetFileName(_environment.ConfigFilePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    var oldBackups = Directory.GetFiles(dir, $"{baseName}.corrupted.*.bak")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.CreationTimeUtc)
                        .Skip(5);
                    foreach (var old in oldBackups)
                    {
                        try { old.Delete(); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create backup of invalid configuration file.");
        }
    }

    public async Task SaveAsync(AppConfiguration config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var validation = _validator.Validate(config);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Cannot save invalid configuration: {string.Join("; ", validation.Errors)}");
        }

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveInternalAsync(config, cancellationToken).ConfigureAwait(false);
            _current = config;
            _logger.LogInformation("Configuration saved successfully.");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task SaveInternalAsync(AppConfiguration config, CancellationToken cancellationToken)
    {
        _environment.EnsureDirectoriesCreated();

        var tempFilePath = _environment.ConfigFilePath + ".tmp";
        var json = JsonSerializer.Serialize(config, JsonOptions);

        await using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        await using (var sw = new StreamWriter(fs))
        {
            await sw.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await sw.FlushAsync(cancellationToken).ConfigureAwait(false);
            await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Atomic file replacement
        File.Move(tempFilePath, _environment.ConfigFilePath, overwrite: true);
    }
}
