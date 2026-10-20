using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.ViewModels;

public sealed partial class StorageAnalysisViewModel : ViewModelBase
{
    private readonly IStorageAnalysisEngine _analysisEngine;
    private readonly IReportGenerator _reportGenerator;
    private readonly IScanRepository _repository;
    private readonly ILogger<StorageAnalysisViewModel> _logger;

    [ObservableProperty]
    private ScanRun? _selectedScan;

    [ObservableProperty]
    private string _totalStorageText = "0.00 GB";

    [ObservableProperty]
    private string _totalFilesText = "0 files";

    [ObservableProperty]
    private string _candidatesCountText = "0 items";

    [ObservableProperty]
    private string _topExtensionText = "N/A";

    [ObservableProperty]
    private string _statusMessage = "Select a scan to analyze storage.";

    [ObservableProperty]
    private string _lastExportedReportPath = string.Empty;

    public bool HasExportedReport => !string.IsNullOrWhiteSpace(LastExportedReportPath);

    partial void OnLastExportedReportPathChanged(string value)
        => OnPropertyChanged(nameof(HasExportedReport));

    [ObservableProperty]
    private bool _isGeneratingReports;

    public ObservableCollection<ScanRun> AvailableScans { get; } = new();
    public ObservableCollection<ExtensionSummary> TopExtensions { get; } = new();
    public ObservableCollection<FileRecord> LargestFiles { get; } = new();
    public ObservableCollection<DirectorySummary> LargestDirectories { get; } = new();
    public ObservableCollection<CandidateClassification> ReviewCandidates { get; } = new();
    public ObservableCollection<ReportFolderInfo> GeneratedReports { get; } = new();

    public StorageAnalysisViewModel(
        IStorageAnalysisEngine analysisEngine,
        IReportGenerator reportGenerator,
        IScanRepository repository,
        ILogger<StorageAnalysisViewModel> logger)
    {
        _analysisEngine = analysisEngine ?? throw new ArgumentNullException(nameof(analysisEngine));
        _reportGenerator = reportGenerator ?? throw new ArgumentNullException(nameof(reportGenerator));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Title = "Storage Analysis";

        _ = InitializeAsync();
    }

    public async Task InitializeAsync()
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
            if (SelectedScan != null)
            {
                await LoadAnalysisAsync(SelectedScan.Id);
            }

            await RefreshGeneratedReportsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load scans for analysis.");
        }
    }

    public async Task RefreshGeneratedReportsAsync()
    {
        try
        {
            var reports = await _reportGenerator.GetGeneratedReportsAsync();
            GeneratedReports.Clear();
            foreach (var r in reports)
            {
                GeneratedReports.Add(r);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh generated reports list.");
        }
    }

    partial void OnSelectedScanChanged(ScanRun? value)
    {
        if (value != null)
        {
            _ = LoadAnalysisAsync(value.Id);
        }
    }

    [RelayCommand]
    public async Task LoadAnalysisAsync(Guid scanRunId)
    {
        try
        {
            StatusMessage = "Analyzing storage metrics from SQLite...";
            var summary = await _analysisEngine.AnalyzeScanAsync(scanRunId);

            TotalStorageText = $"{summary.TotalLogicalSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
            TotalFilesText = $"{summary.TotalFiles:N0} files";
            CandidatesCountText = $"{summary.ReviewCandidates.Count:N0} candidates";
            TopExtensionText = summary.TopExtensions.FirstOrDefault()?.Extension ?? "None";

            TopExtensions.Clear();
            foreach (var ext in summary.TopExtensions) TopExtensions.Add(ext);

            LargestFiles.Clear();
            foreach (var f in summary.LargestFiles) LargestFiles.Add(f);

            LargestDirectories.Clear();
            foreach (var d in summary.LargestDirectories) LargestDirectories.Add(d);

            ReviewCandidates.Clear();
            foreach (var c in summary.ReviewCandidates) ReviewCandidates.Add(c);

            StatusMessage = $"Analysis updated: {summary.TotalFiles:N0} files evaluated across {summary.TopExtensions.Count} file types.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to analyze scan: {ex.Message}";
            _logger.LogError(ex, "Error analyzing scan {ScanRunId}", scanRunId);
        }
    }

    [RelayCommand]
    public async Task ExportReportsAsync()
    {
        if (SelectedScan == null || IsGeneratingReports) return;

        IsGeneratingReports = true;
        StatusMessage = "Generating atomic reports (Summary.md, Summary.html, CSV tables, Manifest.json)...";

        try
        {
            var result = await _reportGenerator.GenerateReportsAsync(SelectedScan.Id);
            LastExportedReportPath = result.OutputDirectory;
            StatusMessage = $"Successfully generated {result.GeneratedFiles.Count} reports in: {result.OutputDirectory}";
            _logger.LogInformation("Exported reports to {OutputDir}", result.OutputDirectory);
            await RefreshGeneratedReportsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to generate reports: {ex.Message}";
            _logger.LogError(ex, "Failed to generate reports for scan {ScanRunId}", SelectedScan.Id);
        }
        finally
        {
            IsGeneratingReports = false;
        }
    }

    [RelayCommand]
    public void OpenReportDirectory()
    {
        if (string.IsNullOrWhiteSpace(LastExportedReportPath) || !Directory.Exists(LastExportedReportPath))
            return;

        OpenFolder(LastExportedReportPath);
    }

    [RelayCommand]
    public void OpenReportFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;
        OpenFolder(folderPath);
    }

    [RelayCommand]
    public void OpenHtmlSummary(string htmlPath)
    {
        if (string.IsNullOrWhiteSpace(htmlPath) || !File.Exists(htmlPath)) return;
        OpenFileWithShell(htmlPath);
    }

    [RelayCommand]
    public void OpenMarkdownSummary(string mdPath)
    {
        if (string.IsNullOrWhiteSpace(mdPath) || !File.Exists(mdPath)) return;
        OpenFileWithShell(mdPath);
    }

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open folder {Path}", path);
        }
    }

    private void OpenFileWithShell(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file {FilePath}", filePath);
        }
    }
}
