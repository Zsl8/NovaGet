using NovaGet.Core.Models;

namespace NovaGet.Core.Engine;

/// <summary>Live view of a running download, polled by the UI (at most a few times per second).</summary>
public sealed record DownloadProgress
{
    public long Id { get; init; }

    public DownloadStatus Status { get; init; }

    /// <summary>Total bytes, or -1 when unknown.</summary>
    public long Size { get; init; }

    public long Downloaded { get; init; }

    public double BytesPerSecond { get; init; }

    public TimeSpan? TimeLeft { get; init; }

    public bool? ResumeCapable { get; init; }

    public IReadOnlyList<ConnectionProgress> Connections { get; init; } = [];

    public IReadOnlyList<SegmentProgress> Segments { get; init; } = [];

    /// <summary>Human-readable state, e.g. "Receiving data..." or the last error.</summary>
    public string? Message { get; init; }
}

/// <summary>One row of "Start positions and download progress by connections".</summary>
public sealed record ConnectionProgress(int Number, long Downloaded, string Info);

/// <summary>A segment for the segment map bar: <c>[Start, End]</c> with bytes up to <c>Received</c> done.</summary>
public sealed record SegmentProgress(long Start, long End, long Received, bool Active);

public sealed class DownloadStateChangedEventArgs(long id, DownloadStatus status, DownloadErrorKind errorKind = DownloadErrorKind.None, string? message = null) : EventArgs
{
    public long Id { get; } = id;

    public DownloadStatus Status { get; } = status;

    public DownloadErrorKind ErrorKind { get; } = errorKind;

    public string? Message { get; } = message;
}
