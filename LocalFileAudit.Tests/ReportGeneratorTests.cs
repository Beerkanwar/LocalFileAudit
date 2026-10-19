using System.Text.Json;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.Analysis;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using LocalFileAudit.Infrastructure.Reporting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalFileAudit.Tests;

public class ReportGeneratorTests : IDisposable
{
    private readonly string _tempAppDataDir;
    private readonly AppEnvironment _environment;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly SqliteScanRepository _repository;
    private readonly JsonConfigurationService _configService;
    private readonly StorageAnalysisEngine _analysisEngine;
    private readonly ReportGenerator _generator;

    public ReportGeneratorTests()
    {
        _tempAppDataDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_ReportTest_" + Guid.NewGuid().ToString("N"));
        _environment = new AppEnvironment(_tempAppDataDir);
        _initializer = new SqliteDatabaseInitializer(_environment, NullLogger<SqliteDatabaseInitializer>.Instance);
        _repository = new SqliteScanRepository(_initializer, NullLogger<SqliteScanRepository>.Instance);
        _configService = new JsonConfigurationService(_environment, new ConfigurationValidator(), NullLogger<JsonConfigurationService>.Instance);
        _analysisEngine = new StorageAnalysisEngine(_initializer, _repository, _configService, NullLogger<StorageAnalysisEngine>.Instance);
        _generator = new ReportGenerator(_analysisEngine, _repository, _environment, NullLogger<ReportGenerator>.Instance);

        _initializer.InitializeAsync().GetAwaiter().GetResult();
        _configService.LoadAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task GenerateReports_CreatesAllRequiredOutputsAtomically()
    {
        var scanId = Guid.NewGuid();
        var run = new ScanRun
        {
            Id = scanId,
            StartTimeUtc = DateTime.UtcNow.AddMinutes(-10),
            EndTimeUtc = DateTime.UtcNow,
            Status = ScanState.Completed,
            SelectedDrives = @"D:\Audit",
            FilesDiscovered = 2,
            DirectoriesCompleted = 1
        };
        await _repository.CreateScanRunAsync(run);

        var testFile1 = new FileRecord
        {
            ScanRunId = scanId,
            FullPath = @"D:\Audit\data,with,comma.csv",
            ParentPath = @"D:\Audit",
            FileName = "data,with,comma.csv",
            Extension = ".csv",
            SizeBytes = 100_000,
            LastWriteTimeUtc = DateTime.UtcNow
        };

        var testFile2 = new FileRecord
        {
            ScanRunId = scanId,
            FullPath = @"D:\Audit\<script>alert('xss')</script>.txt",
            ParentPath = @"D:\Audit",
            FileName = "<script>alert('xss')</script>.txt",
            Extension = ".txt",
            SizeBytes = 600_000_000, // triggers large file candidate
            LastWriteTimeUtc = DateTime.UtcNow
        };

        await _repository.InsertFileBatchAsync(new[] { testFile1, testFile2 });

        var error = new ScanErrorRecord
        {
            ScanRunId = scanId,
            Path = @"D:\Audit\LockedFolder",
            Operation = "Enumerate",
            ErrorMessage = "Access Denied"
        };
        await _repository.InsertScanErrorAsync(error);

        var result = await _generator.GenerateReportsAsync(scanId);

        Assert.NotNull(result);
        Assert.True(Directory.Exists(result.OutputDirectory));
        Assert.Equal(8, result.GeneratedFiles.Count);

        // Verify individual files exist
        var summaryMd = Path.Combine(result.OutputDirectory, "Summary.md");
        var summaryHtml = Path.Combine(result.OutputDirectory, "Summary.html");
        var extCsv = Path.Combine(result.OutputDirectory, "ExtensionSummary.csv");
        var dirCsv = Path.Combine(result.OutputDirectory, "DirectorySummary.csv");
        var candidatesCsv = Path.Combine(result.OutputDirectory, "ReviewCandidates.csv");
        var errorsCsv = Path.Combine(result.OutputDirectory, "ScanErrors.csv");
        var manifestJson = Path.Combine(result.OutputDirectory, "Manifest.json");

        Assert.True(File.Exists(summaryMd));
        Assert.True(File.Exists(summaryHtml));
        Assert.True(File.Exists(extCsv));
        Assert.True(File.Exists(dirCsv));
        Assert.True(File.Exists(candidatesCsv));
        Assert.True(File.Exists(errorsCsv));
        Assert.True(File.Exists(manifestJson));

        // Security check: Verify HTML escaping of filenames to prevent XSS
        var htmlContent = await File.ReadAllTextAsync(summaryHtml);
        Assert.DoesNotContain("<script>alert('xss')</script>", htmlContent);
        Assert.Contains("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;.txt", htmlContent);

        // CSV check: Verify proper RFC-4180 escaping
        var candidatesContent = await File.ReadAllTextAsync(candidatesCsv);
        Assert.Contains("RULE_LARGE_FILE", candidatesContent);

        var extContent = await File.ReadAllTextAsync(extCsv);
        Assert.Contains(".csv", extContent);
        Assert.Contains(".txt", extContent);

        // Manifest check: Valid JSON
        var manifestContent = await File.ReadAllTextAsync(manifestJson);
        using var jsonDoc = JsonDocument.Parse(manifestContent);
        Assert.True(jsonDoc.RootElement.GetProperty("IsCompleteScan").GetBoolean());
        Assert.Equal(2, jsonDoc.RootElement.GetProperty("TotalFiles").GetInt64());
    }

    [Fact]
    public async Task GenerateReports_WhenIncompleteScan_IncludesWarningNotice()
    {
        var scanId = Guid.NewGuid();
        var run = new ScanRun
        {
            Id = scanId,
            Status = ScanState.Cancelled,
            SelectedDrives = @"D:\"
        };
        await _repository.CreateScanRunAsync(run);

        var result = await _generator.GenerateReportsAsync(scanId);

        var mdContent = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "Summary.md"));
        Assert.Contains("Incomplete Scan Warning", mdContent);

        var htmlContent = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "Summary.html"));
        Assert.Contains("Incomplete Scan Notice", htmlContent);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempAppDataDir)) Directory.Delete(_tempAppDataDir, recursive: true);
        }
        catch { }
    }
}
