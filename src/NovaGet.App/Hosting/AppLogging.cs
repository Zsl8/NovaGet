using System.IO;
using NovaGet.Core.Paths;
using Serilog;
using Serilog.Events;

namespace NovaGet.App.Hosting;

internal static class AppLogging
{
    /// <summary>Rolling daily log files in %LOCALAPPDATA%\NovaGet\logs (14 days, 10 MB per file).</summary>
    public static ILogger CreateLogger(AppPaths paths) => new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.File(
            Path.Combine(paths.LogsDir, "novaget-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 10 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .CreateLogger();
}
