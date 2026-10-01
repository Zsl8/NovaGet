namespace NovaGet.Core.Engine;

/// <summary>Runs downloads. Thread-safe; events are raised on background threads.</summary>
public interface IDownloadEngine
{
    /// <summary>Starts or resumes a saved download. Returns false if it is already running or complete.</summary>
    /// <param name="startedByQueue">True when a queue/scheduler starts it (matters for "Apply to scheduler queues only").</param>
    bool Start(long downloadId, bool startedByQueue = false);

    /// <summary>Stops a running download, saving its progress (status Paused).</summary>
    Task PauseAsync(long downloadId);

    /// <summary>Stops every running download, saving progress.</summary>
    Task PauseAllAsync();

    /// <summary>Discards progress and downloads the file again from the beginning.</summary>
    Task RestartAsync(long downloadId);

    /// <summary>Discards progress so the next start downloads from the beginning (leaves it paused).</summary>
    Task ResetAsync(long downloadId);

    /// <summary>Stops a download that is being removed from the list and deletes its temp files.</summary>
    Task RemoveAsync(long downloadId);

    /// <summary>Changes a running download's own speed limit (null/0 = unlimited). Persisting it is up to the caller.</summary>
    void SetSpeedLimit(long downloadId, int? kilobytesPerSecond);

    /// <summary>The global limiter shared by all downloads.</summary>
    SpeedLimits SpeedLimits { get; }

    /// <summary>Everything received so far, for Options → Connection → Download limits.</summary>
    TrafficCounter Traffic { get; }

    /// <summary>Connection caps learned this session from servers that refuse extra connections.</summary>
    HostConnectionLimits HostLimits { get; }

    bool IsRunning(long downloadId);

    IReadOnlyCollection<long> RunningIds { get; }

    /// <summary>Live progress, or null when the download isn't running.</summary>
    DownloadProgress? GetProgress(long downloadId);

    /// <summary>After a crash or kill, marks downloads left in an active state as Paused. Returns how many.</summary>
    int RecoverInterrupted();

    event EventHandler<DownloadStateChangedEventArgs>? StateChanged;
}
