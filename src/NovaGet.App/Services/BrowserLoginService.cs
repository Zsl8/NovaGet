namespace NovaGet.App.Services;

/// <summary>
/// Site Grabber → "Log in via browser…": NovaGet asks for a site's login, the extension's popup offers to send that
/// site's cookies, and the user's click delivers them here. Only sites NovaGet is waiting for are accepted.
/// </summary>
internal sealed class BrowserLoginService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<string>> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hosts waiting for a login (sent to the extension with its settings).</summary>
    public IReadOnlyList<string> PendingHosts
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending.Keys];
            }
        }
    }

    /// <summary>Waits until the user sends the site's cookies from the browser (or <paramref name="cancellationToken"/> ends it).</summary>
    public Task<string> WaitForLoginAsync(Uri site, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);
        var host = Bare(site.Host);
        TaskCompletionSource<string> pending;
        lock (_gate)
        {
            if (!_pending.TryGetValue(host, out pending!))
            {
                pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[host] = pending;
            }
        }

        cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(host, out var current) && current == pending)
                {
                    _pending.Remove(host);
                }
            }

            pending.TrySetCanceled(cancellationToken);
        });
        return pending.Task;
    }

    /// <summary>Cookies sent from the popup; false when NovaGet wasn't waiting for that site.</summary>
    public bool Deliver(Uri url, string cookies)
    {
        ArgumentNullException.ThrowIfNull(url);
        var host = Bare(url.Host);
        TaskCompletionSource<string>? pending = null;
        lock (_gate)
        {
            var key = _pending.Keys.FirstOrDefault(k => host.Equals(k, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + k, StringComparison.OrdinalIgnoreCase));
            if (key is not null)
            {
                pending = _pending[key];
                _pending.Remove(key);
            }
        }

        return pending?.TrySetResult(cookies) == true;
    }

    private static string Bare(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}
