using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Validation;

namespace LocalFileAudit.Tests;

public class ConfigurationValidationTests
{
    private readonly ConfigurationValidator _validator = new();

    [Fact]
    public void Validate_DefaultConfiguration_ReturnsSuccess()
    {
        var config = new AppConfiguration();
        var result = _validator.Validate(config);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Validate_WhenDestructiveOperationsEnabled_ReturnsFailure(bool delete, bool move, bool copy)
    {
        var config = new AppConfiguration();
        config.Safety.AutomaticDeletionEnabled = delete;
        config.Safety.AutomaticMoveEnabled = move;
        config.Safety.AutomaticCopyEnabled = copy;

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.StartsWith("Safety violation:"));
    }

    [Fact]
    public void Validate_WhenNonLocalAiEndpoint_AndRequireLocal_ReturnsFailure()
    {
        var config = new AppConfiguration();
        config.AI.Endpoint = "https://external-api.com/v1";
        config.AI.RequireLocalEndpoint = true;

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("loopback"));
    }

    [Fact]
    public void Validate_WhenNonLocalAiEndpoint_AndRequireLocalFalse_ReturnsSuccess()
    {
        var config = new AppConfiguration();
        config.AI.Endpoint = "https://example.com";
        config.AI.RequireLocalEndpoint = false;

        var result = _validator.Validate(config);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(32)]
    public void Validate_WhenWorkerCountOutOfRange_ReturnsFailure(int workers)
    {
        var config = new AppConfiguration();
        config.Scanning.MaxConcurrentDirectoryWorkers = workers;

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(100000)]
    public void Validate_WhenDatabaseBatchSizeOutOfRange_ReturnsFailure(int batchSize)
    {
        var config = new AppConfiguration();
        config.Scanning.DatabaseBatchSize = batchSize;

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
    }
}
