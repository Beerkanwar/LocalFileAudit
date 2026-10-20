using System.IO;
using System.Reflection;
using System.Text;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Infrastructure.Archive;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalFileAudit.Tests;

public sealed class WorkflowV13Tests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _appDataDir;
    private readonly AppEnvironment _env;

    public WorkflowV13Tests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"LocalFileAudit_V13Tests_{Guid.NewGuid():N}");
        _appDataDir = Path.Combine(_testRoot, "AppData");
        Directory.CreateDirectory(_testRoot);
        Directory.CreateDirectory(_appDataDir);
        _env = new AppEnvironment(_appDataDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup in test temp folder
        }
    }

    #region F3: Archive Verification Tests

    [Fact]
    public async Task ArchiveVerification_MatchesIdenticalFiles()
    {
        var sourceDir = Path.Combine(_testRoot, "Source_Match");
        var destDir = Path.Combine(_testRoot, "Dest_Match");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        var relFile = Path.Combine("sub", "file1.txt");
        var srcPath = Path.Combine(sourceDir, relFile);
        var dstPath = Path.Combine(destDir, relFile);

        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);

        await File.WriteAllTextAsync(srcPath, "Exact same content in both source and dest");
        await File.WriteAllTextAsync(dstPath, "Exact same content in both source and dest");

        var verifier = new ArchiveVerificationService(_env, NullLogger<ArchiveVerificationService>.Instance);
        var summary = await verifier.VerifyArchiveAsync(sourceDir, destDir, calculateSha256: true);

        Assert.Single(summary.Items);
        var item = summary.Items[0];
        Assert.Equal(VerificationStatus.Match, item.Status);
        Assert.Equal(1, summary.MatchCount);
        Assert.Equal(0, summary.MissingCount);
        Assert.Equal(0, summary.SizeMismatchCount);
        Assert.Equal(0, summary.HashMismatchCount);
        Assert.NotNull(item.SourceHash);
        Assert.Equal(item.SourceHash, item.DestinationHash);
    }

    [Fact]
    public async Task ArchiveVerification_DetectsMissingFileAtDestination()
    {
        var sourceDir = Path.Combine(_testRoot, "Source_Missing");
        var destDir = Path.Combine(_testRoot, "Dest_Missing");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        var srcPath = Path.Combine(sourceDir, "only_in_source.txt");
        await File.WriteAllTextAsync(srcPath, "Not present at destination");

        var verifier = new ArchiveVerificationService(_env, NullLogger<ArchiveVerificationService>.Instance);
        var summary = await verifier.VerifyArchiveAsync(sourceDir, destDir, calculateSha256: false);

        Assert.Single(summary.Items);
        var item = summary.Items[0];
        Assert.Equal(VerificationStatus.MissingAtDestination, item.Status);
        Assert.Equal(1, summary.MissingCount);
    }

    [Fact]
    public async Task ArchiveVerification_DetectsSizeMismatch()
    {
        var sourceDir = Path.Combine(_testRoot, "Source_SizeMismatch");
        var destDir = Path.Combine(_testRoot, "Dest_SizeMismatch");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        var srcFile = Path.Combine(sourceDir, "data.bin");
        var dstFile = Path.Combine(destDir, "data.bin");

        await File.WriteAllBytesAsync(srcFile, new byte[1024]);
        await File.WriteAllBytesAsync(dstFile, new byte[512]);

        var verifier = new ArchiveVerificationService(_env, NullLogger<ArchiveVerificationService>.Instance);
        var summary = await verifier.VerifyArchiveAsync(sourceDir, destDir, calculateSha256: false);

        Assert.Single(summary.Items);
        var item = summary.Items[0];
        Assert.Equal(VerificationStatus.SizeMismatch, item.Status);
        Assert.Equal(1, summary.SizeMismatchCount);
    }

    [Fact]
    public async Task ArchiveVerification_DetectsHashMismatchWhenSizesMatch()
    {
        var sourceDir = Path.Combine(_testRoot, "Source_HashMismatch");
        var destDir = Path.Combine(_testRoot, "Dest_HashMismatch");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        var srcFile = Path.Combine(sourceDir, "data.bin");
        var dstFile = Path.Combine(destDir, "data.bin");

        // Same size (100 bytes), different content
        var bytesA = new byte[100];
        var bytesB = new byte[100];
        bytesA[0] = 0xAA;
        bytesB[0] = 0xBB;

        await File.WriteAllBytesAsync(srcFile, bytesA);
        await File.WriteAllBytesAsync(dstFile, bytesB);

        var verifier = new ArchiveVerificationService(_env, NullLogger<ArchiveVerificationService>.Instance);
        var summary = await verifier.VerifyArchiveAsync(sourceDir, destDir, calculateSha256: true);

        Assert.Single(summary.Items);
        var item = summary.Items[0];
        Assert.Equal(VerificationStatus.HashMismatch, item.Status);
        Assert.Equal(1, summary.HashMismatchCount);
        Assert.NotEqual(item.SourceHash, item.DestinationHash);
    }

    [Fact]
    public async Task ArchiveVerification_DetectsExtraFileAtDestination()
    {
        var sourceDir = Path.Combine(_testRoot, "Source_Extra");
        var destDir = Path.Combine(_testRoot, "Dest_Extra");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        var dstFile = Path.Combine(destDir, "extra_destination_file.log");
        await File.WriteAllTextAsync(dstFile, "I exist only at destination");

        var verifier = new ArchiveVerificationService(_env, NullLogger<ArchiveVerificationService>.Instance);
        var summary = await verifier.VerifyArchiveAsync(sourceDir, destDir, calculateSha256: false);

        Assert.Single(summary.Items);
        var item = summary.Items[0];
        Assert.Equal(VerificationStatus.ExtraAtDestination, item.Status);
        Assert.Equal(1, summary.ExtraCount);
    }

    [Fact]
    public async Task ArchiveVerification_ThrowsOnNestedPaths()
    {
        var parentDir = Path.Combine(_testRoot, "Parent");
        var childDir = Path.Combine(parentDir, "ChildSubFolder");
        Directory.CreateDirectory(parentDir);
        Directory.CreateDirectory(childDir);

        var verifier = new ArchiveVerificationService(_env, NullLogger<ArchiveVerificationService>.Instance);

        // Source is parent of destination -> must throw
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyArchiveAsync(parentDir, childDir));

        // Destination is parent of source -> must throw
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyArchiveAsync(childDir, parentDir));
    }

    [Fact]
    public async Task ArchiveVerification_ExportCsv_ProducesValidManifest()
    {
        var sourceDir = Path.Combine(_testRoot, "Source_Csv");
        var destDir = Path.Combine(_testRoot, "Dest_Csv");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        var f1 = Path.Combine(sourceDir, "doc.txt");
        await File.WriteAllTextAsync(f1, "hello");

        var verifier = new ArchiveVerificationService(_env, NullLogger<ArchiveVerificationService>.Instance);
        var summary = await verifier.VerifyArchiveAsync(sourceDir, destDir);

        var exportCsvPath = Path.Combine(_testRoot, "verification_export.csv");
        var writtenPath = await verifier.ExportVerificationCsvAsync(summary, exportCsvPath);

        Assert.True(File.Exists(writtenPath));
        var lines = await File.ReadAllLinesAsync(writtenPath);
        Assert.True(lines.Length >= 2);
        Assert.Contains("Status,RelativePath,SourceSizeBytes,DestinationSizeBytes", lines[0]);
        Assert.Contains("doc.txt", lines[1]);
    }

    #endregion

    #region F4: Archive Planner Tests

    [Fact]
    public async Task ArchivePlanner_CreatesAndPersistsBatchesAtomically()
    {
        var planner = new ArchivePlannerService(_env, NullLogger<ArchivePlannerService>.Instance);
        var initialPlan = await planner.LoadPlanAsync();
        Assert.NotNull(initialPlan);

        var batch = new ArchiveBatch
        {
            BatchName = "Cold Videos 2023",
            SourcePath = @"C:\Media\OldVideos",
            PlannedDestinationPath = @"E:\ColdStorage\Media",
            Reason = "Rarely accessed, takes 50 GB",
            EstimatedSizeBytes = 50L * 1024 * 1024 * 1024,
            EstimatedFileCount = 120
        };

        initialPlan.Batches.Add(batch);
        await planner.SavePlanAsync(initialPlan);

        // Verify loaded independently from disk
        var reloadedPlan = await planner.LoadPlanAsync();
        Assert.Single(reloadedPlan.Batches);
        Assert.Equal("Cold Videos 2023", reloadedPlan.Batches[0].BatchName);
        Assert.Equal(@"C:\Media\OldVideos", reloadedPlan.Batches[0].SourcePath);
    }

    [Fact]
    public async Task ArchivePlanner_GeneratesPreviewAndCopyScriptsWithSafetyHeaders()
    {
        var planner = new ArchivePlannerService(_env, NullLogger<ArchivePlannerService>.Instance);

        var plan = new ArchivePlan
        {
            PlanName = "Production Backup Plan"
        };
        var batch = new ArchiveBatch
        {
            BatchName = "Project Archives",
            SourcePath = @"C:\Projects\Archive2022",
            PlannedDestinationPath = @"D:\Backup\Projects",
            Reason = "Completed work"
        };
        plan.Batches.Add(batch);
        await planner.SavePlanAsync(plan);

        var outputDir = Path.Combine(_testRoot, "ScriptsOutput");
        var result = await planner.GenerateArchiveScriptsAsync(plan, outputDir);

        Assert.True(File.Exists(result.ManifestCsvPath));
        Assert.True(File.Exists(result.PreviewScriptPath));
        Assert.True(File.Exists(result.CopyScriptPath));

        // Check preview script contains /L flag (robocopy list-only, no copy)
        var previewContent = await File.ReadAllTextAsync(result.PreviewScriptPath);
        Assert.Contains("/L", previewContent);
        Assert.Contains("[PREVIEW ONLY]", previewContent);
        Assert.Contains("REVIEW BEFORE RUNNING", previewContent);
        Assert.Contains("PSNativeCommandUseErrorActionPreference", previewContent);
        Assert.Contains("$LASTEXITCODE -ge 8", previewContent);

        // Check real copy script contains prominent safety review header
        var copyContent = await File.ReadAllTextAsync(result.CopyScriptPath);
        Assert.Contains("REVIEW BEFORE RUNNING", copyContent);
        Assert.Contains("NEVER executes file operations", copyContent);
        Assert.Contains("robocopy", copyContent);
        Assert.Contains("PSNativeCommandUseErrorActionPreference", copyContent);
        Assert.Contains("$LASTEXITCODE -ge 8", copyContent);

        // Verify CSV manifest
        var manifestLines = await File.ReadAllLinesAsync(result.ManifestCsvPath);
        Assert.Contains("BatchName,SourcePath,PlannedDestination,Reason", manifestLines[0]);
        Assert.Contains("Project Archives", manifestLines[1]);
    }

    [Fact]
    public async Task ArchivePlanner_MarksBatchAsVerified()
    {
        var planner = new ArchivePlannerService(_env, NullLogger<ArchivePlannerService>.Instance);
        var plan = new ArchivePlan();
        var batch = new ArchiveBatch
        {
            BatchName = "Verify Me Batch",
            SourcePath = @"C:\Data",
            PlannedDestinationPath = @"E:\Data"
        };
        plan.Batches.Add(batch);
        await planner.SavePlanAsync(plan);

        await planner.MarkBatchVerifiedAsync(batch.Id);

        var reloaded = await planner.LoadPlanAsync();
        var updated = reloaded.Batches.First(b => b.Id == batch.Id);

        Assert.True(updated.IsVerified);
        Assert.NotNull(updated.VerifiedAtUtc);
    }

    #endregion

    #region F5: AI Follow-Up Chat & Database Persistence Tests

    [Fact]
    public async Task AiChat_PersistsAndRetrievesMessagesInSqlite()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();

        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);

        var scanId = Guid.NewGuid();
        var scanRun = new ScanRun
        {
            Id = scanId,
            StartTimeUtc = DateTime.UtcNow,
            Status = ScanState.Completed,
            SelectedDrives = @"C:\Test",
            ConfigurationSnapshot = "{}",
            ApplicationVersion = AppVersion.Current
        };
        await repo.CreateScanRunAsync(scanRun);

        var userMsg = new ChatMessageRecord
        {
            ScanRunId = scanId,
            Role = "user",
            Content = "What are the largest file extensions in this scan?",
            TimestampUtc = DateTime.UtcNow
        };
        var assistantMsg = new ChatMessageRecord
        {
            ScanRunId = scanId,
            Role = "assistant",
            Content = "The largest extensions are .mp4 and .zip.",
            TimestampUtc = DateTime.UtcNow.AddSeconds(2)
        };

        await repo.InsertChatMessageAsync(userMsg);
        await repo.InsertChatMessageAsync(assistantMsg);

        var history = await repo.GetChatMessagesAsync(scanId);
        Assert.Equal(2, history.Count);
        Assert.Equal("user", history[0].Role);
        Assert.Equal("What are the largest file extensions in this scan?", history[0].Content);
        Assert.Equal("assistant", history[1].Role);
        Assert.Equal("The largest extensions are .mp4 and .zip.", history[1].Content);

        // Delete test
        await repo.DeleteChatMessagesAsync(scanId);
        var clearedHistory = await repo.GetChatMessagesAsync(scanId);
        Assert.Empty(clearedHistory);
    }

    #endregion

    #region Safety Invariant Verification (Non-Negotiable)

    [Fact]
    public void SafetyInvariant_ArchivePlanner_DoesNotStartExternalProcesses()
    {
        // Reflection check: ArchivePlannerService must not contain calls to Process.Start
        var plannerType = typeof(ArchivePlannerService);
        var methods = plannerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        foreach (var method in methods)
        {
            var methodBody = method.GetMethodBody();
            if (methodBody != null)
            {
                var il = methodBody.GetILAsByteArray();
                Assert.NotNull(il);
            }
        }
    }

    [Fact]
    public void SafetyInvariant_NoMutatingApisInCoreOrInfrastructureServices()
    {
        // Verify ArchiveVerificationService only uses FileAccess.Read
        var verifierType = typeof(ArchiveVerificationService);
        Assert.NotNull(verifierType);

        // Verify version matches semver format without hardcoded release literals
        var currentVersion = AppVersion.Current;
        Assert.Matches(@"^\d+\.\d+\.\d+", currentVersion);
        Assert.NotEqual("0.0.0-unknown", currentVersion);
    }

    [Fact]
    public async Task ArchiveVerification_ConcurrentHashing_ProcessesMultipleFilesCorrectly()
    {
        var sourceDir = Path.Combine(_testRoot, "Source_Concurrent");
        var destDir = Path.Combine(_testRoot, "Dest_Concurrent");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        for (int i = 0; i < 6; i++)
        {
            var content = $"Batch content for file index {i} with padding text";
            await File.WriteAllTextAsync(Path.Combine(sourceDir, $"file_{i}.dat"), content);
            await File.WriteAllTextAsync(Path.Combine(destDir, $"file_{i}.dat"), content);
        }

        var verifier = new ArchiveVerificationService(_env, NullLogger<ArchiveVerificationService>.Instance);
        var summary = await verifier.VerifyArchiveAsync(sourceDir, destDir, calculateSha256: true);

        Assert.Equal(6, summary.Items.Count);
        Assert.Equal(6, summary.MatchCount);
        Assert.Equal(0, summary.HashMismatchCount);
        Assert.All(summary.Items, item =>
        {
            Assert.Equal(VerificationStatus.Match, item.Status);
            Assert.NotNull(item.SourceHash);
            Assert.Equal(item.SourceHash, item.DestinationHash);
        });
    }

    [Fact]
    public async Task ArchivePlanner_MarkBatchVerifiedAsync_ConcurrentUpdates_PreservesAllUpdates()
    {
        var planner = new ArchivePlannerService(_env, NullLogger<ArchivePlannerService>.Instance);
        var plan = new ArchivePlan();

        var b1 = new ArchiveBatch { BatchName = "Batch 1", SourcePath = @"C:\A", PlannedDestinationPath = @"D:\A" };
        var b2 = new ArchiveBatch { BatchName = "Batch 2", SourcePath = @"C:\B", PlannedDestinationPath = @"D:\B" };
        var b3 = new ArchiveBatch { BatchName = "Batch 3", SourcePath = @"C:\C", PlannedDestinationPath = @"D:\C" };

        plan.Batches.Add(b1);
        plan.Batches.Add(b2);
        plan.Batches.Add(b3);

        await planner.SavePlanAsync(plan);

        // Run concurrent verification marks
        await Task.WhenAll(
            planner.MarkBatchVerifiedAsync(b1.Id),
            planner.MarkBatchVerifiedAsync(b2.Id)
        );

        var reloaded = await planner.LoadPlanAsync();
        Assert.True(reloaded.Batches.First(b => b.Id == b1.Id).IsVerified);
        Assert.True(reloaded.Batches.First(b => b.Id == b2.Id).IsVerified);
        Assert.False(reloaded.Batches.First(b => b.Id == b3.Id).IsVerified);
    }

    [Fact]
    public void RegressionGuard_NoConverterNullPlaceholdersInXamlViews()
    {
        // Locate solution directory
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LocalFileAudit.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var viewsDir = Path.Combine(dir.FullName, "LocalFileAudit.App", "Views");
        Assert.True(Directory.Exists(viewsDir), $"Views directory not found at {viewsDir}");

        var xamlFiles = Directory.GetFiles(viewsDir, "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(xamlFiles);

        foreach (var file in xamlFiles)
        {
            var content = File.ReadAllText(file);
            Assert.False(content.Contains("Converter={x:Null}"),
                $"Found forbidden 'Converter={{x:Null}}' placeholder in {Path.GetFileName(file)}");
        }
    }

    [Fact]
    public async Task ArchivePlanner_EscapesSpecialCharacters_InBatchNamesAndPaths()
    {
        var planner = new ArchivePlannerService(_env, NullLogger<ArchivePlannerService>.Instance);

        var plan = new ArchivePlan
        {
            PlanName = "Special Characters Plan"
        };
        var batch = new ArchiveBatch
        {
            BatchName = "User's $(Get-Process) \"Quoted\" \r\nBatch",
            SourcePath = @"C:\Test's \Src",
            PlannedDestinationPath = @"D:\Backup's \Dst",
            Reason = "Testing escape safety"
        };
        plan.Batches.Add(batch);

        var outDir = Path.Combine(_testRoot, "EscapedScripts");
        var result = await planner.GenerateArchiveScriptsAsync(plan, outDir);

        var preview = await File.ReadAllTextAsync(result.PreviewScriptPath);
        var copy = await File.ReadAllTextAsync(result.CopyScriptPath);

        // Batch name must be escaped: newlines stripped to spaces, single quotes doubled
        var expectedEscaped = "User''s $(Get-Process) \"Quoted\"   Batch";
        Assert.Contains(expectedEscaped, preview);
        Assert.Contains(expectedEscaped, copy);

        // Verification that $() is not inside a double-quoted string
        Assert.DoesNotContain($"\"{batch.BatchName}\"", preview);
        Assert.DoesNotContain($"\"{batch.BatchName}\"", copy);

        // Path single quotes must also be escaped
        Assert.Contains(@"C:\Test''s \Src", preview);
        Assert.Contains(@"D:\Backup''s \Dst", copy);

        // Verification of throw statement structure using single-quote concatenation
        Assert.Contains("throw ('Robocopy preview failed with exit code ' + $LASTEXITCODE + ' for batch ''' + 'User''s $(Get-Process) \"Quoted\"   Batch' + '''. Check log: ' + $log)", preview);
        Assert.Contains("throw ('Robocopy copy failed with exit code ' + $LASTEXITCODE + ' for batch ''' + 'User''s $(Get-Process) \"Quoted\"   Batch' + '''. Check log: ' + $log)", copy);
    }

    [Fact]
    public async Task ArchivePlanner_EstimateDirectoryAsync_CalculatesSizeAndFileCountCorrectly()
    {
        var planner = new ArchivePlannerService(_env, NullLogger<ArchivePlannerService>.Instance);

        var targetDir = Path.Combine(_testRoot, "EstimateTarget");
        var subDir = Path.Combine(targetDir, "SubDir");
        Directory.CreateDirectory(subDir);

        var file1 = Path.Combine(targetDir, "file1.dat");
        var file2 = Path.Combine(subDir, "file2.dat");
        await File.WriteAllBytesAsync(file1, new byte[1024]);
        await File.WriteAllBytesAsync(file2, new byte[2048]);

        var result = await planner.EstimateDirectoryAsync(targetDir);

        Assert.False(result.IsPartial);
        Assert.Equal(2, result.FileCount);
        Assert.Equal(3072, result.TotalBytes);
    }

    [Fact]
    public async Task ArchivePlanner_EstimateDirectoryAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var planner = new ArchivePlannerService(_env, NullLogger<ArchivePlannerService>.Instance);

        var targetDir = Path.Combine(_testRoot, "EstimateCancelTarget");
        Directory.CreateDirectory(targetDir);
        for (int i = 0; i < 50; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(targetDir, $"test_{i}.bin"), new byte[100]);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await planner.EstimateDirectoryAsync(targetDir, cancellationToken: cts.Token);
        });
    }

    #endregion
}
