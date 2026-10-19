using LocalFileAudit.Infrastructure.Environment;

namespace LocalFileAudit.Tests;

public class AppEnvironmentTests : IDisposable
{
    private readonly string _tempDir;

    public AppEnvironmentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_Test_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void Paths_AreUnderRootDirectory()
    {
        var env = new AppEnvironment(_tempDir);

        Assert.Equal(_tempDir, env.ApplicationDataDirectory);
        Assert.Equal(Path.Combine(_tempDir, "config.json"), env.ConfigFilePath);
        Assert.Equal(Path.Combine(_tempDir, "logs"), env.LogsDirectory);
        Assert.Equal(Path.Combine(_tempDir, "data"), env.DatabaseDirectory);
        Assert.Equal(Path.Combine(_tempDir, "reports"), env.ReportsDirectory);
        Assert.Equal(Path.Combine(_tempDir, "backups"), env.BackupsDirectory);
    }

    [Fact]
    public void EnsureDirectoriesCreated_CreatesAllFolders()
    {
        var env = new AppEnvironment(_tempDir);
        env.EnsureDirectoriesCreated();

        Assert.True(Directory.Exists(env.ApplicationDataDirectory));
        Assert.True(Directory.Exists(env.LogsDirectory));
        Assert.True(Directory.Exists(env.DatabaseDirectory));
        Assert.True(Directory.Exists(env.ReportsDirectory));
        Assert.True(Directory.Exists(env.BackupsDirectory));
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
