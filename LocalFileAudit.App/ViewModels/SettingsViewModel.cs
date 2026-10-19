using CommunityToolkit.Mvvm.Input;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IConfigurationService _configurationService;
    private readonly IAppEnvironment _environment;
    private readonly ILogger<SettingsViewModel> _logger;

    private bool _includeFixedDrives;
    private bool _followReparsePoints;
    private bool _lowImpactMode;
    private int _maxWorkers;
    private int _batchSize;
    private bool _hashingEnabled;
    private int _largeFileThresholdMb;
    private int _oldFileAgeDays;
    private bool _aiEnabled;
    private string _aiEndpoint = string.Empty;
    private bool _requireLocalEndpoint;
    private bool _redactFilePaths;
    private int _logRetainDays;
    private string _statusMessage = string.Empty;
    private bool _isStatusError;

    public SettingsViewModel(
        IConfigurationService configurationService,
        IAppEnvironment environment,
        ILogger<SettingsViewModel> logger)
    {
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Title = "Settings";
        LoadSettings();
    }

    public string ConfigPath => _environment.ConfigFilePath;
    public string LogsPath => _environment.LogsDirectory;
    public string DatabasePath => _environment.DatabaseDirectory;

    public bool IncludeFixedDrives
    {
        get => _includeFixedDrives;
        set => SetProperty(ref _includeFixedDrives, value);
    }

    public bool FollowReparsePoints
    {
        get => _followReparsePoints;
        set => SetProperty(ref _followReparsePoints, value);
    }

    public bool LowImpactMode
    {
        get => _lowImpactMode;
        set => SetProperty(ref _lowImpactMode, value);
    }

    public int MaxWorkers
    {
        get => _maxWorkers;
        set => SetProperty(ref _maxWorkers, value);
    }

    public int BatchSize
    {
        get => _batchSize;
        set => SetProperty(ref _batchSize, value);
    }

    public bool HashingEnabled
    {
        get => _hashingEnabled;
        set => SetProperty(ref _hashingEnabled, value);
    }

    public int LargeFileThresholdMb
    {
        get => _largeFileThresholdMb;
        set => SetProperty(ref _largeFileThresholdMb, value);
    }

    public int OldFileAgeDays
    {
        get => _oldFileAgeDays;
        set => SetProperty(ref _oldFileAgeDays, value);
    }

    public bool AiEnabled
    {
        get => _aiEnabled;
        set => SetProperty(ref _aiEnabled, value);
    }

    public string AiEndpoint
    {
        get => _aiEndpoint;
        set => SetProperty(ref _aiEndpoint, value);
    }

    public bool RequireLocalEndpoint
    {
        get => _requireLocalEndpoint;
        set => SetProperty(ref _requireLocalEndpoint, value);
    }

    public bool RedactFilePaths
    {
        get => _redactFilePaths;
        set => SetProperty(ref _redactFilePaths, value);
    }

    public int LogRetainDays
    {
        get => _logRetainDays;
        set => SetProperty(ref _logRetainDays, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsStatusError
    {
        get => _isStatusError;
        set => SetProperty(ref _isStatusError, value);
    }

    // Safety policy: permanent read-only status in UI
    public string DeletionProtectionText => "Disabled (Permanently Enforced: Read-Only)";
    public string MoveProtectionText => "Disabled (Permanently Enforced: Read-Only)";
    public string CopyProtectionText => "Disabled (Permanently Enforced: Read-Only)";

    private void LoadSettings()
    {
        var cfg = _configurationService.Current;

        IncludeFixedDrives = cfg.Scanning.IncludeFixedDrivesByDefault;
        FollowReparsePoints = cfg.Scanning.FollowReparsePoints;
        LowImpactMode = cfg.Scanning.LowImpactMode;
        MaxWorkers = cfg.Scanning.MaxConcurrentDirectoryWorkers;
        BatchSize = cfg.Scanning.DatabaseBatchSize;
        HashingEnabled = cfg.Scanning.HashingEnabledByDefault;

        LargeFileThresholdMb = (int)(cfg.Analysis.LargeFileThresholdBytes / (1024 * 1024));
        OldFileAgeDays = cfg.Analysis.OldFileAgeDays;

        AiEnabled = cfg.AI.Enabled;
        AiEndpoint = cfg.AI.Endpoint;
        RequireLocalEndpoint = cfg.AI.RequireLocalEndpoint;

        RedactFilePaths = cfg.Logging.RedactFilePaths;
        LogRetainDays = cfg.Logging.RetainDays;
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        try
        {
            var updated = new AppConfiguration
            {
                Scanning = new ScanningConfiguration
                {
                    IncludeFixedDrivesByDefault = IncludeFixedDrives,
                    FollowReparsePoints = FollowReparsePoints,
                    LowImpactMode = LowImpactMode,
                    MaxConcurrentDirectoryWorkers = MaxWorkers,
                    DatabaseBatchSize = BatchSize,
                    HashingEnabledByDefault = HashingEnabled
                },
                Analysis = new AnalysisConfiguration
                {
                    LargeFileThresholdBytes = (long)LargeFileThresholdMb * 1024 * 1024,
                    OldFileAgeDays = OldFileAgeDays,
                    ArchiveRecommendationsRequireReview = true
                },
                AI = new AiConfiguration
                {
                    Enabled = AiEnabled,
                    Endpoint = AiEndpoint,
                    RequireLocalEndpoint = RequireLocalEndpoint,
                    SendDocumentContents = false,
                    TimeoutSeconds = 60
                },
                Safety = new SafetyConfiguration
                {
                    AutomaticDeletionEnabled = false,
                    AutomaticMoveEnabled = false,
                    AutomaticCopyEnabled = false
                },
                Logging = new LoggingConfiguration
                {
                    LogLevel = "Information",
                    RedactFilePaths = RedactFilePaths,
                    RetainDays = LogRetainDays
                }
            };

            await _configurationService.SaveAsync(updated);
            IsStatusError = false;
            StatusMessage = "Settings saved successfully to config.json.";
            _logger.LogInformation("Settings saved via SettingsViewModel.");
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = $"Failed to save settings: {ex.Message}";
            _logger.LogError(ex, "Failed to save settings from UI");
        }
    }

    [RelayCommand]
    public void ResetDefaults()
    {
        var defaults = new AppConfiguration();
        IncludeFixedDrives = defaults.Scanning.IncludeFixedDrivesByDefault;
        FollowReparsePoints = defaults.Scanning.FollowReparsePoints;
        LowImpactMode = defaults.Scanning.LowImpactMode;
        MaxWorkers = defaults.Scanning.MaxConcurrentDirectoryWorkers;
        BatchSize = defaults.Scanning.DatabaseBatchSize;
        HashingEnabled = defaults.Scanning.HashingEnabledByDefault;

        LargeFileThresholdMb = (int)(defaults.Analysis.LargeFileThresholdBytes / (1024 * 1024));
        OldFileAgeDays = defaults.Analysis.OldFileAgeDays;

        AiEnabled = defaults.AI.Enabled;
        AiEndpoint = defaults.AI.Endpoint;
        RequireLocalEndpoint = defaults.AI.RequireLocalEndpoint;

        RedactFilePaths = defaults.Logging.RedactFilePaths;
        LogRetainDays = defaults.Logging.RetainDays;

        IsStatusError = false;
        StatusMessage = "Defaults loaded. Click Save to persist.";
    }
}
