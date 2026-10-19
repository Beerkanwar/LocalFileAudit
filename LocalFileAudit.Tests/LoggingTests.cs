using LocalFileAudit.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Tests;

public class LoggingTests : IDisposable
{
    private readonly string _tempLogDir;

    public LoggingTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_LogTest_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void PathRedactor_RedactsUserProfile()
    {
        var sample = @"Error reading file at C:\Users\JohnDoe\Documents\secret.txt";
        var redacted = PathRedactor.Redact(sample);

        Assert.DoesNotContain("JohnDoe", redacted);
        Assert.Contains(@"C:\Users\[USER]\Documents\secret.txt", redacted);
    }

    [Fact]
    public void PathRedactor_LeavesNonProfilePathsAlone()
    {
        var sample = @"Scanning D:\Data\Projects\Sample.cs";
        var redacted = PathRedactor.Redact(sample);

        Assert.Equal(sample, redacted);
    }

    [Fact]
    public void FileLogger_WritesFormattedEntryToFile()
    {
        var options = new FileLoggerOptions
        {
            LogDirectory = _tempLogDir,
            LogFileNamePrefix = "testlog",
            MinLevel = LogLevel.Information,
            RedactFilePaths = true
        };

        using (var provider = new FileLoggerProvider(options))
        {
            var logger = provider.CreateLogger("TestCategory");
            logger.LogInformation("This is a test message from user path C:\\Users\\Alice\\test.txt");
        }

        var logFile = Directory.GetFiles(_tempLogDir, "testlog-*.log").FirstOrDefault();
        Assert.NotNull(logFile);

        var content = File.ReadAllText(logFile);
        Assert.Contains("[INFO] [TestCategory] This is a test message from user path C:\\Users\\[USER]\\test.txt", content);
        Assert.DoesNotContain("Alice", content);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempLogDir))
            {
                Directory.Delete(_tempLogDir, recursive: true);
            }
        }
        catch
        {
            // Best effort
        }
    }
}
