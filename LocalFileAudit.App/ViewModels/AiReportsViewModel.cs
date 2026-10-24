using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.ViewModels;

public sealed partial class AiReportsViewModel : ViewModelBase
{
    private readonly IOllamaClient _ollamaClient;
    private readonly IAiAnalysisService _aiService;
    private readonly IStorageAnalysisEngine _analysisEngine;
    private readonly IScanRepository _repository;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<AiReportsViewModel> _logger;

    private CancellationTokenSource? _aiCts;
    private AiAnalysisResponse? _lastResponse;

    public Func<long, Guid?, Task>? OnNavigateToInventoryWithCandidate { get; set; }

    [ObservableProperty]
    private bool _isAiEnabled;

    [ObservableProperty]
    private string _endpoint = "http://127.0.0.1:11434";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionStatusMessage = "Checking local Ollama status...";

    [ObservableProperty]
    private string? _selectedModel;

    [ObservableProperty]
    private ScanRun? _selectedScan;

    [ObservableProperty]
    private bool _redactPersonalPaths = true;

    [ObservableProperty]
    private string _promptPreviewText = "Select a scan and click 'Preview Minimised Input' to inspect the exact data sent to Ollama.";

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private string _statusMessage = "Ready. Deterministic storage analysis works 100% offline without AI.";

    [ObservableProperty]
    private string _executiveSummary = string.Empty;

    [ObservableProperty]
    private string _storageHotspots = string.Empty;

    [ObservableProperty]
    private bool _hasAiResults;

    [ObservableProperty]
    private string _lastGeneratedReportPath = string.Empty;

    [ObservableProperty]
    private string _chatInputText = string.Empty;

    [ObservableProperty]
    private bool _isChatSending;

    [ObservableProperty]
    private string _chatStatusMessage = "Ask a question about this scan...";

    public ObservableCollection<string> AvailableModels { get; } = new();
    public ObservableCollection<ScanRun> AvailableScans { get; } = new();
    public ObservableCollection<AiRecommendation> Recommendations { get; } = new();
    public ObservableCollection<string> ReviewPlan { get; } = new();
    public ObservableCollection<string> SafetyWarnings { get; } = new();
    public ObservableCollection<AiReportRecord> AiReportHistory { get; } = new();
    public ObservableCollection<ChatMessageRecord> ChatMessages { get; } = new();

    public AiReportsViewModel(
        IOllamaClient ollamaClient,
        IAiAnalysisService aiService,
        IStorageAnalysisEngine analysisEngine,
        IScanRepository repository,
        IConfigurationService configurationService,
        ILogger<AiReportsViewModel> logger)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
        _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
        _analysisEngine = analysisEngine ?? throw new ArgumentNullException(nameof(analysisEngine));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Title = "AI Reports";

        LoadConfiguration();
    }

    private bool _isInitialized;

    public async Task EnsureInitializedAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        await InitializeAsync();
    }

    private void LoadConfiguration()
    {
        var config = _configurationService.Current;
        IsAiEnabled = config.AI.Enabled;
        Endpoint = config.AI.Endpoint;
        RedactPersonalPaths = config.Logging.RedactFilePaths;
    }

    public async Task InitializeAsync()
    {
        await LoadScansAsync();
        await TestConnectionAsync();
        await RefreshHistoryAsync();
    }

    public async Task LoadScansAsync()
    {
        try
        {
            var scans = await _repository.GetRecentScanRunsAsync(20);
            AvailableScans.Clear();
            foreach (var s in scans)
            {
                AvailableScans.Add(s);
            }
            SelectedScan = AvailableScans.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load recent scans for AI report view.");
        }
    }

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        ConnectionStatusMessage = "Connecting to local Ollama on loopback...";
        IsConnected = false;

        try
        {
            var status = await _ollamaClient.CheckConnectionAsync(Endpoint);
            IsConnected = status.IsAvailable;

            if (status.IsAvailable)
            {
                AvailableModels.Clear();
                foreach (var m in status.AvailableModels)
                {
                    AvailableModels.Add(m);
                }

                var configuredModel = _configurationService.Current.AI.ModelName;
                if (!string.IsNullOrWhiteSpace(configuredModel) && AvailableModels.Contains(configuredModel))
                {
                    SelectedModel = configuredModel;
                }
                else
                {
                    SelectedModel = AvailableModels.FirstOrDefault() ?? "llama3.2:latest";
                }
                ConnectionStatusMessage = $"✓ Connected to Ollama ({status.AvailableModels.Count} local models available, {status.ResponseTimeMs}ms).";
            }
            else
            {
                ConnectionStatusMessage = $"⚠ {status.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionStatusMessage = $"Connection failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task GeneratePromptPreviewAsync()
    {
        if (SelectedScan == null)
        {
            StatusMessage = "Please select a scan run first.";
            return;
        }

        try
        {
            StatusMessage = "Generating minimized inference payload preview...";
            var metrics = await _analysisEngine.AnalyzeScanAsync(SelectedScan.Id);
            PromptPreviewText = _aiService.BuildPromptPreview(metrics, RedactPersonalPaths);
            StatusMessage = "Payload preview ready. Review data minimization before running analysis.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to preview prompt: {ex.Message}";
            _logger.LogError(ex, "Error building prompt preview");
        }
    }

    [RelayCommand]
    public async Task RunAiAnalysisAsync()
    {
        if (!IsAiEnabled || !_configurationService.Current.AI.Enabled)
        {
            StatusMessage = "AI analysis is disabled in Settings. Enable it to run local inference.";
            return;
        }

        if (SelectedScan == null)
        {
            StatusMessage = "Please select a scan to analyze.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedModel))
        {
            StatusMessage = "Please select an Ollama model from the dropdown.";
            return;
        }

        _aiCts = new CancellationTokenSource();
        IsGenerating = true;
        StatusMessage = $"Running local inference with model '{SelectedModel}'... (Bounded timeout active)";

        try
        {
            var response = await _aiService.RunAnalysisAsync(
                SelectedScan.Id,
                SelectedModel,
                RedactPersonalPaths,
                _aiCts.Token);

            ExecutiveSummary = response.ExecutiveSummary;
            StorageHotspots = response.StorageHotspotsAssessment;

            Recommendations.Clear();
            foreach (var r in response.Recommendations)
            {
                Recommendations.Add(r);
            }

            ReviewPlan.Clear();
            foreach (var p in response.PrioritizedReviewPlan)
            {
                ReviewPlan.Add(p);
            }

            SafetyWarnings.Clear();
            foreach (var w in response.SafetyWarnings)
            {
                SafetyWarnings.Add(w);
            }

            _lastResponse = response;
            HasAiResults = true;
            StatusMessage = $"AI decision support complete. {Recommendations.Count} recommendations generated and saved to AI-Review.md and AI-Review-Table.csv/.md.";
        }
        catch (OperationCanceledException) when (_aiCts?.IsCancellationRequested == true)
        {
            StatusMessage = "AI inference cancelled by user. Deterministic reports remain unaffected.";
        }
        catch (TimeoutException tex)
        {
            StatusMessage = $"{tex.Message} Deterministic analysis remains unaffected.";
            _logger.LogWarning(tex, "AI analysis execution timed out.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"AI analysis failed: {ex.Message}. Deterministic analysis remains unaffected.";
            _logger.LogError(ex, "AI analysis execution failed");
        }
        finally
        {
            IsGenerating = false;
            _aiCts?.Dispose();
            _aiCts = null;
        }

        await RefreshHistoryAsync();
    }

    [RelayCommand]
    public void CancelAiAnalysis()
    {
        if (IsGenerating && _aiCts != null)
        {
            StatusMessage = "Cancelling inference request...";
            _aiCts.Cancel();
        }
    }

    [RelayCommand]
    public async Task RefreshHistoryAsync()
    {
        try
        {
            var history = await _aiService.GetHistoryAsync(SelectedScan?.Id);
            AiReportHistory.Clear();
            foreach (var item in history)
            {
                AiReportHistory.Add(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load AI report history.");
        }
    }

    [RelayCommand]
    public void OpenReportFile(string? reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            StatusMessage = "No report file is available for this entry. Reports are only generated when AI analysis completes successfully.";
            return;
        }

        if (!File.Exists(reportPath))
        {
            StatusMessage = $"Report file not found on disk: {reportPath}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = reportPath,
                UseShellExecute = true
            });
            StatusMessage = $"Opened AI Review report: {Path.GetFileName(reportPath)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open report file {Path}", reportPath);
            StatusMessage = $"Could not open report file: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task InspectCandidateAsync(long candidateId)
    {
        if (OnNavigateToInventoryWithCandidate != null)
        {
            try
            {
                await OnNavigateToInventoryWithCandidate(candidateId, SelectedScan?.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed navigating to inventory with candidate {CandidateId}", candidateId);
            }
        }
    }

    [RelayCommand]
    public async Task ExportAiReviewTableAsync()
    {
        if (SelectedScan == null || _lastResponse == null)
        {
            StatusMessage = "No active AI analysis results available to export. Run AI analysis first.";
            return;
        }

        try
        {
            var (csvPath, mdPath) = await _aiService.ExportAiReviewTableAsync(
                SelectedScan.Id,
                SelectedModel ?? "local-model",
                _lastResponse,
                CancellationToken.None);

            StatusMessage = $"AI Review Tables exported: {Path.GetFileName(csvPath)} & {Path.GetFileName(mdPath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to export AI tables: {ex.Message}";
            _logger.LogError(ex, "Failed to export AI review tables from UI");
        }
    }

    partial void OnSelectedScanChanged(ScanRun? value)
    {
        _ = LoadChatHistoryAsync(value?.Id);
        _ = RefreshHistoryAsync();
    }

    public async Task LoadChatHistoryAsync(Guid? scanId)
    {
        try
        {
            ChatMessages.Clear();
            if (scanId == null) return;

            var history = await _aiService.GetChatHistoryAsync(scanId.Value);
            foreach (var msg in history)
            {
                ChatMessages.Add(msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chat history for scan {ScanId}", scanId);
        }
    }

    [RelayCommand]
    public async Task SendChatMessageAsync()
    {
        if (!IsAiEnabled || !_configurationService.Current.AI.Enabled)
        {
            ChatStatusMessage = "AI features are disabled in Settings.";
            return;
        }

        if (SelectedScan == null)
        {
            ChatStatusMessage = "Please select a target scan run first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedModel))
        {
            ChatStatusMessage = "Please select a local Ollama model first.";
            return;
        }

        var message = ChatInputText.Trim();
        if (string.IsNullOrEmpty(message)) return;

        ChatInputText = string.Empty;
        IsChatSending = true;
        ChatStatusMessage = "Waiting for local model response...";

        var tempUserMsg = new ChatMessageRecord
        {
            ScanRunId = SelectedScan.Id,
            Sender = "user",
            Message = message,
            CreatedAtUtc = DateTime.UtcNow
        };
        ChatMessages.Add(tempUserMsg);

        var timeoutSec = _configurationService.Current.AI.TimeoutSeconds > 0 ? _configurationService.Current.AI.TimeoutSeconds : 180;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

        try
        {
            var assistantMsg = await _aiService.SendChatMessageAsync(
                SelectedScan.Id,
                message,
                candidateIdsContext: null,
                modelOverride: SelectedModel,
                cancellationToken: cts.Token);

            ChatMessages.Add(assistantMsg);
            ChatStatusMessage = "Ready";
        }
        catch (TimeoutException tex)
        {
            ChatMessages.Remove(tempUserMsg);
            ChatInputText = message;
            ChatStatusMessage = $"Chat response timed out: {tex.Message}";
        }
        catch (OperationCanceledException)
        {
            ChatMessages.Remove(tempUserMsg);
            ChatInputText = message;
            ChatStatusMessage = "Inference timed out or was cancelled.";
        }
        catch (Exception ex)
        {
            ChatMessages.Remove(tempUserMsg);
            ChatInputText = message;
            ChatStatusMessage = $"Error: {ex.Message}";
            _logger.LogError(ex, "AI chat failed");
        }
        finally
        {
            IsChatSending = false;
        }
    }

    [RelayCommand]
    public async Task ClearChatHistoryAsync()
    {
        if (SelectedScan == null) return;

        try
        {
            await _aiService.ClearChatHistoryAsync(SelectedScan.Id);
            ChatMessages.Clear();
            ChatStatusMessage = "Chat history cleared.";
        }
        catch (Exception ex)
        {
            ChatStatusMessage = $"Failed to clear history: {ex.Message}";
            _logger.LogError(ex, "Failed to clear chat history");
        }
    }

    [RelayCommand]
    public void AddCandidateToChatContext(long candidateId)
    {
        var rec = Recommendations.FirstOrDefault(r => r.CandidateId == candidateId);
        if (rec != null)
        {
            ChatInputText = $"Can you explain candidate #{candidateId} ({rec.FileName}, category: {rec.SuggestedCategory})? Why is it flagged and what should I verify?";
        }
        else
        {
            ChatInputText = $"Can you explain candidate #{candidateId} from this scan and give safety advice?";
        }
    }
}
