using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.DependencyInjection;
using LocalFileAudit.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Tests;

public class DependencyInjectionTests : IDisposable
{
    private readonly string _tempDir;

    public DependencyInjectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_DITest_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void ServiceCollection_ResolvesInfrastructureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            logging.AddLocalFileLogger(opts =>
            {
                opts.LogDirectory = Path.Combine(_tempDir, "logs");
            });
        });

        services.AddInfrastructureServices(_tempDir);

        using var provider = services.BuildServiceProvider();

        var env = provider.GetService<IAppEnvironment>();
        Assert.NotNull(env);
        Assert.Equal(_tempDir, env.ApplicationDataDirectory);

        var validator = provider.GetService<IConfigurationValidator>();
        Assert.NotNull(validator);

        var configService = provider.GetService<IConfigurationService>();
        Assert.NotNull(configService);

        var logger = provider.GetService<ILogger<DependencyInjectionTests>>();
        Assert.NotNull(logger);

        var fileLoggerProvider = provider.GetService<FileLoggerProvider>();
        Assert.NotNull(fileLoggerProvider);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort
        }
    }
}
