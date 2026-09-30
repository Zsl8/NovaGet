namespace NovaGet.Core.Engine;

/// <summary>Host matching for per-server settings: exact names or <c>*.example.com</c> wildcards.</summary>
public static class HostPattern
{
    public static bool Matches(string pattern, string host)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        pattern = pattern.Trim().TrimEnd('.');
        host = host.Trim().TrimEnd('.');
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..]; // ".example.com"
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase);
        }

        return host.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The value of the first matching pattern (exact matches win over wildcards).</summary>
    public static int? Lookup(IReadOnlyDictionary<string, int>? table, string host)
    {
        if (table is null || table.Count == 0)
        {
            return null;
        }

        foreach (var (pattern, value) in table)
        {
            if (!pattern.Contains('*', StringComparison.Ordinal) && Matches(pattern, host))
            {
                return value;
            }
        }

        foreach (var (pattern, value) in table)
        {
            if (pattern.Contains('*', StringComparison.Ordinal) && Matches(pattern, host))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>
/// Connection caps learned during this session: when a server answers 429/503 or refuses connections,
/// its host gets a lower cap that later downloads from the same host respect.
/// </summary>
public sealed class HostConnectionLimits
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _limits = new(StringComparer.OrdinalIgnoreCase);

    public int? Get(string host) => _limits.TryGetValue(host, out var limit) ? limit : null;

    /// <summary>Lowers (never raises) the cap for a host. Returns the effective cap.</summary>
    public int Lower(string host, int limit)
    {
        limit = Math.Max(1, limit);
        return _limits.AddOrUpdate(host, limit, (_, existing) => Math.Min(existing, limit));
    }

    public void Clear() => _limits.Clear();
}
