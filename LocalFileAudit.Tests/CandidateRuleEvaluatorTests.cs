using LocalFileAudit.Core.Analysis;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Models;

namespace LocalFileAudit.Tests;

public class CandidateRuleEvaluatorTests
{
    private readonly AppConfiguration _config = new();

    [Fact]
    public void Classify_ProtectedSystemPath_ReturnsProtectedCategory()
    {
        var file = new FileRecord
        {
            Id = 1,
            FullPath = @"C:\Windows\System32\drivers\etc\hosts",
            FileName = "hosts",
            Extension = "",
            SizeBytes = 824,
            LastWriteTimeUtc = DateTime.UtcNow.AddDays(-1000)
        };

        var result = CandidateRuleEvaluator.Classify(file, _config);

        Assert.Equal(CandidateCategory.ProtectedSystem, result.Category);
        Assert.Equal("RULE_PROTECTED_SYSTEM", result.RuleId);
    }

    [Fact]
    public void Classify_LargeFile_ReturnsInvestigateWithLargeFileRule()
    {
        var file = new FileRecord
        {
            Id = 2,
            FullPath = @"D:\Work\huge_database.mdf",
            FileName = "huge_database.mdf",
            Extension = ".mdf",
            SizeBytes = 1_000_000_000, // 1 GB (threshold is 500 MB)
            LastWriteTimeUtc = DateTime.UtcNow.AddDays(-10)
        };

        var result = CandidateRuleEvaluator.Classify(file, _config);

        Assert.Equal(CandidateCategory.Investigate, result.Category);
        Assert.Equal("RULE_LARGE_FILE", result.RuleId);
        Assert.Contains("exceeding threshold", result.Explanation);
    }

    [Fact]
    public void Classify_OldMediaFile_ReturnsArchiveCandidate()
    {
        var file = new FileRecord
        {
            Id = 3,
            FullPath = @"D:\Photos\vacation_2022.zip",
            FileName = "vacation_2022.zip",
            Extension = ".zip",
            SizeBytes = 400_000_000,
            LastWriteTimeUtc = DateTime.UtcNow.AddDays(-800) // > 730 days
        };

        var result = CandidateRuleEvaluator.Classify(file, _config);

        Assert.Equal(CandidateCategory.ArchiveCandidate, result.Category);
        Assert.Equal("RULE_OLD_ARCHIVE_MEDIA", result.RuleId);
        Assert.Contains("Archive/media file inactive", result.Explanation);
    }

    [Fact]
    public void Classify_InactiveBackupFile_ReturnsCleanupCandidate()
    {
        var file = new FileRecord
        {
            Id = 4,
            FullPath = @"D:\Projects\app.bak",
            FileName = "app.bak",
            Extension = ".bak",
            SizeBytes = 50_000_000,
            LastWriteTimeUtc = DateTime.UtcNow.AddDays(-200) // > 180 days
        };

        var result = CandidateRuleEvaluator.Classify(file, _config);

        Assert.Equal(CandidateCategory.CleanupCandidate, result.Category);
        Assert.Equal("RULE_INACTIVE_BACKUP_TEMP", result.RuleId);
        Assert.Contains("Never delete automatically", result.Explanation);
    }

    [Fact]
    public void Classify_OldInstallerInDownloads_ReturnsCleanupCandidate()
    {
        var file = new FileRecord
        {
            Id = 5,
            FullPath = @"C:\Users\Someone\Downloads\setup.msi",
            FileName = "setup.msi",
            Extension = ".msi",
            SizeBytes = 80_000_000,
            LastWriteTimeUtc = DateTime.UtcNow.AddDays(-100) // > 90 days
        };

        var result = CandidateRuleEvaluator.Classify(file, _config);

        Assert.Equal(CandidateCategory.CleanupCandidate, result.Category);
        Assert.Equal("RULE_OLD_DOWNLOADED_INSTALLER", result.RuleId);
    }

    [Fact]
    public void Classify_NormalActiveFile_ReturnsKeepOnLaptop()
    {
        var file = new FileRecord
        {
            Id = 6,
            FullPath = @"D:\Projects\Source\Code.cs",
            FileName = "Code.cs",
            Extension = ".cs",
            SizeBytes = 12_000,
            LastWriteTimeUtc = DateTime.UtcNow.AddDays(-2)
        };

        var result = CandidateRuleEvaluator.Classify(file, _config);

        Assert.Equal(CandidateCategory.KeepOnLaptop, result.Category);
        Assert.Equal("RULE_DEFAULT_KEEP", result.RuleId);
    }
}
