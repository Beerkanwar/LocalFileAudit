using LocalFileAudit.Core.Models;

namespace LocalFileAudit.Core.Interfaces;

/// <summary>
/// Communicates with the local Ollama inference service over HTTP.
/// Strictly enforces local loopback URLs to guarantee user data never leaves the device.
/// </summary>
public interface IOllamaClient
{
    Task<AiConnectionStatus> CheckConnectionAsync(string endpoint, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetAvailableModelsAsync(string endpoint, CancellationToken cancellationToken = default);
    Task<string> GenerateJsonAsync(string endpoint, string model, string systemPrompt, string prompt, CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates data minimization, prompt synthesis, JSON schema validation,
/// atomic report generation, and crash-safe history recording for AI decision support.
/// </summary>
public interface IAiAnalysisService
{
    string BuildPromptPreview(StorageMetricsSummary summary, bool redactPaths);
    Task<AiAnalysisResponse> RunAnalysisAsync(Guid scanRunId, string model, bool redactPaths, CancellationToken cancellationToken = default);
    Task<string> SaveAiReportAsync(Guid scanRunId, string model, AiAnalysisResponse response, CancellationToken cancellationToken = default);
    Task<(string CsvPath, string MdPath)> ExportAiReviewTableAsync(Guid scanRunId, string model, AiAnalysisResponse response, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiReportRecord>> GetHistoryAsync(Guid? scanRunId = null, CancellationToken cancellationToken = default);
}
