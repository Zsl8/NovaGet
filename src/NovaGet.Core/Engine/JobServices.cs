namespace NovaGet.Core.Engine;

/// <summary>Engine-wide state shared by every running download.</summary>
internal sealed record JobServices(SpeedLimits SpeedLimits, HostConnectionLimits HostLimits, TrafficCounter Traffic);

/// <summary>Bytes received by every download since the engine started (for download limits).</summary>
public sealed class TrafficCounter
{
    private long _total;

    public long TotalBytes => Interlocked.Read(ref _total);

    internal void Add(long bytes) => Interlocked.Add(ref _total, bytes);
}
