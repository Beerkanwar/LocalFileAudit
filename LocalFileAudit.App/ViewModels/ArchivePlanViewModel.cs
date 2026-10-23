using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.ViewModels;

public sealed partial class ArchivePlanViewModel : ViewModelBase
{
    private readonly IArchivePlannerService _plannerService;
    private readonly IScanRepository _repository;
    private readonly IAppEnvironment _environment;
    private readonly ILogger<ArchivePlanViewModel> _logger;

    [ObservableProperty]
    private string _planName = "Local Storage Archive Plan";

    [ObservableProperty]
    private string _newBatchName = string.Empty;

    [ObservableProperty]
    private string _newSourcePath = string.Empty;

    [ObservableProperty]
    private string _newDestinationPath = string.Empty;

    [ObservableProperty]
    private string _newReason = string.Empty;

    [ObservableProperty]
    private string _destinationFreeSpaceText = "Destination not checked";

    [ObservableProperty]
    private bool _hasSpaceWarning;

    [ObservableProperty]
    private string _spaceWarningMessage = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Build archive batches to offload cold data to external drives.";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddBatchCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditForm))]
    private bool _isEstimating;

    public bool CanEditForm => !IsEstimating;
    private bool CanAddBatch => !IsEstimating;

    private CancellationTokenSource? _estimationCts;

    public ObservableCollection<ArchiveBatch> Batches { get; } = new();

    public ArchivePlanViewModel(
        IArchivePlannerService plannerService,
        IScanRepository repository,
        IAppEnvironment environment,
        ILogger<ArchivePlanViewModel> logger)
    {
        _plannerService = plannerService ?? throw new ArgumentNullException(nameof(plannerService));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Title = "Archive Plan";
    }

    public async Task LoadPlanAsync()
    {
        IsLoading = true;
        try
        {
            var plan = await _plannerService.LoadPlanAsync();
            PlanName = plan.PlanName;

            Batches.Clear();
            foreach (var b in plan.Batches)
            {
                Batches.Add(b);
            }

            StatusMessage = $"Loaded plan with {Batches.Count} archive batches.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load archive plan");
            StatusMessage = $"Failed to load plan: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnNewDestinationPathChanged(string value)
    {
        CheckDestinationSpace(value);
    }

    private void CheckDestinationSpace(string destinationPath)
    {
        HasSpaceWarning = false;
        SpaceWarningMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            DestinationFreeSpaceText = "No destination specified";
            return;
        }

        var space = _plannerService.GetDestinationFreeSpace(destinationPath);
        if (space.HasValue)
        {
            var freeGb = space.Value.FreeBytes / (1024.0 * 1024.0 * 1024.0);
            var totalGb = space.Value.TotalBytes / (1024.0 * 1024.0 * 1024.0);
            DestinationFreeSpaceText = $"{freeGb:F1} GB free of {totalGb:F1} GB";
        }
        else
        {
            DestinationFreeSpaceText = "Unknown (drive not ready or unmapped)";
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddBatch))]
    public async Task AddBatchAsync()
    {
        // 1. Capture all form values into locals upfront before validation and asynchronous estimation
        var trimmedName = NewBatchName?.Trim() ?? string.Empty;
        var sourcePath = NewSourcePath?.Trim() ?? string.Empty;
        var destPath = NewDestinationPath?.Trim() ?? string.Empty;
        var reason = string.IsNullOrWhiteSpace(NewReason) ? "Cold storage offload" : NewReason.Trim();

        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            MessageBox.Show("Please specify a descriptive name for this archive batch.", "Archive Planner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (trimmedName.IndexOfAny(new[] { '\r', '\n' }) >= 0 || trimmedName.Any(char.IsControl))
        {
            MessageBox.Show("Batch name cannot contain newlines or control characters.", "Archive Planner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
        {
            MessageBox.Show("Please specify a valid, existing source directory to archive.", "Archive Planner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(destPath))
        {
            MessageBox.Show("Please specify a planned destination directory (e.g. E:\\Archive).", "Archive Planner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            LocalFileAudit.Infrastructure.Archive.ArchivePlannerService.ValidatePathsDoNotOverlap(sourcePath, destPath);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Archive Planner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Asynchronously estimate size and file count off UI thread, skipping reparse points
        IsEstimating = true;
        IsLoading = true;
        _estimationCts = new CancellationTokenSource();
        DirectoryEstimationResult estimation;

        try
        {
            StatusMessage = "Estimating batch size (skipping junctions)...";
            var progress = new Progress<string>(msg => StatusMessage = msg);
            estimation = await _plannerService.EstimateDirectoryAsync(sourcePath, progress, _estimationCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Batch estimation was cancelled.";
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not completely evaluate source directory size for {Path}", sourcePath);
            estimation = new DirectoryEstimationResult { TotalBytes = 0, FileCount = 0, IsPartial = true };
        }
        finally
        {
            IsEstimating = false;
            IsLoading = false;
            _estimationCts?.Dispose();
            _estimationCts = null;
        }

        long estimatedBytes = estimation.TotalBytes;
        int fileCount = estimation.FileCount;

        if (estimation.IsPartial)
        {
            MessageBox.Show(
                $"Notice: Some files or subfolders could not be accessed during estimation (e.g. permission restrictions).\n\n" +
                $"Estimated size is partial ({fileCount:N0} files, {FormatBytes(estimatedBytes)}).\n" +
                $"The free space check will proceed with this partial total.",
                "Partial Batch Size Estimate",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        // Space warning check
        var space = _plannerService.GetDestinationFreeSpace(destPath);
        if (space.HasValue && space.Value.FreeBytes < estimatedBytes)
        {
            var neededGb = estimatedBytes / (1024.0 * 1024.0 * 1024.0);
            var freeGb = space.Value.FreeBytes / (1024.0 * 1024.0 * 1024.0);
            var res = MessageBox.Show(
                $"Warning: Planned batch size ({neededGb:F1} GB) exceeds destination free space ({freeGb:F1} GB).\n\nDo you want to add this batch anyway?",
                "Insufficient Destination Space",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes) return;
        }

        var batch = new ArchiveBatch
        {
            BatchName = trimmedName,
            SourcePath = sourcePath,
            PlannedDestinationPath = destPath,
            Reason = reason,
            EstimatedSizeBytes = estimatedBytes,
            EstimatedFileCount = fileCount,
            IsVerified = false,
            CreatedAtUtc = DateTime.UtcNow
        };

        Batches.Add(batch);
        await PersistPlanAsync();

        // Clear input fields
        NewBatchName = string.Empty;
        NewSourcePath = string.Empty;
        NewDestinationPath = string.Empty;
        NewReason = string.Empty;
        DestinationFreeSpaceText = "Batch added successfully.";
        StatusMessage = estimation.IsPartial
            ? $"Added batch '{batch.BatchName}' (PARTIAL: {batch.EstimatedFileCount:N0} files, {FormatBytes(batch.EstimatedSizeBytes)})"
            : $"Added batch '{batch.BatchName}' ({batch.EstimatedFileCount:N0} files, {FormatBytes(batch.EstimatedSizeBytes)})";
    }

    [RelayCommand]
    public void CancelEstimation()
    {
        if (IsEstimating && _estimationCts != null && !_estimationCts.IsCancellationRequested)
        {
            _estimationCts.Cancel();
            StatusMessage = "Cancelling estimation...";
        }
    }

    [RelayCommand]
    public async Task RemoveBatchAsync(ArchiveBatch? batch)
    {
        if (batch == null) return;
        Batches.Remove(batch);
        await PersistPlanAsync();
        StatusMessage = $"Removed batch '{batch.BatchName}'";
    }

    [RelayCommand]
    public async Task MarkVerifiedAsync(ArchiveBatch? batch)
    {
        if (batch == null) return;
        batch.IsVerified = true;
        batch.VerifiedAtUtc = DateTime.UtcNow;
        await _plannerService.MarkBatchVerifiedAsync(batch.Id);
        // Refresh UI
        var index = Batches.IndexOf(batch);
        if (index >= 0)
        {
            Batches[index] = batch;
        }
        StatusMessage = $"Batch '{batch.BatchName}' marked as verified.";
    }

    [RelayCommand]
    public async Task GenerateScriptsAsync()
    {
        if (Batches.Count == 0)
        {
            MessageBox.Show("No batches have been added to the archive plan yet.", "Generate Scripts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outDir = Path.Combine(_environment.ReportsDirectory, $"ArchiveScripts-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        var plan = new ArchivePlan
        {
            PlanName = PlanName,
            Batches = Batches.ToList(),
            LastModifiedUtc = DateTime.UtcNow
        };

        try
        {
            var result = await _plannerService.GenerateArchiveScriptsAsync(plan, outDir);
            StatusMessage = $"Scripts generated in {outDir}";

            MessageBox.Show(
                $"Archive scripts and manifest generated successfully!\n\n" +
                $"• Manifest: {Path.GetFileName(result.ManifestCsvPath)}\n" +
                $"• Preview Script: {Path.GetFileName(result.PreviewScriptPath)} (robocopy /L)\n" +
                $"• Copy Script: {Path.GetFileName(result.CopyScriptPath)}\n\n" +
                $"Location:\n{outDir}\n\n" +
                $"REMINDER: This application never executes file operations. Inspect the scripts and run them manually in PowerShell when ready.",
                "Archive Scripts Ready",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate archive scripts");
            MessageBox.Show($"Failed to generate archive scripts:\n\n{ex.Message}", "Script Generation Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task PersistPlanAsync()
    {
        var plan = new ArchivePlan
        {
            PlanName = PlanName,
            Batches = Batches.ToList(),
            LastModifiedUtc = DateTime.UtcNow
        };
        await _plannerService.SavePlanAsync(plan);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        if (bytes >= 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes:N0} B";
    }
}
