namespace NovaGet.Core.Engine;

/// <summary>Everything needed to make a request for a download's address.</summary>
public sealed record RequestContext
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    public required Uri Url { get; init; }

    public string? Referrer { get; init; }

    /// <summary>Raw Cookie header value captured from the browser.</summary>
    public string? Cookies { get; init; }

    public string UserAgent { get; init; } = DefaultUserAgent;

    public string? UserName { get; init; }

    public string? Password { get; init; }

    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }

    /// <summary>Applies to connecting, receiving headers and each body read.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool IgnoreCertificateErrors { get; init; }

    public bool HasCredentials => !string.IsNullOrEmpty(UserName);

    /// <summary>The request a download makes: its address, referrer, cookies, login and user agent.</summary>
    public static RequestContext For(Models.Download download, string defaultUserAgent, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(download);
        return new RequestContext
        {
            Url = new Uri(download.Url),
            Referrer = download.Referrer,
            Cookies = download.Cookies,
            UserAgent = string.IsNullOrWhiteSpace(download.UserAgent) ? defaultUserAgent : download.UserAgent,
            UserName = download.AuthUser,
            Password = download.AuthPassword,
            Timeout = timeout,
            IgnoreCertificateErrors = download.IgnoreCertificateErrors,
        };
    }
}
