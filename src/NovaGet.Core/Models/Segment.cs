namespace NovaGet.Core.Models;

public enum SegmentState
{
    Pending = 0,
    Active = 1,
    Done = 2,
    Error = 3,
}

/// <summary>
/// A byte range of a download (table <c>Segment</c>). <see cref="EndByte"/> is inclusive, or -1 when the size is unknown.
/// <see cref="CurrentByte"/> is the next byte to fetch.
/// </summary>
public sealed class Segment
{
    public long Id { get; set; }

    public long DownloadId { get; set; }

    public long StartByte { get; set; }

    public long EndByte { get; set; } = -1;

    public long CurrentByte { get; set; }

    public SegmentState State { get; set; }

    public long Downloaded => CurrentByte - StartByte;

    /// <summary>Bytes still to fetch, or -1 when the end is unknown.</summary>
    public long Remaining => EndByte < 0 ? -1 : Math.Max(0, EndByte - CurrentByte + 1);

    public bool IsComplete => EndByte >= 0 && CurrentByte > EndByte;
}
