using System.Text;
using LocalFileAudit.Core.Analysis;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Infrastructure.Analysis;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using LocalFileAudit.Infrastructure.Reporting;
using LocalFileAudit.Infrastructure.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalFileAudit.Tests;

public sealed class DataAccuracyV12Tests : IDisposable
{
    private readonly string _testDir;
    private readonly AppEnvironment _environment;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly SqliteScanRepository _repository;
    private readonly StorageAnalysisEngine _analysisEngine;
    private readonly ReportGenerator _reportGenerator;

    public DataAccuracyV12Tests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_V12_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _environment = new AppEnvironment(_testDir);
        _initializer = new SqliteDatabaseInitializer(_environment, NullLogger<SqliteDatabaseInitializer>.Instance);
        _initializer.InitializeAsync().GetAwaiter().GetResult();

        _repository = new SqliteScanRepository(_initializer, NullLogger<SqliteScanRepository>.Instance);
        var configService = new LocalFileAudit.Infrastructure.Configuration.JsonConfigurationService(_environment, new LocalFileAudit.Core.Validation.ConfigurationValidator(), NullLogger<LocalFileAudit.Infrastructure.Configuration.JsonConfigurationService>.Instance);
        _analysisEngine = new StorageAnalysisEngine(_initializer, _repository, configService, NullLogger<StorageAnalysisEngine>.Instance);
        _reportGenerator = new ReportGenerator(_analysisEngine, _repository, _environment, NullLogger<ReportGenerator>.Instance, configService);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try
            {
                Directory.Delete(_testDir, recursive: true);
            }
            catch
            {
                // Best effort test cleanup
            }
        }
    }

    [Fact]
    public async Task FullInventoryExport_ExportsExpectedHeadersAndColumns()
    {
        var runId = Guid.NewGuid();
        var run = new ScanRun
        {
            Id = runId,
            StartTimeUtc = DateTime.UtcNow.AddMinutes(-10),
            Status = ScanState.Completed,
            SelectedDrives = "C:\\Test",
            ConfigurationSnapshot = "{}",
            FilesDiscovered = 2,
            ApplicationVersion = "1.2.0"
        };
        await _repository.CreateScanRunAsync(run);

        var file1 = new FileRecord
        {
            ScanRunId = runId,
            FullPath = "C:\\Test\\installer.iso",
            ParentPath = "C:\\Test",
            FileName = "installer.iso",
            Extension = ".iso",
            SizeBytes = 10L * 1024 * 1024,
            LastWriteTimeUtc = DateTime.UtcNow.AddDays(-800),
            Attributes = (int)FileAttributes.Normal,
            ObservedAtUtc = DateTime.UtcNow,
            MetadataStatus = ScanMetadataStatus.Ok
        };

        var file2 = new FileRecord
        {
            ScanRunId = runId,
            FullPath = "C:\\Test\\document.docx",
            ParentPath = "C:\\Test",
            FileName = "document.docx",
            Extension = ".docx",
            SizeBytes = 1024 * 50,
            LastWriteTimeUtc = DateTime.UtcNow.AddDays(-5),
            Attributes = (int)FileAttributes.Normal,
            ObservedAtUtc = DateTime.UtcNow,
            MetadataStatus = ScanMetadataStatus.Ok
        };
        await _repository.InsertFileBatchAsync(new[] { file1, file2 });

        var exportPath = Path.Combine(_testDir, "FullInventory.csv");
        var progressValues = new List<double>();
        var progress = new Progress<double>(p => progressValues.Add(p));

        var exportedCount = await _reportGenerator.ExportFullInventoryCsvAsync(runId, exportPath, progress: progress);

        Assert.Equal(2, exportedCount);
        Assert.True(File.Exists(exportPath));

        var lines = await File.ReadAllLinesAsync(exportPath);
        Assert.True(lines.Length >= 3);
        Assert.Equal("FullName,Extension,SizeBytes,LastWriteTime,AgeDays,RuleCategory", lines[0]);
        Assert.Contains("installer.iso", lines[1]);
        Assert.Contains(".iso", lines[1]);
        Assert.Contains("ArchiveCandidate", lines[1]);
    }

    [Fact]
    public async Task FullInventoryExport_WithFilter_FiltersCorrectly()
    {
        var runId = Guid.NewGuid();
        var run = new ScanRun
        {
            Id = runId,
            StartTimeUtc = DateTime.UtcNow.AddMinutes(-5),
            Status = ScanState.Completed,
            SelectedDrives = "C:\\Data",
            ConfigurationSnapshot = "{}",
            FilesDiscovered = 3,
            ApplicationVersion = "1.2.0"
        };
        await _repository.CreateScanRunAsync(run);

        var file1 = new FileRecord { ScanRunId = runId, FullPath = "C:\\Data\\video.mp4", ParentPath = "C:\\Data", FileName = "video.mp4", Extension = ".mp4", SizeBytes = 200 * 1024 * 1024, LastWriteTimeUtc = DateTime.UtcNow.AddDays(-200), Attributes = 0, ObservedAtUtc = DateTime.UtcNow, MetadataStatus = ScanMetadataStatus.Ok };
        var file2 = new FileRecord { ScanRunId = runId, FullPath = "C:\\Data\\notes.txt", ParentPath = "C:\\Data", FileName = "notes.txt", Extension = ".txt", SizeBytes = 500, LastWriteTimeUtc = DateTime.UtcNow.AddDays(-10), Attributes = 0, ObservedAtUtc = DateTime.UtcNow, MetadataStatus = ScanMetadataStatus.Ok };
        var file3 = new FileRecord { ScanRunId = runId, FullPath = "C:\\Data\\clip.mp4", ParentPath = "C:\\Data", FileName = "clip.mp4", Extension = ".mp4", SizeBytes = 10 * 1024 * 1024, LastWriteTimeUtc = DateTime.UtcNow.AddDays(-30), Attributes = 0, ObservedAtUtc = DateTime.UtcNow, MetadataStatus = ScanMetadataStatus.Ok };
        await _repository.InsertFileBatchAsync(new[] { file1, file2, file3 });

        var exportPath = Path.Combine(_testDir, "FilteredInventory.csv");
        var filter = new FileInventoryExportFilter
        {
            Extension = ".mp4",
            MinSizeBytes = 50 * 1024 * 1024
        };

        var exportedCount = await _reportGenerator.ExportFullInventoryCsvAsync(runId, exportPath, filter);

        Assert.Equal(1, exportedCount);
        var lines = await File.ReadAllLinesAsync(exportPath);
        Assert.Equal(2, lines.Length); // Header + 1 row
        Assert.Contains("video.mp4", lines[1]);
    }

    [Fact]
    public async Task FullInventoryExport_Cancellation_CleansUpStaging()
    {
        var runId = Guid.NewGuid();
        var run = new ScanRun
        {
            Id = runId,
            StartTimeUtc = DateTime.UtcNow,
            Status = ScanState.Completed,
            SelectedDrives = "C:\\",
            ConfigurationSnapshot = "{}",
            FilesDiscovered = 1,
            ApplicationVersion = "1.2.0"
        };
        await _repository.CreateScanRunAsync(run);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        var exportPath = Path.Combine(_testDir, "CancelledExport.csv");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _reportGenerator.ExportFullInventoryCsvAsync(runId, exportPath, cancellationToken: cts.Token);
        });

        Assert.False(File.Exists(exportPath));
        var tmpFiles = Directory.GetFiles(_testDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public async Task RunComparison_DetectsNewDeletedAndModifiedFiles()
    {
        var baselineRunId = Guid.NewGuid();
        var targetRunId = Guid.NewGuid();

        var baselineRun = new ScanRun { Id = baselineRunId, StartTimeUtc = DateTime.UtcNow.AddDays(-7), Status = ScanState.Completed, SelectedDrives = "C:\\Audit", ConfigurationSnapshot = "{}", FilesDiscovered = 3, ApplicationVersion = "1.2.0" };
        var targetRun = new ScanRun { Id = targetRunId, StartTimeUtc = DateTime.UtcNow, Status = ScanState.Completed, SelectedDrives = "C:\\Audit", ConfigurationSnapshot = "{}", FilesDiscovered = 3, ApplicationVersion = "1.2.0" };

        await _repository.CreateScanRunAsync(baselineRun);
        await _repository.CreateScanRunAsync(targetRun);

        var time1 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var time2 = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);

        // Baseline files:
        // A: deleted in target
        // B: modified in target (size increased from 200 to 300)
        // C: unchanged
        var baseFileA = new FileRecord { ScanRunId = baselineRunId, FullPath = "C:\\Audit\\fileA.txt", ParentPath = "C:\\Audit", FileName = "fileA.txt", Extension = ".txt", SizeBytes = 100, LastWriteTimeUtc = time1, Attributes = 0, ObservedAtUtc = time1, MetadataStatus = ScanMetadataStatus.Ok };
        var baseFileB = new FileRecord { ScanRunId = baselineRunId, FullPath = "C:\\Audit\\fileB.log", ParentPath = "C:\\Audit", FileName = "fileB.log", Extension = ".log", SizeBytes = 200, LastWriteTimeUtc = time1, Attributes = 0, ObservedAtUtc = time1, MetadataStatus = ScanMetadataStatus.Ok };
        var baseFileC = new FileRecord { ScanRunId = baselineRunId, FullPath = "C:\\Audit\\fileC.dat", ParentPath = "C:\\Audit", FileName = "fileC.dat", Extension = ".dat", SizeBytes = 500, LastWriteTimeUtc = time1, Attributes = 0, ObservedAtUtc = time1, MetadataStatus = ScanMetadataStatus.Ok };
        await _repository.InsertFileBatchAsync(new[] { baseFileA, baseFileB, baseFileC });

        // Target files:
        // B: modified (size = 300, time = time2)
        // C: unchanged
        // D: new file (size = 1000)
        var targetFileB = new FileRecord { ScanRunId = targetRunId, FullPath = "C:\\Audit\\fileB.log", ParentPath = "C:\\Audit", FileName = "fileB.log", Extension = ".log", SizeBytes = 300, LastWriteTimeUtc = time2, Attributes = 0, ObservedAtUtc = time2, MetadataStatus = ScanMetadataStatus.Ok };
        var targetFileC = new FileRecord { ScanRunId = targetRunId, FullPath = "C:\\Audit\\fileC.dat", ParentPath = "C:\\Audit", FileName = "fileC.dat", Extension = ".dat", SizeBytes = 500, LastWriteTimeUtc = time1, Attributes = 0, ObservedAtUtc = time2, MetadataStatus = ScanMetadataStatus.Ok };
        var targetFileD = new FileRecord { ScanRunId = targetRunId, FullPath = "C:\\Audit\\fileD.bin", ParentPath = "C:\\Audit", FileName = "fileD.bin", Extension = ".bin", SizeBytes = 1000, LastWriteTimeUtc = time2, Attributes = 0, ObservedAtUtc = time2, MetadataStatus = ScanMetadataStatus.Ok };
        await _repository.InsertFileBatchAsync(new[] { targetFileB, targetFileC, targetFileD });

        var comparison = await _repository.CompareScanRunsAsync(baselineRunId, targetRunId);

        Assert.NotNull(comparison);
        Assert.Equal(1, comparison.DeletedFilesCount); // fileA
        Assert.Equal(1, comparison.ModifiedFilesCount); // fileB
        Assert.Equal(1, comparison.NewFilesCount); // fileD
        Assert.Equal(3, comparison.Deltas.Count);

        // Net storage: New (1000) - Deleted (100) + Modified delta (300 - 200 = 100) = 1000
        Assert.Equal(1000, comparison.NetStorageDeltaBytes);

        var deletedDelta = comparison.Deltas.Single(d => d.DeltaType == DeltaType.Deleted);
        Assert.Equal("C:\\Audit\\fileA.txt", deletedDelta.FullPath);
        Assert.Equal(100, deletedDelta.BaselineSizeBytes);
        Assert.Equal(0, deletedDelta.TargetSizeBytes);

        var newDelta = comparison.Deltas.Single(d => d.DeltaType == DeltaType.New);
        Assert.Equal("C:\\Audit\\fileD.bin", newDelta.FullPath);
        Assert.Equal(1000, newDelta.TargetSizeBytes);

        var modDelta = comparison.Deltas.Single(d => d.DeltaType == DeltaType.Modified);
        Assert.Equal("C:\\Audit\\fileB.log", modDelta.FullPath);
        Assert.Equal(100, modDelta.SizeDeltaBytes);
    }

    [Fact]
    public async Task RunComparison_ExportDiffCsv_ProducesValidFile()
    {
        var baselineRunId = Guid.NewGuid();
        var targetRunId = Guid.NewGuid();

        var baselineRun = new ScanRun { Id = baselineRunId, StartTimeUtc = DateTime.UtcNow.AddDays(-1), Status = ScanState.Completed, SelectedDrives = "C:\\", ConfigurationSnapshot = "{}", FilesDiscovered = 1, ApplicationVersion = "1.2.0" };
        var targetRun = new ScanRun { Id = targetRunId, StartTimeUtc = DateTime.UtcNow, Status = ScanState.Completed, SelectedDrives = "C:\\", ConfigurationSnapshot = "{}", FilesDiscovered = 1, ApplicationVersion = "1.2.0" };
        await _repository.CreateScanRunAsync(baselineRun);
        await _repository.CreateScanRunAsync(targetRun);

        var fileNew = new FileRecord { ScanRunId = targetRunId, FullPath = "C:\\Data\\new.zip", ParentPath = "C:\\Data", FileName = "new.zip", Extension = ".zip", SizeBytes = 2048, LastWriteTimeUtc = DateTime.UtcNow, Attributes = 0, ObservedAtUtc = DateTime.UtcNow, MetadataStatus = ScanMetadataStatus.Ok };
        await _repository.InsertFileBatchAsync(new[] { fileNew });

        var comparison = await _repository.CompareScanRunsAsync(baselineRunId, targetRunId);
        var csvPath = Path.Combine(_testDir, "ComparisonExport.csv");

        await _repository.ExportComparisonCsvAsync(comparison, csvPath);

        Assert.True(File.Exists(csvPath));
        var content = await File.ReadAllTextAsync(csvPath);
        Assert.Contains("DeltaType,FullPath,Extension", content);
        Assert.Contains("New,C:\\Data\\new.zip,.zip,0,2048", content);
    }

    [Fact]
    public void StorageAccuracy_CloudPlaceholderAttributeCheck()
    {
        const int RecallOnDataAccess = 0x00400000;
        const int RecallOnOpen = 0x00040000;
        const int Offline = 0x00001000;

        Assert.True(FilesystemScanner.IsCloudPlaceholder((FileAttributes)RecallOnDataAccess));
        Assert.True(FilesystemScanner.IsCloudPlaceholder((FileAttributes)RecallOnOpen));
        Assert.True(FilesystemScanner.IsCloudPlaceholder((FileAttributes)Offline));
        Assert.True(FilesystemScanner.IsCloudPlaceholder((FileAttributes)(RecallOnDataAccess | (int)FileAttributes.Archive)));

        Assert.False(FilesystemScanner.IsCloudPlaceholder(FileAttributes.Normal));
        Assert.False(FilesystemScanner.IsCloudPlaceholder(FileAttributes.ReadOnly | FileAttributes.Hidden));
    }

    [Fact]
    public void StorageAccuracy_FileAllocationHelper_ReturnsValidSpace()
    {
        var space = FileAllocationHelper.GetVolumeSpace("C:\\");
        if (space.HasValue)
        {
            Assert.True(space.Value.TotalBytes > 0);
            Assert.True(space.Value.FreeBytes > 0);
        }
    }
}
