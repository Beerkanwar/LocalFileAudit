using System.Collections.Concurrent;
using System.Threading.Channels;
using LocalFileAudit.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Logging;

[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _channel;
    private readonly Task _writerTask;
    private bool _disposed;

    public FileLoggerOptions Options { get; }

    public FileLoggerProvider(FileLoggerOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(Options.LogDirectory))
        {
            Options.LogDirectory = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "LocalFileAudit",
                "logs");
        }

        Directory.CreateDirectory(Options.LogDirectory);
        CleanOldLogs();

        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
        _writerTask = Task.Run(ProcessLogQueueAsync);
    }

    public void ApplyConfiguration(LoggingConfiguration? config)
    {
        if (config == null) return;
        Options.RedactFilePaths = config.RedactFilePaths;
        Options.RetainDays = config.RetainDays;
        if (Enum.TryParse<LogLevel>(config.LogLevel, ignoreCase: true, out var level))
        {
            Options.MinLevel = level;
        }
        CleanOldLogs();
    }

    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));
    }

    private const long MaxLogFileSizeBytes = 10 * 1024 * 1024; // 10 MB bound

    internal void WriteEntry(string entry)
    {
        if (_disposed)
        {
            return;
        }

        _channel.Writer.TryWrite(entry);
    }

    private async Task ProcessLogQueueAsync()
    {
        var buffer = new List<string>(100);
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var entry))
                {
                    buffer.Add(entry);
                    if (buffer.Count >= 200)
                    {
                        FlushLogBuffer(buffer);
                    }
                }

                if (buffer.Count > 0)
                {
                    FlushLogBuffer(buffer);
                }
            }
        }
        catch
        {
            // Consumer cancellation or termination
        }
        finally
        {
            if (buffer.Count > 0)
            {
                FlushLogBuffer(buffer);
            }
        }
    }

    private void FlushLogBuffer(List<string> buffer)
    {
        try
        {
            var filePath = GetCurrentLogFilePath();
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Exists && fileInfo.Length > MaxLogFileSizeBytes)
            {
                var rolledPath = Path.Combine(
                    Options.LogDirectory!,
                    $"{Options.LogFileNamePrefix}-{DateTime.UtcNow:yyyyMMdd}-{DateTime.UtcNow:HHmmss}.log");
                try
                {
                    File.Move(filePath, rolledPath, overwrite: true);
                }
                catch
                {
                    // Fallback if move fails
                }
            }

            var text = string.Join(System.Environment.NewLine, buffer) + System.Environment.NewLine;
            File.AppendAllText(filePath, text);
        }
        catch
        {
            // File logging should never crash the host application
        }
        finally
        {
            buffer.Clear();
        }
    }

    public string GetCurrentLogFilePath()
    {
        var fileName = $"{Options.LogFileNamePrefix}-{DateTime.UtcNow:yyyyMMdd}.log";
        return Path.Combine(Options.LogDirectory!, fileName);
    }

    private void CleanOldLogs()
    {
        if (Options.RetainDays <= 0 || !Directory.Exists(Options.LogDirectory))
        {
            return;
        }

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-Options.RetainDays);
            var pattern = $"{Options.LogFileNamePrefix}-*.log";
            var files = Directory.GetFiles(Options.LogDirectory, pattern);
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                if (info.CreationTimeUtc < cutoff && info.LastWriteTimeUtc < cutoff)
                {
                    try
                    {
                        info.Delete();
                    }
                    catch
                    {
                        // Best effort cleanup only
                    }
                }
            }
        }
        catch
        {
            // Ignore directory query errors during cleanup
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
        _loggers.Clear();
    }
}
