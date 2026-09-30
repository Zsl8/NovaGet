using System.Diagnostics;

namespace NovaGet.Core.Engine;

/// <summary>
/// Byte-rate limiter. Readers consume what they received and then wait off any debt, so the long-run rate
/// matches <see cref="BytesPerSecond"/> across any number of connections. A small burst allowance
/// (a tenth of a second's worth) keeps short idle periods from turning into spikes.
/// </summary>
public sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private long _rate;
    private double _tokens;
    private long _last;

    /// <param name="bytesPerSecond">Limit; 0 or less means unlimited.</param>
    /// <param name="clock">Monotonic timestamp source in <see cref="Stopwatch"/> ticks (tests inject a fake).</param>
    public TokenBucket(long bytesPerSecond = 0, Func<long>? clock = null)
    {
        _clock = clock ?? Stopwatch.GetTimestamp;
        _rate = Math.Max(0, bytesPerSecond);
        _last = _clock();
        _tokens = Capacity;
    }

    /// <summary>Current limit in bytes per second; 0 = unlimited. Can be changed while downloads run.</summary>
    public long BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _rate;
            }
        }

        set
        {
            lock (_gate)
            {
                Refill();
                _rate = Math.Max(0, value);
                _tokens = Math.Min(_tokens, Capacity);
            }
        }
    }

    public bool IsLimited => BytesPerSecond > 0;

    private double Capacity => Math.Max(_rate / 10.0, 4096);

    /// <summary>Takes <paramref name="bytes"/> and returns how long the caller must wait to stay within the rate.</summary>
    public TimeSpan Reserve(int bytes)
    {
        lock (_gate)
        {
            if (_rate <= 0)
            {
                return TimeSpan.Zero;
            }

            Refill();
            _tokens -= bytes;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _rate);
        }
    }

    public ValueTask ConsumeAsync(int bytes, CancellationToken cancellationToken)
    {
        var wait = Reserve(bytes);
        return wait <= TimeSpan.Zero ? ValueTask.CompletedTask : new ValueTask(Task.Delay(wait, cancellationToken));
    }

    private void Refill()
    {
        var now = _clock();
        var elapsed = (now - _last) / (double)Stopwatch.Frequency;
        _last = now;
        if (_rate > 0 && elapsed > 0)
        {
            _tokens = Math.Min(Capacity, _tokens + (elapsed * _rate));
        }
    }
}
