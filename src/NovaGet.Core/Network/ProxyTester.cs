using System.Net;
using System.Net.Http.Headers;
using NovaGet.Core.Engine;
using NovaGet.Core.Security;
using NovaGet.Core.Settings;

namespace NovaGet.Core.Network;

public sealed record ProxyTestResult(bool Success, int? StatusCode, string Message, TimeSpan Elapsed);

/// <summary>Options → Proxy/Socks → Test: one HEAD request to an address the user typed, through the settings being edited.</summary>
public static class ProxyTester
{
    public static async Task<ProxyTestResult> TestAsync(
        ProxySettings settings,
        ISecretProtector protector,
        Uri url,
        TimeSpan timeout,
        IPacResolver? pac = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme is not ("http" or "https"))
        {
            return new ProxyTestResult(false, null, "Enter an http:// or https:// address to test with.", TimeSpan.Zero);
        }

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = timeout,
        };
        ProxyFactory.Configure(handler, settings, protector, pac);
        using var client = new HttpClient(handler) { Timeout = timeout };
        using var request = new HttpRequestMessage(HttpMethod.Head, url) { Version = HttpVersion.Version11 };
        request.Headers.UserAgent.ParseAdd(RequestContext.DefaultUserAgent);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            var message = $"HTTP {code} {response.ReasonPhrase}".TrimEnd();
            // 407 means the proxy answered but refused the credentials; anything else came from the server.
            return new ProxyTestResult(response.StatusCode != HttpStatusCode.ProxyAuthenticationRequired, code, message, started.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProxyTestResult(false, null, "The request timed out.", started.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return new ProxyTestResult(false, null, Innermost(ex).Message, started.Elapsed);
        }
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex;
    }
}
