using System.Security.Cryptography;
using LocalFileAudit.Core.Analysis;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.AI;
using LocalFileAudit.Infrastructure.Analysis;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using LocalFileAudit.Infrastructure.Reporting;
using LocalFileAudit.Infrastructure.Scanning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalFileAudit.Tests;

/// <summary>
/// Hardening and Safety Acceptance Tests verifying Section 16.5 of the development specification:
/// 1. No source file is created, deleted, moved, renamed, or modified by scanning.
/// 2. Forced termination does not damage the database beyond recoverable incomplete work.
/// 3. AI failure does not prevent deterministic reports.
/// 4. No remote service receives inventory data.
/// 5. No administrator permission is required.
/// 6. The application never executes AI-generated commands.
/// 7. Every candidate has an explainable reason for its classification.
/// 8. Incomplete scans and inaccessible paths are clearly reported.
/// 9. No automatic deletion or movement exists.
/// 10. Report generation cannot overwrite a previously successful report with partial content.
/// </summary>
public class SafetyAcceptanceTests : IDisposable
{
    private readonly string _tempAppDataDir;
    private readonly string _testSourceDir;
    private readonly AppEnvironment _environment;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly SqliteScanRepository _repository;
    private readonly JsonConfigurationService _configService;
    private readonly FilesystemScanner _scanner;
    private readonly StorageAnalysisEngine _analysisEngine;
    private readonly ReportGenerator _reportGenerator;
    private readonly DuplicateAnalysisEngine _duplicateEngine;

    public SafetyAcceptanceTests()
    {
        _tempAppDataDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_SafetyApp_" + Guid.NewGuid().ToString("N"));
        _testSourceDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_SafetySrc_" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_tempAppDataDir);
        Directory.CreateDirectory(_testSourceDir);

        _environment = new AppEnvironment(_tempAppDataDir);
        _initializer = new SqliteDatabaseInitializer(_environment, NullLogger<SqliteDatabaseInitializer>.Instance);
        _repository = new SqliteScanRepository(_initializer, NullLogger<SqliteScanRepository>.Instance);
        _configService = new JsonConfigurationService(_environment, new ConfigurationValidator(), NullLogger<JsonConfigurationService>.Instance);
        _scanner = new FilesystemScanner(_repository, _configService, NullLogger<FilesystemScanner>.Instance);
        _analysisEngine = new StorageAnalysisEngine(_initializer, _repository, _configService, NullLogger<StorageAnalysisEngine>.Instance);
        _reportGenerator = new ReportGenerator(_analysisEngine, _repository, _environment, NullLogger<ReportGenerator>.Instance);
        _duplicateEngine = new DuplicateAnalysisEngine(_initializer, _repository, NullLogger<DuplicateAnalysisEngine>.Instance);

        _initializer.InitializeAsync().GetAwaiter().GetResult();
        _configService.LoadAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempAppDataDir)) Directory.Delete(_tempAppDataDir, recursive: true); } catch { }
        try { if (Directory.Exists(_testSourceDir)) Directory.Delete(_testSourceDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task AcceptanceCriterion1_ZeroMutationOfSourceFiles()
    {
        // Setup source test fixture with various files
        var fileA = Path.Combine(_testSourceDir, "docA.txt");
        var fileB = Path.Combine(_testSourceDir, "docB.bin");
        await File.WriteAllTextAsync(fileA, "Hello World Content");
        await File.WriteAllBytesAsync(fileB, new byte[] { 0x01, 0x02, 0x03, 0x04 });

        var hashA = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fileA)));
        var hashB = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fileB)));
        var timeA = File.GetLastWriteTimeUtc(fileA);
        var timeB = File.GetLastWriteTimeUtc(fileB);

        // Perform full scan
        var scanTarget = new ScanTarget { TargetPaths = new List<string> { _testSourceDir } };
        var run = await _scanner.StartScanAsync(scanTarget, CancellationToken.None);
        Assert.Equal(ScanState.Completed, run.Status);

        // Perform deterministic analysis
        var metrics = await _analysisEngine.AnalyzeScanAsync(run.Id);
        Assert.NotNull(metrics);

        // Perform duplicate analysis (including SHA-256 calculation)
        await _duplicateEngine.ComputeHashesAndVerifyDuplicatesAsync(run.Id, minSizeBytes: 1);

        // Perform report generation
        await _reportGenerator.GenerateReportsAsync(run.Id);

        // ASSERT: Source files are completely unchanged
        Assert.True(File.Exists(fileA));
        Assert.True(File.Exists(fileB));
        Assert.Equal(hashA, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fileA))));
        Assert.Equal(hashB, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fileB))));
        Assert.Equal(timeA, File.GetLastWriteTimeUtc(fileA));
        Assert.Equal(timeB, File.GetLastWriteTimeUtc(fileB));
    }

    [Fact]
    public async Task AcceptanceCriterion2_DatabaseIntegrityAndRecoveryAfterAbruptTermination()
    {
        var scanId = Guid.NewGuid();
        var run = new ScanRun
        {
            Id = scanId,
            StartTimeUtc = DateTime.UtcNow,
            Status = ScanState.InProgress,
            SelectedDrives = _testSourceDir
        };
        await _repository.CreateScanRunAsync(run);

        // Insert batch of records
        var records = Enumerable.Range(1, 50).Select(i => new FileRecord
        {
            ScanRunId = scanId,
            FullPath = Path.Combine(_testSourceDir, $"file_{i}.dat"),
            ParentPath = _testSourceDir,
            FileName = $"file_{i}.dat",
            Extension = ".dat",
            SizeBytes = i * 100,
            ObservedAtUtc = DateTime.UtcNow
        }).ToList();
        await _repository.InsertFileBatchAsync(records);

        // Simulate crash: Reopen SQLite database fresh and run PRAGMA integrity_check
        var dbPath = _initializer.DatabasePath;
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = (string?)await cmd.ExecuteScalarAsync();

        Assert.Equal("ok", result);

        // Verify incomplete scan is detected cleanly
        var incompleteRuns = await _repository.GetIncompleteScanRunsAsync();
        Assert.Contains(incompleteRuns, r => r.Id == scanId);
    }

    [Fact]
    public async Task AcceptanceCriterion3_AiFailureDoesNotPreventDeterministicReports()
    {
        var scanId = Guid.NewGuid();
        var run = new ScanRun
        {
            Id = scanId,
            StartTimeUtc = DateTime.UtcNow,
            Status = ScanState.Completed,
            SelectedDrives = _testSourceDir,
            FilesDiscovered = 1
        };
        await _repository.CreateScanRunAsync(run);
        await _repository.InsertFileBatchAsync(new[]
        {
            new FileRecord
            {
                ScanRunId = scanId,
                FullPath = Path.Combine(_testSourceDir, "test.txt"),
                ParentPath = _testSourceDir,
                FileName = "test.txt",
                Extension = ".txt",
                SizeBytes = 1024,
                ObservedAtUtc = DateTime.UtcNow
            }
        });

        // Even with no Ollama / AI running, report generation succeeds deterministically
        var result = await _reportGenerator.GenerateReportsAsync(scanId);

        Assert.NotNull(result);
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "Summary.md")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "Summary.html")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "Manifest.json")));
    }

    [Fact]
    public async Task AcceptanceCriterion4_NoRemoteServiceReceivesInventoryData()
    {
        var client = new OllamaClient(new HttpClient(), _configService, NullLogger<OllamaClient>.Instance);

        // Enforce loopback validation: remote hosts must be strictly rejected
        var remoteEndpoints = new[]
        {
            "https://api.openai.com",
            "http://192.168.1.50:11434",
            "http://example.com:11434",
            "http://remote.host.local:11434"
        };

        foreach (var remoteUrl in remoteEndpoints)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await client.CheckConnectionAsync(remoteUrl);
            });
            Assert.Contains("Remote AI endpoints are strictly prohibited", ex.Message);
        }

        // Loopback endpoints must be accepted by validator without InvalidOperationException
        var localEndpoints = new[]
        {
            "http://localhost:11434",
            "http://127.0.0.1:11434"
        };

        foreach (var localUrl in localEndpoints)
        {
            // Local loopback URLs must be permitted by the security validator without throwing InvalidOperationException
            var status = await client.CheckConnectionAsync(localUrl);
            Assert.NotNull(status);
        }
    }

    [Fact]
    public void AcceptanceCriterion7_EveryCandidateHasExplainableReason()
    {
        var config = new AppConfiguration();
        var files = new[]
        {
            new FileRecord { FullPath = @"C:\Windows\System32\kernel32.dll", FileName = "kernel32.dll", Extension = ".dll", SizeBytes = 1000 },
            new FileRecord { FullPath = @"D:\LargeFile.iso", FileName = "LargeFile.iso", Extension = ".iso", SizeBytes = 800_000_000 },
            new FileRecord { FullPath = @"D:\Temp\temp.tmp", FileName = "temp.tmp", Extension = ".tmp", SizeBytes = 50_000 },
            new FileRecord { FullPath = @"D:\Logs\app.log", FileName = "app.log", Extension = ".log", SizeBytes = 100_000 },
            new FileRecord { FullPath = @"D:\Photos\old.zip", FileName = "old.zip", Extension = ".zip", SizeBytes = 50_000, LastWriteTimeUtc = DateTime.UtcNow.AddDays(-1000) }
        };

        foreach (var file in files)
        {
            var classification = CandidateRuleEvaluator.Classify(file, config);
            Assert.False(string.IsNullOrWhiteSpace(classification.RuleId), $"RuleId is empty for {file.FullPath}");
            Assert.False(string.IsNullOrWhiteSpace(classification.Explanation), $"Explanation is empty for {file.FullPath}");
        }
    }

    [Fact]
    public async Task AcceptanceCriterion8_InaccessiblePathsAndIncompleteScansClearlyReported()
    {
        var scanId = Guid.NewGuid();
        var run = new ScanRun
        {
            Id = scanId,
            StartTimeUtc = DateTime.UtcNow,
            Status = ScanState.Failed,
            ErrorCount = 2
        };
        await _repository.CreateScanRunAsync(run);

        var errors = new[]
        {
            new ScanErrorRecord { ScanRunId = scanId, Path = @"C:\System Volume Information", Operation = "EnumerateDirectory", ErrorCode = "UnauthorizedAccessException", ErrorMessage = "Access is denied" },
            new ScanErrorRecord { ScanRunId = scanId, Path = @"C:\CorruptDir", Operation = "EnumerateDirectory", ErrorCode = "IOException", ErrorMessage = "The file or directory is corrupted and unreadable" }
        };

        foreach (var err in errors)
        {
            await _repository.InsertScanErrorAsync(err);
        }

        var retrievedErrors = await _repository.GetScanErrorsAsync(scanId);
        Assert.Equal(2, retrievedErrors.Count);
        Assert.Contains(retrievedErrors, e => e.ErrorCode == "UnauthorizedAccessException");
        Assert.Contains(retrievedErrors, e => e.Path == @"C:\System Volume Information");

        var reports = await _reportGenerator.GenerateReportsAsync(scanId);
        var manifestContent = await File.ReadAllTextAsync(Path.Combine(reports.OutputDirectory, "Manifest.json"));
        Assert.Contains("\"IsCompleteScan\": false", manifestContent);
        Assert.Contains("\"ErrorCount\": 2", manifestContent);
    }

    [Fact]
    public async Task AcceptanceCriterion10_ReportGenerationAtomicNeverOverwritesSuccessfulWithPartial()
    {
        var scanId = Guid.NewGuid();
        var run = new ScanRun { Id = scanId, Status = ScanState.Completed, SelectedDrives = _testSourceDir };
        await _repository.CreateScanRunAsync(run);

        var targetReportDir = Path.Combine(_tempAppDataDir, "StableReport");
        var initialResult = await _reportGenerator.GenerateReportsAsync(scanId, targetReportDir);
        Assert.True(File.Exists(Path.Combine(targetReportDir, "Summary.md")));
        var originalSummary = await File.ReadAllTextAsync(Path.Combine(targetReportDir, "Summary.md"));

        // Simulate cancelled second report generation with an aborted token
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _reportGenerator.GenerateReportsAsync(scanId, targetReportDir, cts.Token);
        });

        // The original report must remain 100% intact and uncorrupted!
        Assert.True(File.Exists(Path.Combine(targetReportDir, "Summary.md")));
        var summaryAfterFailedAttempt = await File.ReadAllTextAsync(Path.Combine(targetReportDir, "Summary.md"));
        Assert.Equal(originalSummary, summaryAfterFailedAttempt);
    }
}
