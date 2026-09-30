namespace NovaGet.Core.Engine;

/// <summary>The global speed limiter (Downloads → Speed Limiter), shared by all running downloads.</summary>
public sealed class SpeedLimits
{
    public TokenBucket Global { get; } = new();

    /// <summary>"Apply to scheduler queues only": downloads started by hand are not limited globally.</summary>
    public bool QueueOnly { get; set; }

    /// <summary>Turns the global limit on (KB/s) or off (null / 0).</summary>
    public void SetGlobal(int? kilobytesPerSecond, bool queueOnly)
    {
        QueueOnly = queueOnly;
        Global.BytesPerSecond = kilobytesPerSecond is > 0 ? kilobytesPerSecond.Value * 1024L : 0;
    }

    internal TokenBucket? For(bool startedByQueue) =>
        Global.IsLimited && (startedByQueue || !QueueOnly) ? Global : null;
}
