using NovaGet.Core.Engine;
using NovaGet.Core.Models;

namespace NovaGet.Core.Services;

public enum DownloadListChange
{
    Added,
    Removed,
    Updated,
}

public sealed class DownloadListChangedEventArgs(DownloadListChange change, IReadOnlyList<long> ids) : EventArgs
{
    public DownloadListChange Change { get; } = change;

    public IReadOnlyList<long> Ids { get; } = ids;
}

/// <summary>Totals for the status bar and tray tooltip.</summary>
public sealed record DownloadStatistics(int Total, int Active, double BytesPerSecond);

/// <summary>
/// The download list: an in-memory copy of the database kept in sync with the engine. All UI list operations go
/// through here. Thread-safe; events are raised on the calling or engine thread.
/// </summary>
public interface IDownloadService
{
    /// <summary>Copies of every download, in insertion order.</summary>
    IReadOnlyList<Download> GetAll();

    /// <summary>A copy of one download, or null.</summary>
    Download? Find(long id);

    Download Add(DownloadRequest request);

    /// <summary>Starts or resumes. Returns false if running, complete or unknown.</summary>
    bool Start(long id, bool startedByQueue = false);

    Task StopAsync(long id);

    Task StopAllAsync();

    /// <summary>Removes entries; with <paramref name="deleteFiles"/> also deletes finished files and partial data.</summary>
    Task RemoveAsync(IReadOnlyCollection<long> ids, bool deleteFiles);

    /// <summary>Removes every completed entry from the list (files stay). Returns how many.</summary>
    int RemoveCompleted();

    /// <summary>Downloads the file again from the beginning.</summary>
    Task RedownloadAsync(long id);

    /// <summary>Appends downloads to a queue (in the given order), or takes them out of their queue (null).</summary>
    void SetQueue(IReadOnlyCollection<long> ids, long? queueId);

    /// <summary>Moves a queued download up (-1) or down (+1) within its queue.</summary>
    void MoveInQueue(long id, int delta);

    void SetCategory(IReadOnlyCollection<long> ids, long categoryId);

    /// <summary>Persists edits (properties dialog, rename, address refresh).</summary>
    void Save(Download download);

    DownloadStatistics GetStatistics();

    event EventHandler<DownloadListChangedEventArgs>? Changed;

    /// <summary>Engine state changes, raised after the list copy was refreshed.</summary>
    event EventHandler<DownloadStateChangedEventArgs>? StateChanged;
}
