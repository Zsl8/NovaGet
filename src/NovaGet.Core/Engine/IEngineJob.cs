namespace NovaGet.Core.Engine;

/// <summary>A running download as the engine sees it: a file download (<see cref="DownloadJob"/>) or a stream (<see cref="Streams.StreamJob"/>).</summary>
internal interface IEngineJob
{
    long Id { get; }

    Task Completion { get; }

    event EventHandler<DownloadStateChangedEventArgs>? StateChanged;

    void Start();

    void Stop(StopReason reason);

    void SetSpeedLimit(int? kilobytesPerSecond);

    DownloadProgress GetProgress();
}

/// <summary>A download's own speed limit combined with the global limiter.</summary>
internal sealed class TransferThrottle(long bytesPerSecond, SpeedLimits speedLimits, bool startedByQueue)
{
    private readonly TokenBucket _own = new(Math.Max(0, bytesPerSecond));

    /// <summary>Changes this download's own limit while it runs (null/0 = unlimited).</summary>
    public void SetLimit(int? kilobytesPerSecond) =>
        _own.BytesPerSecond = kilobytesPerSecond is > 0 ? kilobytesPerSecond.Value * 1024L : 0;

    /// <summary>
    /// Largest read a connection should make: unlimited normally, about 100 ms worth of the tightest active
    /// limit otherwise, so a limited download doesn't burst a whole buffer per connection before waiting.
    /// </summary>
    public int MaxReadSize
    {
        get
        {
            var rate = long.MaxValue;
            if (_own.IsLimited)
            {
                rate = _own.BytesPerSecond;
            }

            if (speedLimits.For(startedByQueue) is { } global)
            {
                rate = Math.Min(rate, global.BytesPerSecond);
            }

            return rate == long.MaxValue ? int.MaxValue : (int)Math.Clamp(rate / 10, 4096, 64 * 1024);
        }
    }

    /// <summary>Waits as needed to respect this download's limit and the global limiter.</summary>
    public async ValueTask ThrottleAsync(int count, CancellationToken token)
    {
        if (_own.IsLimited)
        {
            await _own.ConsumeAsync(count, token).ConfigureAwait(false);
        }

        if (speedLimits.For(startedByQueue) is { } global)
        {
            await global.ConsumeAsync(count, token).ConfigureAwait(false);
        }
    }
}
