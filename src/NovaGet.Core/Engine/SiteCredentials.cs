using System.Net;
using System.Text.RegularExpressions;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Models;

namespace NovaGet.Core.Engine;

/// <summary>Options → Site Logins: the saved login for an address, if one matches.</summary>
public interface ISiteCredentials
{
    NetworkCredential? Find(Uri uri);
}

/// <summary>
/// Matches saved logins against addresses. A pattern is either a host (<c>example.com</c>, <c>*.example.com</c>)
/// or an address with wildcards (<c>https://example.com/private/*</c>, <c>example.com/files/*</c>). The most
/// specific (longest) matching pattern wins. Logins are re-read from the database at most every few seconds.
/// </summary>
public sealed class SiteCredentials(ISiteLoginRepository repository, TimeProvider? time = null) : ISiteCredentials
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private IReadOnlyList<(SiteLogin Login, Regex Pattern, bool MatchesHostOnly)> _logins = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public NetworkCredential? Find(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        foreach (var (login, pattern, hostOnly) in Logins())
        {
            var subject = hostOnly ? uri.Host : Strip(uri.GetLeftPart(UriPartial.Path), uri.Scheme);
            if (pattern.IsMatch(subject) || (!hostOnly && pattern.IsMatch(uri.GetLeftPart(UriPartial.Path))))
            {
                return new NetworkCredential(login.User, login.Password);
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="pattern"/> matches <paramref name="uri"/> (for tests and the Options dialog).</summary>
    public static bool Matches(string pattern, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var (regex, hostOnly) = Compile(pattern);
        return hostOnly
            ? regex.IsMatch(uri.Host)
            : regex.IsMatch(Strip(uri.GetLeftPart(UriPartial.Path), uri.Scheme)) || regex.IsMatch(uri.GetLeftPart(UriPartial.Path));
    }

    /// <summary>Forget the cached list (after Options saved new logins).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _loadedAt = DateTimeOffset.MinValue;
        }
    }

    private IReadOnlyList<(SiteLogin Login, Regex Pattern, bool MatchesHostOnly)> Logins()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now - _loadedAt > CacheTime)
            {
                _logins = [.. repository.GetAll()
                    .Where(l => !string.IsNullOrWhiteSpace(l.UrlPattern))
                    .OrderByDescending(l => l.UrlPattern.Trim().Length)
                    .Select(l =>
                    {
                        var (regex, hostOnly) = Compile(l.UrlPattern);
                        return (l, regex, hostOnly);
                    })];
                _loadedAt = now;
            }

            return _logins;
        }
    }

    private static (Regex Pattern, bool HostOnly) Compile(string pattern)
    {
        pattern = pattern.Trim();
        var hostOnly = !pattern.Contains('/', StringComparison.Ordinal);
        var body = Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal);
        if (hostOnly && pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            // *.example.com also matches example.com itself.
            body = "(.*\\.)?" + Regex.Escape(pattern[2..]).Replace(@"\*", ".*", StringComparison.Ordinal);
        }

        return (new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), hostOnly);
    }

    private static string Strip(string address, string scheme) =>
        address.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase) ? address[(scheme.Length + 3)..] : address;
}
