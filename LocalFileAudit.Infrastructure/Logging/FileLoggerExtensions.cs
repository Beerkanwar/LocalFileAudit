using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Logging;

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddLocalFileLogger(this ILoggingBuilder builder, Action<FileLoggerOptions>? configure = null)
    {
        var options = new FileLoggerOptions();
        configure?.Invoke(options);

        var provider = new FileLoggerProvider(options);
        builder.Services.AddSingleton<ILoggerProvider>(provider);
        builder.Services.AddSingleton(provider);

        return builder;
    }
}
