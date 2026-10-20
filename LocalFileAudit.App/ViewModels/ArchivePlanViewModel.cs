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

    [RelayCommand]
    public async Task AddBatchAsync()
    {
        if (string.IsNullOrWhiteSpace(NewBatchName))
        {
            MessageBox.Show("Please specify a descriptive name for this archive batch.", "Archive Planner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(NewSourcePath) || !Directory.Exists(NewSourcePath))
        {
            MessageBox.Show("Please specify a valid, existing source directory to archive.", "Archive Planner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(NewDestinationPath))
        {
            MessageBox.Show("Please specify a planned destination directory (e.g. E:\\Archive).", "Archive Planner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Estimate size and files in source
        long estimatedBytes = 0;
        int fileCount = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(NewSourcePath, "*", SearchOption.AllDirectories))
            {
                fileCount++;
                try { estimatedBytes += new FileInfo(f).Length; } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not completely evaluate source directory size for {Path}", NewSourcePath);
        }

        // Space warning check
        var space = _plannerService.GetDestinationFreeSpace(NewDestinationPath);
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
            BatchName = NewBatchName.Trim(),
            SourcePath = NewSourcePath.Trim(),
            PlannedDestinationPath = NewDestinationPath.Trim(),
            Reason = string.IsNullOrWhiteSpace(NewReason) ? "Cold storage offload" : NewReason.Trim(),
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
        StatusMessage = $"Added batch '{batch.BatchName}' ({batch.EstimatedFileCount:N0} files, {FormatBytes(batch.EstimatedSizeBytes)})";
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
