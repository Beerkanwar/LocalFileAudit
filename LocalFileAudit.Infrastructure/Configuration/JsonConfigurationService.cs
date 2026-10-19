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

    private AppConfiguration _current;

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
                    _logger.LogWarning("Deserialized configuration was null. Falling back to default configuration.");
                    _current = new AppConfiguration();
                    return _current;
                }

                var validation = _validator.Validate(loaded);
                if (!validation.IsValid)
                {
                    _logger.LogError("Loaded configuration failed validation: {Errors}. Reverting to safe defaults.", string.Join("; ", validation.Errors));
                    _current = new AppConfiguration();
                    return _current;
                }

                _current = loaded;
                _logger.LogInformation("Configuration loaded successfully from {ConfigPath}", _environment.ConfigFilePath);
                return _current;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read configuration from {ConfigPath}. Falling back to safe defaults.", _environment.ConfigFilePath);
                _current = new AppConfiguration();
                return _current;
            }
        }
        finally
        {
            _fileLock.Release();
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

        await File.WriteAllTextAsync(tempFilePath, json, cancellationToken).ConfigureAwait(false);

        // Atomic file replacement
        File.Move(tempFilePath, _environment.ConfigFilePath, overwrite: true);
    }
}
