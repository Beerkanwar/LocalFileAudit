using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Infrastructure.Logging;
using LocalFileAudit.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalFileAudit.Tests;

public class AuditV1312RemediationTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestAppEnvironment _env;

    public AuditV1312RemediationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_V1312Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _env = new TestAppEnvironment(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void Sec01_PathRedactor_RedactFileName_MasksPersonalBareFileName()
    {
        var redacted = PathRedactor.RedactFileName("Confidential_Taxes_JohnDoe.pdf", 42);
        Assert.Equal("candidate_42.pdf", redacted);
    }

    [Fact]
    public async Task Sec02_ComparisonCsvExport_NeutralizesFormulaInjection()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();
        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);

        var comparison = new RunComparisonResult
        {
            BaselineRunId = Guid.NewGuid(),
            TargetRunId = Guid.NewGuid(),
            BaselineTimeUtc = DateTime.UtcNow,
            TargetTimeUtc = DateTime.UtcNow,
            Deltas = new List<FileDeltaRecord>
            {
                new FileDeltaRecord
                {
                    DeltaType = DeltaType.New,
                    FullPath = "=cmd|' /C calc'!A0.txt",
                    Extension = ".txt",
                    BaselineSizeBytes = 0,
                    TargetSizeBytes = 100,
                    TargetModifiedUtc = DateTime.UtcNow
                }
            }
        };

        var exportPath = Path.Combine(_testDir, "test-diff.csv");
        await repo.ExportComparisonCsvAsync(comparison, exportPath);

        var content = await File.ReadAllTextAsync(exportPath);
        Assert.Contains(",'=cmd", content);
        Assert.DoesNotContain(",=cmd", content);
    }

    [Fact]
    public void Bug01_SelectedRootsHelper_CorrectlyParsesRootsWithCommas()
    {
        var rootsStr = @"C:\Folder, 2026|D:\Archive|E:\";
        var parsed = SelectedRootsHelper.ParseRoots(rootsStr);
        Assert.Equal(3, parsed.Count);
        Assert.Equal(@"C:\Folder, 2026", parsed[0]);
        Assert.Equal(@"D:\Archive", parsed[1]);
        Assert.Equal(@"E:\", parsed[2]);
    }

    [Fact]
    public void Arch01_ScanningConfiguration_ExcludedPaths_InitializesAndStoresCorrectly()
    {
        var config = new ScanningConfiguration();
        Assert.NotNull(config.ExcludedPaths);
        Assert.Empty(config.ExcludedPaths);

        config.ExcludedPaths.Add(@"C:\Projects\LocalFileAudit\node_modules");
        config.ExcludedPaths.Add(@".git");

        Assert.Equal(2, config.ExcludedPaths.Count);
    }

    private sealed class TestAppEnvironment : IAppEnvironment
    {
        public string ApplicationDataDirectory { get; }
        public string DatabaseDirectory => Path.Combine(ApplicationDataDirectory, "database");
        public string LogsDirectory => Path.Combine(ApplicationDataDirectory, "logs");
        public string ReportsDirectory => Path.Combine(ApplicationDataDirectory, "reports");
        public string BackupsDirectory => Path.Combine(ApplicationDataDirectory, "backups");
        public string ConfigFilePath => Path.Combine(ApplicationDataDirectory, "config.json");

        public TestAppEnvironment(string root)
        {
            ApplicationDataDirectory = root;
        }

        public void EnsureDirectoriesCreated()
        {
            Directory.CreateDirectory(ApplicationDataDirectory);
            Directory.CreateDirectory(DatabaseDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(ReportsDirectory);
            Directory.CreateDirectory(BackupsDirectory);
        }
    }
}
