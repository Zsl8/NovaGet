namespace NovaGet.Core.Engine;

/// <summary>Transfer rate as a moving average over the last few seconds, sampled periodically.</summary>
public sealed class SpeedMeter
{
    private readonly object _gate = new();
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private readonly long _windowTicks;
    private double _bytesPerSecond;

    public SpeedMeter(TimeSpan? window = null)
    {
        _windowTicks = (window ?? TimeSpan.FromSeconds(3)).Ticks;
    }

    /// <summary>Latest moving-average rate in bytes per second.</summary>
    public double BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _bytesPerSecond;
            }
        }
    }

    /// <summary>Records the cumulative byte count at <paramref name="now"/> and recomputes the rate.</summary>
    public void Sample(long totalBytes, DateTime now)
    {
        lock (_gate)
        {
            var ticks = now.Ticks;
            _samples.Enqueue((ticks, totalBytes));
            while (_samples.Count > 2 && ticks - _samples.Peek().Ticks > _windowTicks)
            {
                _samples.Dequeue();
            }

            var (firstTicks, firstBytes) = _samples.Peek();
            var elapsed = TimeSpan.FromTicks(ticks - firstTicks).TotalSeconds;
            _bytesPerSecond = elapsed > 0 ? Math.Max(0, (totalBytes - firstBytes) / elapsed) : 0;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _bytesPerSecond = 0;
        }
    }

    /// <summary>Estimated time to finish, or null when the rate or size is unknown.</summary>
    public static TimeSpan? TimeLeft(long size, long downloaded, double bytesPerSecond) =>
        size > 0 && bytesPerSecond > 1 && downloaded <= size
            ? TimeSpan.FromSeconds(Math.Ceiling((size - downloaded) / bytesPerSecond))
            : null;
}
