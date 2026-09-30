namespace NovaGet.Core.Models;

/// <summary>A download queue (table <c>Queue</c>). "Main download queue" and "Synchronization queue" are built in.</summary>
public sealed class DownloadQueue
{
    public const long MainQueueId = 1;
    public const long SyncQueueId = 2;

    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsBuiltIn { get; set; }

    /// <summary>"Download N files at the same time".</summary>
    public int SimultaneousCount { get; set; } = 1;

    public QueueSchedule Schedule { get; set; } = new();

    public bool IsSyncQueue { get; set; }

    public int SyncIntervalMinutes { get; set; } = 60;
}

public enum ScheduleMode
{
    OneTime,
    Daily,
}

public enum PowerAction
{
    None,
    ShutDown,
    Sleep,
    Hibernate,
    LogOff,
}

/// <summary>Scheduler tab settings, stored as JSON in <c>Queue.scheduleJson</c>.</summary>
public sealed class QueueSchedule
{
    public ScheduleMode Mode { get; set; } = ScheduleMode.Daily;

    /// <summary>Date for <see cref="ScheduleMode.OneTime"/> (local time).</summary>
    public DateTime? OneTimeDate { get; set; }

    /// <summary>Days for <see cref="ScheduleMode.Daily"/>; all seven means "Every day".</summary>
    public List<DayOfWeek> Days { get; set; } = [.. Enum.GetValues<DayOfWeek>()];

    public bool StartEnabled { get; set; }

    /// <summary>Local time of day to start, e.g. 02:00.</summary>
    public TimeSpan StartTime { get; set; } = new(2, 0, 0);

    public bool StopEnabled { get; set; }

    public TimeSpan StopTime { get; set; } = new(7, 0, 0);

    public bool StartOnAppStartup { get; set; }

    public int RetriesPerFile { get; set; } = 10;

    public bool OpenFileWhenDone { get; set; }

    public string? OpenFilePath { get; set; }

    public bool HangUpWhenDone { get; set; }

    public bool ExitWhenDone { get; set; }

    public bool TurnOffWhenDone { get; set; }

    public PowerAction PowerAction { get; set; } = PowerAction.ShutDown;

    public bool ForceProcessesToTerminate { get; set; }

    /// <summary>Register a Task Scheduler wake task that launches <c>NovaGet.exe /startqueue "name"</c>.</summary>
    public bool WakeComputer { get; set; }

    /// <summary>Synchronization queue: check at startup.</summary>
    public bool SyncAtStartup { get; set; }

    /// <summary>Synchronization queue: keep the previous copy as .bak when replacing.</summary>
    public bool SyncKeepBackup { get; set; }
}
