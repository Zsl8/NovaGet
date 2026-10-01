using System.IO;
using NovaGet.Core.Diagnostics;
using NovaGet.Core.Paths;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace NovaGet.App.Hosting;

internal static class AppLogging
{
    private const string Template = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Rolling daily log files in %LOCALAPPDATA%\NovaGet\logs (14 days, 10 MB per file). Every line, exceptions
    /// included, has passwords in addresses masked before it is written.
    /// </summary>
    public static ILogger CreateLogger(AppPaths paths) => new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.File(
            new RedactingFormatter(new MessageTemplateTextFormatter(Template)),
            Path.Combine(paths.LogsDir, "novaget-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 10 * 1024 * 1024,
            rollOnFileSizeLimit: true)
        .CreateLogger();

    private sealed class RedactingFormatter(ITextFormatter inner) : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output)
        {
            using var line = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            inner.Format(logEvent, line);
            output.Write(LogRedaction.Redact(line.ToString()));
        }
    }
}
