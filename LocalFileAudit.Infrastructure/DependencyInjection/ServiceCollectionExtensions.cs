using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
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

        return services;
    }
}
