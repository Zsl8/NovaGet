namespace NovaGet.TestServer;

/// <summary>One request seen by the server (for asserting ranges, connection reuse, headers).</summary>
public sealed record RequestRecord(
    string Method,
    string Path,
    string? Range,
    string? IfRange,
    int Status,
    string ConnectionId,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset Time)
{
    /// <summary>Start offset of the Range header, or 0 when absent.</summary>
    public long RangeStart
    {
        get
        {
            if (Range is null || !Range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                return 0;
            }

            var dash = Range.IndexOf('-', StringComparison.Ordinal);
            return dash > 6 && long.TryParse(Range[6..dash], out var start) ? start : 0;
        }
    }
}
