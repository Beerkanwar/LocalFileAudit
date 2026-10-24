using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.ViewModels;

public sealed class MinSizeOption
{
    public string DisplayName { get; init; } = string.Empty;
    public long Bytes { get; init; }
}

public sealed partial class DuplicateAnalysisViewModel : ViewModelBase
{
    private readonly IDuplicateAnalysisEngine _duplicateEngine;
    private readonly IScanRepository _repository;
    private readonly ILogger<DuplicateAnalysisViewModel> _logger;

    private CancellationTokenSource? _hashCts;

    [ObservableProperty]
    private ScanRun? _selectedScan;

    [ObservableProperty]
    private MinSizeOption? _selectedMinSizeOption;

    [ObservableProperty]
    private DuplicateGroup? _selectedGroup;

    [ObservableProperty]
    private FileRecord? _selectedFile;

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private bool _isHashing;

    [ObservableProperty]
    private double _hashingProgressPercent;

    [ObservableProperty]
    private string _hashingProgressText = string.Empty;

    [ObservableProperty]
    private string _groupsCountText = "0 groups";

    [ObservableProperty]
    private string _potentialWastedSpaceText = "0.00 GB";

    [ObservableProperty]
    private string _verifiedWastedSpaceText = "0.00 GB";

    [ObservableProperty]
    private bool _isVerifiedStage;

    [ObservableProperty]
    private string _statusMessage = "Select a scan and click 'Analyze Potential Duplicates' to begin Stage 1.";

    public ObservableCollection<ScanRun> AvailableScans { get; } = new();
    public ObservableCollection<MinSizeOption> MinSizeOptions { get; } = new();
    public ObservableCollection<DuplicateGroup> DuplicateGroups { get; } = new();
    public ObservableCollection<FileRecord> SelectedGroupFiles { get; } = new();

    public DuplicateAnalysisViewModel(
        IDuplicateAnalysisEngine duplicateEngine,
        IScanRepository repository,
        ILogger<DuplicateAnalysisViewModel> logger)
    {
        _duplicateEngine = duplicateEngine ?? throw new ArgumentNullException(nameof(duplicateEngine));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Title = "Duplicate Analysis";

        InitSizeOptions();
    }

    private bool _isInitialized;

    public async Task EnsureInitializedAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        await LoadScansAsync();
    }

    private void InitSizeOptions()
    {
        MinSizeOptions.Add(new MinSizeOption { DisplayName = "Any Size (> 1 KB)", Bytes = 1024 });
        MinSizeOptions.Add(new MinSizeOption { DisplayName = "> 1 MB", Bytes = 1024 * 1024 });
        MinSizeOptions.Add(new MinSizeOption { DisplayName = "> 10 MB", Bytes = 10 * 1024 * 1024 });
        MinSizeOptions.Add(new MinSizeOption { DisplayName = "> 50 MB", Bytes = 50 * 1024 * 1024 });
        MinSizeOptions.Add(new MinSizeOption { DisplayName = "> 100 MB", Bytes = 100 * 1024 * 1024 });
        MinSizeOptions.Add(new MinSizeOption { DisplayName = "> 500 MB", Bytes = 500 * 1024 * 1024 });
        SelectedMinSizeOption = MinSizeOptions[1]; // > 1 MB default
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
            _logger.LogError(ex, "Failed to load scans for duplicate analysis.");
        }
    }

    [RelayCommand]
    public async Task FindPotentialDuplicatesAsync()
    {
        if (SelectedScan == null || IsAnalyzing) return;

        IsAnalyzing = true;
        IsVerifiedStage = false;
        StatusMessage = "Stage 1: Grouping files by exact byte size...";

        try
        {
            var minBytes = SelectedMinSizeOption?.Bytes ?? 1024;
            var summary = await _duplicateEngine.FindPotentialDuplicatesBySizeAsync(SelectedScan.Id, minBytes);

            DuplicateGroups.Clear();
            foreach (var g in summary.Groups)
            {
                DuplicateGroups.Add(g);
            }

            GroupsCountText = $"{summary.PotentialGroupsCount:N0} potential groups";
            PotentialWastedSpaceText = $"{summary.PotentialWastedSizeGb:F2} GB";
            VerifiedWastedSpaceText = "Not verified";

            StatusMessage = $"Stage 1 complete: Found {summary.PotentialGroupsCount:N0} size-matching groups ({summary.TotalDuplicateFilesCount:N0} files, {summary.PotentialWastedSizeGb:F2} GB potential space). Click 'Verify Hashes' for cryptographic verification.";
            SelectedGroup = DuplicateGroups.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Duplicate grouping failed: {ex.Message}";
            _logger.LogError(ex, "Error finding potential duplicates by size");
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    public async Task VerifyHashesAsync()
    {
        if (SelectedScan == null || IsHashing) return;

        _hashCts = new CancellationTokenSource();
        IsHashing = true;
        StatusMessage = "Stage 2: Computing SHA-256 hashes asynchronously...";
        HashingProgressPercent = 0;

        var progress = new Progress<HashJobProgress>(p =>
        {
            HashingProgressPercent = p.PercentComplete;
            HashingProgressText = $"{p.FilesProcessed} of {p.TotalFilesToHash} files ({p.BytesHashed / (1024.0 * 1024.0):F0} MB hashed)";
        });

        try
        {
            var minBytes = SelectedMinSizeOption?.Bytes ?? 1024;
            var summary = await _duplicateEngine.ComputeHashesAndVerifyDuplicatesAsync(
                SelectedScan.Id,
                minBytes,
                progress,
                _hashCts.Token);

            DuplicateGroups.Clear();
            foreach (var g in summary.Groups)
            {
                DuplicateGroups.Add(g);
            }

            IsVerifiedStage = true;
            GroupsCountText = $"{summary.VerifiedGroupsCount:N0} verified duplicate groups";
            PotentialWastedSpaceText = $"{summary.PotentialWastedSizeGb:F2} GB";
            VerifiedWastedSpaceText = $"{summary.VerifiedWastedSizeGb:F2} GB";

            StatusMessage = $"Stage 2 complete: Cryptographically verified {summary.VerifiedGroupsCount:N0} duplicate groups with matching SHA-256 hashes ({summary.VerifiedWastedSizeGb:F2} GB wasted storage).";
            SelectedGroup = DuplicateGroups.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "SHA-256 hashing cancelled by user. Computed hashes are preserved in database.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Hashing failed: {ex.Message}";
            _logger.LogError(ex, "Failed to verify duplicates with hashes");
        }
        finally
        {
            IsHashing = false;
            _hashCts?.Dispose();
            _hashCts = null;
        }
    }

    [RelayCommand]
    public void CancelHashing()
    {
        if (IsHashing && _hashCts != null)
        {
            StatusMessage = "Cancelling hashing job...";
            _hashCts.Cancel();
        }
    }

    partial void OnSelectedGroupChanged(DuplicateGroup? value)
    {
        SelectedGroupFiles.Clear();
        if (value != null)
        {
            foreach (var f in value.Files)
            {
                SelectedGroupFiles.Add(f);
            }
            SelectedFile = SelectedGroupFiles.FirstOrDefault();
        }
    }

    [RelayCommand]
    public void CopyPath()
    {
        if (SelectedFile != null && !string.IsNullOrWhiteSpace(SelectedFile.FullPath))
        {
            Clipboard.SetText(SelectedFile.FullPath);
            StatusMessage = $"Copied path to clipboard: {SelectedFile.FileName}";
        }
    }

    [RelayCommand]
    public void RevealInExplorer()
    {
        if (SelectedFile == null || string.IsNullOrWhiteSpace(SelectedFile.FullPath)) return;

        try
        {
            var cleanPath = SelectedFile.FullPath.Trim().Trim('"');
            if (!File.Exists(cleanPath) && !Directory.Exists(cleanPath))
            {
                StatusMessage = "File or directory does not exist on disk.";
                return;
            }

            var args = $"/select,\"{cleanPath}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = args,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reveal file in Windows Explorer: {Path}", SelectedFile.FullPath);
            StatusMessage = $"Unable to open explorer: {ex.Message}";
        }
    }
}
