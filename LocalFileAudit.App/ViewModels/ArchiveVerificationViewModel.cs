using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFileAudit.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.ViewModels;

public sealed partial class ArchiveVerificationViewModel : ViewModelBase
{
    private readonly IArchiveVerificationService _verificationService;
    private readonly INotificationService? _notificationService;
    private readonly ILogger<ArchiveVerificationViewModel> _logger;
    private CancellationTokenSource? _verifyCts;

    [ObservableProperty]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    private string _destinationPath = string.Empty;

    [ObservableProperty]
    private bool _calculateSha256 = true;

    [ObservableProperty]
    private bool _isVerifying;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _statusMessage = "Pick Source and Destination directories to verify relative path matches, sizes, and hashes.";

    [ObservableProperty]
    private bool _hasVerificationResult;

    [ObservableProperty]
    private ArchiveVerificationSummary? _summary;

    [ObservableProperty]
    private string _matchesText = "0 files";

    [ObservableProperty]
    private string _missingText = "0 files";

    [ObservableProperty]
    private string _sizeMismatchText = "0 files";

    [ObservableProperty]
    private string _hashMismatchText = "0 files";

    [ObservableProperty]
    private string _extraText = "0 files";

    [ObservableProperty]
    private string _selectedStatusFilter = "All";

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private string _resultsCountText = string.Empty;

    [ObservableProperty]
    private bool _isTruncated;

    public ObservableCollection<ArchiveVerificationItem> FilteredItems { get; } = new();
    public ObservableCollection<string> StatusFilterOptions { get; } = new()
    {
        "All",
        "Match",
        "MissingAtDestination",
        "SizeMismatch",
        "HashMismatch",
        "HashError",
        "ExtraAtDestination"
    };

    public ArchiveVerificationViewModel(
        IArchiveVerificationService verificationService,
        ILogger<ArchiveVerificationViewModel> logger,
        INotificationService? notificationService = null)
    {
        _verificationService = verificationService ?? throw new ArgumentNullException(nameof(verificationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notificationService = notificationService;

        Title = "Archive Verifier";
    }

    public void Refresh()
    {
        if (Summary != null)
        {
            ApplyFilter();
        }
    }

    [RelayCommand]
    public async Task StartVerificationAsync()
    {
        if (IsVerifying) return;

        if (string.IsNullOrWhiteSpace(SourcePath) || !Directory.Exists(SourcePath))
        {
            MessageBox.Show("Please select a valid, existing source directory.", "Archive Verifier", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationPath) || !Directory.Exists(DestinationPath))
        {
            MessageBox.Show("Please select a valid, existing destination directory.", "Archive Verifier", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _verifyCts = new CancellationTokenSource();
        IsVerifying = true;
        ProgressPercent = 0;
        StatusMessage = "Traversing source and destination directories...";

        try
        {
            var progress = new Progress<double>(p =>
            {
                ProgressPercent = p;
                StatusMessage = $"Verifying archive items: {p:F0}%";
            });

            var res = await _verificationService.VerifyArchiveAsync(
                SourcePath,
                DestinationPath,
                CalculateSha256,
                progress,
                _verifyCts.Token);

            Summary = res;
            MatchesText = $"{res.MatchCount:N0} matches";
            MissingText = $"{res.MissingCount:N0} missing";
            SizeMismatchText = $"{res.SizeMismatchCount:N0} size diff";
            HashMismatchText = $"{res.HashMismatchCount:N0} hash diff";
            ExtraText = $"{res.ExtraCount:N0} extra on dest";

            ApplyFilter();
            HasVerificationResult = true;

            var msg = $"Verification finished in {res.Duration.TotalSeconds:F1}s: {res.MatchCount:N0} matches, {res.MissingCount:N0} missing, {res.SizeMismatchCount:N0} size diffs, {res.HashMismatchCount:N0} hash diffs, {res.HashErrorCount:N0} hash errors";
            if (res.IsPartial)
            {
                msg += " (PARTIAL SCAN: Inaccessible files/directories skipped. DO NOT DELETE ORIGINALS)";
            }
            StatusMessage = msg;

            var matchDesc = CalculateSha256
                ? $"{res.MatchCount:N0} matches verified via SHA-256 hash"
                : $"{res.MatchCount:N0} matches verified by size only (unhashed)";

            var isSafeSuccess = !res.IsPartial && res.MissingCount == 0 && res.SizeMismatchCount == 0 && res.HashMismatchCount == 0 && res.HashErrorCount == 0;
            _notificationService?.NotifyScanCompleted(
                res.IsPartial ? "Archive Verification Partial Warning" : "Archive Verification Complete",
                res.IsPartial
                    ? $"Verification was PARTIAL ({res.EnumerationErrors.Count} unreadable paths). Do not delete source files."
                    : $"{matchDesc}. {res.MissingCount + res.SizeMismatchCount + res.HashMismatchCount + res.HashErrorCount} discrepancies/errors discovered.",
                isSuccess: isSafeSuccess);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Verification was cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during archive verification");
            StatusMessage = $"Verification error: {ex.Message}";
            MessageBox.Show($"Verification error:\n\n{ex.Message}", "Verification Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsVerifying = false;
            _verifyCts?.Dispose();
            _verifyCts = null;
        }
    }

    [RelayCommand]
    public void CancelVerification()
    {
        if (IsVerifying && _verifyCts != null && !_verifyCts.IsCancellationRequested)
        {
            _verifyCts.Cancel();
            StatusMessage = "Cancelling verification...";
        }
    }

    [RelayCommand]
    public async Task ExportReportCsvAsync()
    {
        if (Summary == null || Summary.Items.Count == 0)
        {
            MessageBox.Show("No verification results to export.", "Export Report", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Archive Verification Report to CSV",
            FileName = $"ArchiveVerification-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv"
        };

        if (sfd.ShowDialog() != true) return;

        try
        {
            var path = await _verificationService.ExportVerificationCsvAsync(Summary, sfd.FileName);
            StatusMessage = $"Verification report exported to {Path.GetFileName(path)}";
            MessageBox.Show($"Report exported successfully to:\n\n{path}", "Report Exported", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export verification CSV to {Path}", sfd.FileName);
            MessageBox.Show($"Failed to export report:\n\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private CancellationTokenSource? _filterDebounceCts;

    partial void OnSelectedStatusFilterChanged(string value) => ApplyFilter();
    partial void OnSearchFilterChanged(string value)
    {
        _filterDebounceCts?.Cancel();
        _filterDebounceCts?.Dispose();
        _filterDebounceCts = new CancellationTokenSource();
        var token = _filterDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    Application.Current?.Dispatcher?.Invoke(ApplyFilter);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private void ApplyFilter()
    {
        if (Summary == null)
        {
            FilteredItems.Clear();
            return;
        }

        var query = Summary.Items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SelectedStatusFilter) && SelectedStatusFilter != "All")
        {
            if (Enum.TryParse<VerificationStatus>(SelectedStatusFilter, ignoreCase: true, out var status))
            {
                query = query.Where(i => i.Status == status);
            }
        }

        if (!string.IsNullOrWhiteSpace(SearchFilter))
        {
            var term = SearchFilter.Trim();
            query = query.Where(i => i.RelativePath.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                     i.Details.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var allItems = query.ToList();
        var total = allItems.Count;

        FilteredItems.Clear();
        foreach (var item in allItems.Take(1000))
        {
            FilteredItems.Add(item);
        }

        IsTruncated = total > 1000;
        ResultsCountText = total > 1000
            ? $"Showing 1,000 of {total:N0} records (Export CSV to view full dataset)"
            : $"{total:N0} records";
    }
}
