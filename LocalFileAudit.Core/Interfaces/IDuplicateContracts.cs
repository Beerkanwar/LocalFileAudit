using LocalFileAudit.Core.Models;

namespace LocalFileAudit.Core.Interfaces;

/// <summary>
/// Two-stage duplicate file analysis engine.
/// Stage 1: Group files by exact byte size.
/// Stage 2: Asynchronously verify matching content using SHA-256 cryptographic hashes.
/// Permanently read-only: Never deletes, moves, or alters any files.
/// </summary>
public interface IDuplicateAnalysisEngine
{
    Task<DuplicateAnalysisSummary> FindPotentialDuplicatesBySizeAsync(
        Guid scanRunId,
        long minSizeBytes = 1024,
        CancellationToken cancellationToken = default);

    Task<DuplicateAnalysisSummary> ComputeHashesAndVerifyDuplicatesAsync(
        Guid scanRunId,
        long minSizeBytes = 1024,
        IProgress<HashJobProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
