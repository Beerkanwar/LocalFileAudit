using System.Text;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Logging;

internal sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string categoryName, FileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider.Options.MinLevel && logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null)
        {
            return;
        }

        if (_provider.Options.RedactFilePaths)
        {
            message = PathRedactor.Redact(message);
        }

        var sb = new StringBuilder();
        sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'"));
        sb.Append(" [");
        sb.Append(GetLogLevelString(logLevel));
        sb.Append("] [");
        sb.Append(_categoryName);
        sb.Append("] ");
        sb.Append(message);

        if (exception != null)
        {
            sb.AppendLine();
            var exText = exception.ToString();
            if (_provider.Options.RedactFilePaths)
            {
                exText = PathRedactor.Redact(exText);
            }
            sb.Append(exText);
        }

        _provider.WriteEntry(sb.ToString());
    }

    private static string GetLogLevelString(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "TRCE",
        LogLevel.Debug => "DBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        LogLevel.Critical => "CRIT",
        _ => "NONE"
    };
}
