using NovaGet.Core.Paths;
using NovaGet.Core.Settings;

namespace NovaGet.Core.Engine;

/// <summary>Engine tuning, normally derived from settings each time a download starts.</summary>
public sealed record EngineOptions
{
    public required string TempDirectory { get; init; }

    /// <summary>Default maximum connections per download (1, 2, 4, 8, 16, 24 or 32).</summary>
    public int MaxConnections { get; init; } = 8;

    /// <summary>Segments are never split into pieces smaller than this.</summary>
    public long MinSegmentSize { get; init; } = 64 * 1024;

    public int WriteBufferSize { get; init; } = 256 * 1024;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive failures without any progress before the download stops with an error.</summary>
    public int MaxRetries { get; init; } = 20;

    /// <summary>Backoff: base, 2×base, 4×base … capped at <see cref="RetryMaxDelay"/>.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the segment map is flushed to disk and saved.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan SpeedSampleInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public string UserAgent { get; init; } = RequestContext.DefaultUserAgent;

    public bool PreallocateDiskSpace { get; init; } = true;

    public bool KeepTempFilesAfterCancel { get; init; }

    public bool KeepServerFileDate { get; init; }

    /// <summary>Options → Connection exceptions: host pattern → max connections.</summary>
    public IReadOnlyDictionary<string, int> ServerConnectionLimits { get; init; } = new Dictionary<string, int>();

    /// <summary>"Remember speed limit for this server": host pattern → KB/s.</summary>
    public IReadOnlyDictionary<string, int> HostSpeedLimitsKBps { get; init; } = new Dictionary<string, int>();

    /// <summary>Extra free space required beyond the bytes still to download.</summary>
    public long FreeSpaceMargin { get; init; } = 16L * 1024 * 1024;

    /// <summary>Write the Zone.Identifier stream (Mark of the Web) on finished files.</summary>
    public bool MarkOfTheWeb { get; init; }

    /// <summary>Virus scanner run on every finished file (null = none).</summary>
    public string? VirusScanProgram { get; init; }

    /// <summary>Its arguments; <c>[file]</c> is replaced with the file's path.</summary>
    public string? VirusScanArguments { get; init; }

    public TimeSpan VirusScanTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Backoff delay before retry number <paramref name="attempt"/> (1-based).</summary>
    public TimeSpan RetryDelay(int attempt)
    {
        var factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(RetryBaseDelay.Ticks * factor, RetryMaxDelay.Ticks));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    public static EngineOptions FromSettings(AppSettings settings, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        var temp = string.IsNullOrWhiteSpace(settings.SaveTo.TempDirectory) ? paths.DefaultTempDir : settings.SaveTo.TempDirectory;
        return new EngineOptions
        {
            TempDirectory = Environment.ExpandEnvironmentVariables(temp),
            MaxConnections = settings.Connection.DefaultMaxConnections,
            MinSegmentSize = settings.Advanced.MinSegmentSizeKB * 1024L,
            WriteBufferSize = settings.Advanced.WriteBufferSizeKB * 1024,
            Timeout = TimeSpan.FromSeconds(settings.Connection.TimeoutSeconds),
            MaxRetries = settings.Connection.MaxRetries,
            RetryBaseDelay = TimeSpan.FromSeconds(settings.Connection.RetryDelaySeconds),
            UserAgent = settings.Downloads.UseCustomUserAgent && !string.IsNullOrWhiteSpace(settings.Downloads.CustomUserAgent)
                ? settings.Downloads.CustomUserAgent
                : RequestContext.DefaultUserAgent,
            PreallocateDiskSpace = settings.Advanced.PreallocateDiskSpace,
            KeepTempFilesAfterCancel = settings.Advanced.KeepTempFilesAfterCancel,
            KeepServerFileDate = settings.Downloads.KeepServerFileDate,
            HostSpeedLimitsKBps = new Dictionary<string, int>(settings.Connection.HostSpeedLimits, StringComparer.OrdinalIgnoreCase),
            MarkOfTheWeb = settings.Downloads.MarkAsDownloadedFromInternet,
            VirusScanProgram = settings.Downloads.VirusScan.Enabled && !string.IsNullOrWhiteSpace(settings.Downloads.VirusScan.Program)
                ? settings.Downloads.VirusScan.Program
                : null,
            VirusScanArguments = settings.Downloads.VirusScan.Arguments,
        };
    }
}
