using NovaGet.Core.Models;

namespace NovaGet.Core.Engine;

/// <summary>A byte range being downloaded. Mutated only under the owning <see cref="SegmentMap"/>'s lock.</summary>
internal sealed class LiveSegment
{
    public LiveSegment(long start, long end, long position)
    {
        Start = start;
        End = end;
        Received = position;
        Written = position;
    }

    public long Start { get; }

    /// <summary>Last byte (inclusive), or -1 while the size is unknown. Lowered when the segment is split.</summary>
    public long End { get; set; }

    /// <summary>Next byte to receive. Split decisions use this.</summary>
    public long Received { get; set; }

    /// <summary>Everything before this offset is written to the temp file. Checkpoints persist this.</summary>
    public long Written { get; set; }

    /// <summary>The connection currently working on the segment, if any.</summary>
    public object? Owner { get; set; }

    public bool IsReceived => End >= 0 && Received > End;

    public bool IsWritten => End >= 0 && Written > End;

    /// <summary>Bytes still to receive; <see cref="long.MaxValue"/> when the end is unknown.</summary>
    public long Remaining => End < 0 ? long.MaxValue : Math.Max(0, End - Received + 1);
}

/// <summary>Thread-safe set of segments covering a download.</summary>
internal sealed class SegmentMap
{
    private readonly object _gate = new();
    private readonly List<LiveSegment> _segments;

    private SegmentMap(List<LiveSegment> segments, long size)
    {
        _segments = segments;
        Size = size;
    }

    /// <summary>Total size, or -1 while unknown.</summary>
    public long Size { get; private set; }

    public static SegmentMap Create(long size) => new(
        size == 0 ? [] : [new LiveSegment(0, size > 0 ? size - 1 : -1, 0)],
        size);

    /// <summary>Rebuilds the map from a saved checkpoint (positions = the persisted, flushed offsets).</summary>
    public static SegmentMap Restore(IEnumerable<Segment> saved, long size)
    {
        var segments = saved
            .OrderBy(s => s.StartByte)
            .Select(s => new LiveSegment(s.StartByte, s.EndByte, Math.Max(s.StartByte, s.CurrentByte)))
            .ToList();
        return segments.Count == 0 && size != 0 ? Create(size) : new SegmentMap(segments, size);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _segments.Count;
            }
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (_gate)
            {
                return _segments.TrueForAll(s => s.IsWritten);
            }
        }
    }

    public long ReceivedBytes
    {
        get
        {
            lock (_gate)
            {
                return _segments.Sum(s => s.Received - s.Start);
            }
        }
    }

    public long WrittenBytes
    {
        get
        {
            lock (_gate)
            {
                return _segments.Sum(s => s.Written - s.Start);
            }
        }
    }

    /// <summary>Gives <paramref name="owner"/> the first unfinished segment nobody is working on.</summary>
    public LiveSegment? AcquirePending(object owner)
    {
        lock (_gate)
        {
            var segment = _segments.Find(s => s.Owner is null && !s.IsReceived);
            if (segment is not null)
            {
                segment.Owner = owner;
            }

            return segment;
        }
    }

    public void Release(LiveSegment segment)
    {
        lock (_gate)
        {
            segment.Owner = null;
        }
    }

    /// <summary>Accepts up to <paramref name="count"/> received bytes, clamped to the segment's (possibly lowered) end.</summary>
    public int Accept(LiveSegment segment, int count)
    {
        lock (_gate)
        {
            var accepted = segment.End < 0 ? count : (int)Math.Min(count, Math.Max(0, segment.End - segment.Received + 1));
            segment.Received += accepted;
            return accepted;
        }
    }

    public void MarkWritten(LiveSegment segment, long writtenUpTo)
    {
        lock (_gate)
        {
            segment.Written = Math.Max(segment.Written, writtenUpTo);
        }
    }

    /// <summary>Rolls a segment back to its last written offset (after a failed connection lost buffered data).</summary>
    public void RewindToWritten(LiveSegment segment)
    {
        lock (_gate)
        {
            segment.Received = segment.Written;
        }
    }

    /// <summary>Discards a segment's progress (servers that can't resume must resend from its start).</summary>
    public void RewindToStart(LiveSegment segment)
    {
        lock (_gate)
        {
            segment.Received = segment.Start;
            segment.Written = segment.Start;
        }
    }

    /// <summary>Unknown-size download reached end of stream: the size is now known.</summary>
    public void CompleteUnknownSize(LiveSegment segment)
    {
        lock (_gate)
        {
            Size = segment.Received;
            if (segment.Received == segment.Start)
            {
                _segments.Remove(segment); // empty file
            }
            else
            {
                segment.End = segment.Received - 1;
            }
        }
    }

    /// <summary>
    /// The real size became known (or changed) before any data was kept: resize the single segment that
    /// starts at byte 0.
    /// </summary>
    public void SetSize(long size)
    {
        lock (_gate)
        {
            Size = size;
            if (_segments.Count == 1 && _segments[0].Start == 0)
            {
                if (size == 0)
                {
                    _segments.Clear();
                }
                else
                {
                    _segments[0].End = size - 1;
                }
            }
        }
    }

    /// <summary>Throws away all progress (restart from byte 0).</summary>
    public void Reset(long size)
    {
        lock (_gate)
        {
            _segments.Clear();
            Size = size;
            if (size != 0)
            {
                _segments.Add(new LiveSegment(0, size > 0 ? size - 1 : -1, 0));
            }
        }
    }

    /// <summary>Checkpoint form: current offsets are the <em>written</em> ones.</summary>
    public IReadOnlyList<Segment> ToPersisted()
    {
        lock (_gate)
        {
            return [.. _segments.Select(s => new Segment
            {
                StartByte = s.Start,
                EndByte = s.End,
                CurrentByte = s.Written,
                State = s.IsWritten ? SegmentState.Done : s.Owner is null ? SegmentState.Pending : SegmentState.Active,
            })];
        }
    }

    public IReadOnlyList<SegmentProgress> Snapshot()
    {
        lock (_gate)
        {
            return [.. _segments.Select(s => new SegmentProgress(s.Start, s.End, s.Received, s.Owner is not null))];
        }
    }
}
