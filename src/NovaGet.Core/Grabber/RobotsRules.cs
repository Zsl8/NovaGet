using System.Globalization;
using System.Text.RegularExpressions;

namespace NovaGet.Core.Grabber;

/// <summary>
/// robots.txt (RFC 9309): the group for "NovaGet" or else "*"; the longest matching Allow/Disallow path wins (Allow on
/// a tie); <c>*</c> and a trailing <c>$</c> are supported. Crawl-delay is read too.
/// </summary>
public sealed class RobotsRules
{
    public const string AgentName = "NovaGet";

    private readonly List<(bool Allow, string Pattern, Regex Regex)> _rules;

    private RobotsRules(List<(bool Allow, string Pattern, Regex Regex)> rules, TimeSpan? crawlDelay)
    {
        _rules = rules;
        CrawlDelay = crawlDelay;
    }

    /// <summary>Everything allowed (no robots.txt, or it could not be read).</summary>
    public static RobotsRules AllowAll { get; } = new([], null);

    public TimeSpan? CrawlDelay { get; }

    public static RobotsRules Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return AllowAll;
        }

        // Groups: one or more user-agent lines followed by rules.
        var groups = new List<(List<string> Agents, List<(bool, string)> Rules, TimeSpan? Delay)>();
        (List<string> Agents, List<(bool, string)> Rules, TimeSpan? Delay)? current = null;
        var lastWasAgent = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key == "user-agent")
            {
                if (current is null || !lastWasAgent)
                {
                    current = ([], [], null);
                    groups.Add(current.Value);
                }

                current.Value.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
                continue;
            }

            lastWasAgent = false;
            if (current is null)
            {
                continue;
            }

            switch (key)
            {
                case "allow" or "disallow" when value.Length > 0:
                    current.Value.Rules.Add((key == "allow", value));
                    break;
                case "crawl-delay" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0:
                    var index = groups.FindIndex(g => ReferenceEquals(g.Agents, current.Value.Agents));
                    current = (current.Value.Agents, current.Value.Rules, TimeSpan.FromSeconds(Math.Min(seconds, 60)));
                    groups[index] = current.Value;
                    break;
            }
        }

        var agent = AgentName.ToLowerInvariant();
        var group = groups.FirstOrDefault(g => g.Agents.Any(a => a != "*" && agent.Contains(a, StringComparison.Ordinal)));
        if (group.Agents is null)
        {
            group = groups.FirstOrDefault(g => g.Agents.Contains("*"));
        }

        if (group.Agents is null)
        {
            return AllowAll;
        }

        return new RobotsRules([.. group.Rules.Select(r => (r.Item1, r.Item2, ToRegex(r.Item2)))], group.Delay);
    }

    public bool IsAllowed(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (_rules.Count == 0)
        {
            return true;
        }

        var path = url.PathAndQuery;
        var best = -1;
        var allowed = true;
        foreach (var (allow, pattern, regex) in _rules)
        {
            if (regex.IsMatch(path) && (pattern.Length > best || (pattern.Length == best && allow)))
            {
                best = pattern.Length;
                allowed = allow;
            }
        }

        return allowed;
    }

    private static Regex ToRegex(string pattern)
    {
        var anchored = pattern.EndsWith('$');
        var body = Regex.Escape(anchored ? pattern[..^1] : pattern).Replace(@"\*", ".*", StringComparison.Ordinal);
        return new Regex("^" + body + (anchored ? "$" : string.Empty), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
