using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalFileAudit.Tests;

public class JsonConfigurationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppEnvironment _environment;
    private readonly ConfigurationValidator _validator;
    private readonly JsonConfigurationService _service;

    public JsonConfigurationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_CfgTest_" + Guid.NewGuid().ToString("N"));
        _environment = new AppEnvironment(_tempDir);
        _validator = new ConfigurationValidator();
        _service = new JsonConfigurationService(_environment, _validator, NullLogger<JsonConfigurationService>.Instance);
    }

    [Fact]
    public async Task LoadAsync_WhenFileMissing_CreatesAndSavesDefaults()
    {
        var config = await _service.LoadAsync();

        Assert.NotNull(config);
        Assert.True(config.Scanning.IncludeFixedDrivesByDefault);
        Assert.False(config.Safety.AutomaticDeletionEnabled);
        Assert.True(File.Exists(_environment.ConfigFilePath));
    }

    [Fact]
    public async Task SaveAsync_WhenInvalid_ThrowsInvalidOperationException()
    {
        var config = new AppConfiguration();
        config.Safety.AutomaticDeletionEnabled = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveAsync(config));
    }

    [Fact]
    public async Task SaveAsync_AndLoadAsync_RoundtripsSuccessfully()
    {
        var config = new AppConfiguration();
        config.Scanning.MaxConcurrentDirectoryWorkers = 4;
        config.Analysis.LargeFileThresholdBytes = 100_000_000;

        await _service.SaveAsync(config);

        var loaded = await _service.LoadAsync();
        Assert.Equal(4, loaded.Scanning.MaxConcurrentDirectoryWorkers);
        Assert.Equal(100_000_000, loaded.Analysis.LargeFileThresholdBytes);
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
            // Best effort cleanup in test temp folder
        }
    }
}
