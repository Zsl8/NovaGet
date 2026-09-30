namespace NovaGet.Core.Models;

/// <summary>
/// Lifecycle: Queued → Connecting → Receiving → (Paused | Error | Completed), plus the
/// transitional Assembling, Merging (streams), Scanning (antivirus), WaitingForRetry and Refreshing states.
/// Values are persisted as integers; never renumber them.
/// </summary>
public enum DownloadStatus
{
    Paused = 0,
    Queued = 1,
    Connecting = 2,
    Receiving = 3,
    Error = 4,
    Completed = 5,
    Assembling = 6,
    Merging = 7,
    Scanning = 8,
    WaitingForRetry = 9,
    Refreshing = 10,
}

public static class DownloadStatusExtensions
{
    /// <summary>True while the engine is working on the download.</summary>
    public static bool IsActive(this DownloadStatus status) => status is
        DownloadStatus.Connecting or DownloadStatus.Receiving or DownloadStatus.Assembling or
        DownloadStatus.Merging or DownloadStatus.Scanning or DownloadStatus.WaitingForRetry or DownloadStatus.Refreshing;

    /// <summary>True when the download can be (re)started by Resume.</summary>
    public static bool IsResumable(this DownloadStatus status) => status is
        DownloadStatus.Paused or DownloadStatus.Queued or DownloadStatus.Error;
}
