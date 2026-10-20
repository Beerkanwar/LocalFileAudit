using System.Text;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.AI;
using LocalFileAudit.Infrastructure.Analysis;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalFileAudit.Tests;

public sealed class QualityOfLifeV11Tests : IDisposable
{
    private readonly string _testDir;
    private readonly AppEnvironment _environment;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly SqliteScanRepository _repository;

    public QualityOfLifeV11Tests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_V11_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _environment = new AppEnvironment(_testDir);
        _initializer = new SqliteDatabaseInitializer(_environment, NullLogger<SqliteDatabaseInitializer>.Instance);
        _initializer.InitializeAsync().GetAwaiter().GetResult();

        _repository = new SqliteScanRepository(_initializer, NullLogger<SqliteScanRepository>.Instance);
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
                // Best effort
            }
        }
    }

    [Fact]
    public void AppVersion_ReturnsConfiguredVersion()
    {
        // Assert
        Assert.NotNull(AppVersion.Current);
        Assert.Matches(@"^\d+\.\d+\.\d+", AppVersion.Current);
        Assert.NotEqual("1.0.0", AppVersion.Current);
    }

    [Fact]
    public void ConfigurationValidator_ValidatesAiTimeoutAndPayloadBounds()
    {
        var validator = new ConfigurationValidator();

        // Valid
        var validConfig = new AppConfiguration();
        validConfig.AI.TimeoutSeconds = 120;
        validConfig.AI.MaxPayloadCandidates = 75;
        var validResult = validator.Validate(validConfig);
        Assert.True(validResult.IsValid);

        // Invalid timeout
        var invalidTimeout = new AppConfiguration();
        invalidTimeout.AI.TimeoutSeconds = 0;
        var res1 = validator.Validate(invalidTimeout);
        Assert.False(res1.IsValid);
        Assert.Contains(res1.Errors, e => e.Contains("AI.TimeoutSeconds"));

        // Invalid payload limit
        var invalidPayload = new AppConfiguration();
        invalidPayload.AI.MaxPayloadCandidates = 0;
        var res2 = validator.Validate(invalidPayload);
        Assert.False(res2.IsValid);
        Assert.Contains(res2.Errors, e => e.Contains("AI.MaxPayloadCandidates"));
    }

    [Fact]
    public async Task SqliteScanRepository_FindExistingFileHashAsync_ReusesHashFromPriorScan()
    {
        // Arrange
        var scan1 = new ScanRun
        {
            Id = Guid.NewGuid(),
            StartTimeUtc = DateTime.UtcNow.AddHours(-2),
            Status = ScanState.Completed,
            ApplicationVersion = "1.1.0"
        };
        await _repository.CreateScanRunAsync(scan1);

        var fileTime = DateTime.UtcNow.AddDays(-10);
        var knownHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        var file1 = new FileRecord
        {
            ScanRunId = scan1.Id,
            FullPath = @"C:\Data\Project\Document.pdf",
            ParentPath = @"C:\Data\Project",
            FileName = "Document.pdf",
            Extension = ".pdf",
            SizeBytes = 4096,
            CreationTimeUtc = fileTime,
            LastWriteTimeUtc = fileTime,
            Attributes = 32,
            ObservedAtUtc = DateTime.UtcNow.AddHours(-2),
            MetadataStatus = ScanMetadataStatus.Ok,
            HashAlgorithm = "SHA-256",
            HashValue = knownHash
        };
        await _repository.InsertFileBatchAsync(new[] { file1 });

        // Act - lookup in repository for a later scan
        var foundHash = await _repository.FindExistingFileHashAsync(
            @"C:\Data\Project\Document.pdf",
            4096,
            fileTime);

        // Assert
        Assert.Equal(knownHash, foundHash);

        // If size or timestamp changed, it should return null
        var changedSize = await _repository.FindExistingFileHashAsync(
            @"C:\Data\Project\Document.pdf",
            5000,
            fileTime);
        Assert.Null(changedSize);

        var changedTime = await _repository.FindExistingFileHashAsync(
            @"C:\Data\Project\Document.pdf",
            4096,
            fileTime.AddDays(1));
        Assert.Null(changedTime);
    }

    [Fact]
    public async Task SqliteScanRepository_GetFileByIdAsync_ReturnsMatchingFile()
    {
        var scan = new ScanRun
        {
            Id = Guid.NewGuid(),
            StartTimeUtc = DateTime.UtcNow,
            Status = ScanState.Completed,
            ApplicationVersion = "1.1.0"
        };
        await _repository.CreateScanRunAsync(scan);

        var file = new FileRecord
        {
            ScanRunId = scan.Id,
            FullPath = @"C:\Archive\Backup.zip",
            ParentPath = @"C:\Archive",
            FileName = "Backup.zip",
            Extension = ".zip",
            SizeBytes = 1048576,
            CreationTimeUtc = DateTime.UtcNow,
            LastWriteTimeUtc = DateTime.UtcNow,
            Attributes = 32,
            ObservedAtUtc = DateTime.UtcNow,
            MetadataStatus = ScanMetadataStatus.Ok
        };
        await _repository.InsertFileBatchAsync(new[] { file });

        var paged = await _repository.QueryFilesAsync(new FileInventoryQuery { ScanRunId = scan.Id });
        var createdFile = Assert.Single(paged.Items);

        // Act
        var retrieved = await _repository.GetFileByIdAsync(createdFile.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(createdFile.Id, retrieved.Id);
        Assert.Equal(@"C:\Archive\Backup.zip", retrieved.FullPath);
        Assert.Equal(1048576, retrieved.SizeBytes);
    }

    [Fact]
    public async Task AiAnalysisService_ExportAiReviewTableAsync_GeneratesCsvAndMarkdown()
    {
        var scanId = Guid.NewGuid();
        var mockOllama = new MockOllamaClient();
        var cfgService = new InMemoryConfigService();
        var storageEngine = new StorageAnalysisEngine(_initializer, _repository, cfgService, NullLogger<StorageAnalysisEngine>.Instance);

        var aiService = new AiAnalysisService(
            mockOllama,
            storageEngine,
            _repository,
            _environment,
            cfgService,
            NullLogger<AiAnalysisService>.Instance);

        var response = new AiAnalysisResponse
        {
            ExecutiveSummary = "Executive summary of test scan.",
            StorageHotspotsAssessment = "Hotspots identified in Downloads folder.",
            Recommendations = new List<AiRecommendation>
            {
                new()
                {
                    CandidateId = 42,
                    FileName = "large_archive.tar.gz",
                    SuggestedCategory = "Archive candidate",
                    ConfidenceLevel = "High",
                    Evidence = "4.5 GB, inactive for 3 years",
                    Explanation = "Old build artifact that is no longer referenced.",
                    RecommendedVerificationStep = "Check if build was deployed."
                }
            },
            PrioritizedReviewPlan = new List<string> { "Step 1: Check Downloads", "Step 2: Review ISOs" },
            SafetyWarnings = new List<string> { "Verify file hashes before archiving." },
            ModelName = "qwen2.5:7b"
        };

        // Act
        var (csvPath, mdPath) = await aiService.ExportAiReviewTableAsync(scanId, "qwen2.5:7b", response);

        // Assert
        Assert.True(File.Exists(csvPath));
        Assert.True(File.Exists(mdPath));

        var csvLines = await File.ReadAllLinesAsync(csvPath);
        Assert.True(csvLines.Length >= 2);
        Assert.Contains("CandidateId,FileName,SuggestedCategory", csvLines[0]);
        Assert.Contains("42,large_archive.tar.gz,Archive candidate", csvLines[1]);

        var mdContent = await File.ReadAllTextAsync(mdPath);
        Assert.Contains("AI Recommendations Table", mdContent);
        Assert.Contains("large_archive.tar.gz", mdContent);
        Assert.Contains("Prioritized Review Plan", mdContent);
        Assert.Contains("Safety Warnings", mdContent);
    }

    private sealed class MockOllamaClient : IOllamaClient
    {
        public Task<AiConnectionStatus> CheckConnectionAsync(string endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiConnectionStatus { IsAvailable = true });

        public Task<IReadOnlyList<string>> GetAvailableModelsAsync(string endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "qwen2.5:7b" });

        public Task<string> GenerateJsonAsync(string endpoint, string model, string systemPrompt, string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult("{}");
    }

    private sealed class InMemoryConfigService : LocalFileAudit.Core.Interfaces.IConfigurationService
    {
        public AppConfiguration Current { get; private set; } = new();

        public Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Current = configuration;
            return Task.CompletedTask;
        }
    }
}
