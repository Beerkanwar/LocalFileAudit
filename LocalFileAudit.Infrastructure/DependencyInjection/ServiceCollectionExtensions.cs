using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.Analysis;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using LocalFileAudit.Infrastructure.Reporting;
using LocalFileAudit.Infrastructure.Scanning;
using Microsoft.Extensions.DependencyInjection;

namespace LocalFileAudit.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, string? appDataRootOverride = null)
    {
        services.AddSingleton<IAppEnvironment>(_ => new AppEnvironment(appDataRootOverride));
        services.AddSingleton<IConfigurationValidator, ConfigurationValidator>();
        services.AddSingleton<IConfigurationService, JsonConfigurationService>();

        // Persistence & Scanner
        services.AddSingleton<IDatabaseInitializer, SqliteDatabaseInitializer>();
        services.AddSingleton<IScanRepository, SqliteScanRepository>();
        services.AddSingleton<IScanEngine, FilesystemScanner>();

        // Storage Analysis & Reporting
        services.AddSingleton<IStorageAnalysisEngine, StorageAnalysisEngine>();
        services.AddSingleton<IReportGenerator, ReportGenerator>();

        // Local Ollama AI
        services.AddHttpClient();
        services.AddHttpClient<IOllamaClient, LocalFileAudit.Infrastructure.AI.OllamaClient>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        });
        services.AddSingleton<IAiAnalysisService, LocalFileAudit.Infrastructure.AI.AiAnalysisService>();

        // Duplicate Detection
        services.AddSingleton<IDuplicateAnalysisEngine, DuplicateAnalysisEngine>();

        // Archive Planning & Verification (Safety Invariant: Strictly Read-Only)
        services.AddSingleton<IArchiveVerificationService, LocalFileAudit.Infrastructure.Archive.ArchiveVerificationService>();
        services.AddSingleton<IArchivePlannerService, LocalFileAudit.Infrastructure.Archive.ArchivePlannerService>();

        return services;
    }
}
