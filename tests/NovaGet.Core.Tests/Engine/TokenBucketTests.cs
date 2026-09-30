using System.Diagnostics;
using NovaGet.Core.Engine;

namespace NovaGet.Core.Tests.Engine;

public sealed class TokenBucketTests
{
    private sealed class FakeClock
    {
        public long Ticks { get; private set; }

        public void Advance(TimeSpan time) => Ticks += (long)(time.TotalSeconds * Stopwatch.Frequency);
    }

    [Fact]
    public void Unlimited_never_waits()
    {
        var bucket = new TokenBucket(0);

        Assert.Equal(TimeSpan.Zero, bucket.Reserve(10_000_000));
        Assert.False(bucket.IsLimited);
    }

    [Fact]
    public void Debt_is_repaid_at_the_configured_rate()
    {
        var clock = new FakeClock();
        var bucket = new TokenBucket(100_000, () => clock.Ticks); // burst allowance: 10 000 bytes

        Assert.Equal(TimeSpan.Zero, bucket.Reserve(10_000));
        Assert.Equal(0.5, bucket.Reserve(50_000).TotalSeconds, 3);
        clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(TimeSpan.Zero, bucket.Reserve(0));
        Assert.Equal(1.0, bucket.Reserve(100_000).TotalSeconds, 3);
    }

    [Fact]
    public void Long_run_rate_matches_the_limit()
    {
        var clock = new FakeClock();
        var bucket = new TokenBucket(512 * 1024, () => clock.Ticks);
        long transferred = 0;
        var elapsed = TimeSpan.Zero;

        // Simulate 4 connections reading 64 KB chunks and waiting as told, for 20 seconds.
        while (elapsed < TimeSpan.FromSeconds(20))
        {
            for (var c = 0; c < 4; c++)
            {
                var wait = bucket.Reserve(64 * 1024);
                transferred += 64 * 1024;
                clock.Advance(wait);
                elapsed += wait;
            }
        }

        var rate = transferred / elapsed.TotalSeconds;
        Assert.InRange(rate, 512 * 1024 * 0.95, 512 * 1024 * 1.05);
    }

    [Fact]
    public void Idle_time_does_not_allow_a_large_burst()
    {
        var clock = new FakeClock();
        var bucket = new TokenBucket(100_000, () => clock.Ticks);
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(bucket.Reserve(100_000) > TimeSpan.FromSeconds(0.8));
    }

    [Fact]
    public void Rate_can_change_on_the_fly()
    {
        var clock = new FakeClock();
        var bucket = new TokenBucket(100_000, () => clock.Ticks);
        bucket.Reserve(10_000);

        bucket.BytesPerSecond = 0;
        Assert.Equal(TimeSpan.Zero, bucket.Reserve(1_000_000));

        bucket.BytesPerSecond = 1000;
        Assert.True(bucket.Reserve(2000) > TimeSpan.FromSeconds(1));
    }
}
