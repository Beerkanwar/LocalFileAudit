using System;
using System.Threading;
using System.Threading.Tasks;
using LocalFileAudit.Core.Models;

namespace LocalFileAudit.Core.Interfaces;

public record DirectoryEstimationResult
{
    public long TotalBytes { get; init; }
    public int FileCount { get; init; }
    public bool IsPartial { get; init; }
}

public interface IArchivePlannerService
{
    Task<ArchivePlan> LoadPlanAsync(CancellationToken cancellationToken = default);
    Task SavePlanAsync(ArchivePlan plan, CancellationToken cancellationToken = default);
    Task<ArchiveScriptGenerationResult> GenerateArchiveScriptsAsync(ArchivePlan plan, string outputDirectory, CancellationToken cancellationToken = default);
    (long TotalBytes, long FreeBytes)? GetDestinationFreeSpace(string destinationPath);
    Task MarkBatchVerifiedAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task<DirectoryEstimationResult> EstimateDirectoryAsync(string directoryPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
