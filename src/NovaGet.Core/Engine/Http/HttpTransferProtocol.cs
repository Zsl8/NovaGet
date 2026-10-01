using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using NovaGet.Core.Engine.Naming;

namespace NovaGet.Core.Engine.Http;

/// <summary>
/// HTTP/HTTPS: probing, ranged GETs with If-Range, manual redirects (max 20) and error classification.
/// Logins (the download's, the address's, a matching Site Login, or the Windows account when "Use Windows
/// authentication" is on) answer Basic, Digest, NTLM and Negotiate challenges from the original host only.
/// </summary>
public sealed class HttpTransferProtocol(
    IHttpClientProvider clients,
    ISiteCredentials? siteCredentials = null,
    Func<bool>? useWindowsAuthentication = null) : ITransferProtocol
{
    public const int MaxRedirects = 20;

    public bool CanHandle(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// HEAD first; when it fails, has no length, or doesn't advertise ranges, a <c>GET Range: bytes=0-0</c>
    /// settles size (Content-Range) and resume support (206).
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Metadata? head = null;
        try
        {
            var (response, finalUri, redirects) = await SendAsync(HttpMethod.Head, context, null, cancellationToken).ConfigureAwait(false);
            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    head = Metadata.From(response, finalUri, redirects);
                    var acceptsRanges = response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase);
                    if (head.ContentLength is { } length && acceptsRanges)
                    {
                        return head.ToProbe(length, resumeSupported: true);
                    }
                }
            }
        }
        catch (DownloadException ex) when (ex.Kind is not (DownloadErrorKind.TooManyRedirects or DownloadErrorKind.InvalidAddress))
        {
            // Many servers mishandle HEAD (405, 403 on signed URLs, dropped connections); GET decides.
        }

        var (get, getFinal, getRedirects) = await SendAsync(
            HttpMethod.Get, context, r => r.Headers.Range = new RangeHeaderValue(0, 0), cancellationToken).ConfigureAwait(false);
        using (get)
        {
            var meta = Metadata.From(get, getFinal, getRedirects).MergeMissingFrom(head);
            switch (get.StatusCode)
            {
                case HttpStatusCode.PartialContent:
                    var total = get.Content.Headers.ContentRange?.Length ?? head?.ContentLength ?? -1;
                    return meta.ToProbe(total, resumeSupported: true);
                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    // bytes=0-0 is unsatisfiable only for an empty file.
                    return meta.ToProbe(get.Content.Headers.ContentRange?.Length ?? 0, resumeSupported: true);
                case var status when (int)status is >= 200 and < 300:
                    return meta.ToProbe(get.Content.Headers.ContentLength ?? head?.ContentLength ?? -1, resumeSupported: false);
                default:
                    throw ErrorFor(get);
            }
        }
    }

    public async Task<TransferResponse> OpenAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var useRange = request.UseRange && (request.Start > 0 || request.End >= 0);
        var (response, finalUri, _) = await SendAsync(HttpMethod.Get, request.Context, r =>
        {
            if (!useRange)
            {
                return;
            }

            r.Headers.Range = new RangeHeaderValue(request.Start, request.End >= 0 ? request.End : null);
            if (request.Start > 0 && IfRangeFor(request.Validator) is { } condition)
            {
                r.Headers.IfRange = condition;
            }
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                throw new DownloadException(DownloadErrorKind.ServerFileChanged, "The server can no longer supply this part of the file (HTTP 416).", 416)
                {
                    ReportedSize = response.Content.Headers.ContentRange?.Length,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                throw ErrorFor(response);
            }

            var partial = response.StatusCode == HttpStatusCode.PartialContent;
            long start = 0;
            long? total;
            if (partial)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From is not { } from)
                {
                    throw new DownloadException(DownloadErrorKind.HttpError, "The server sent partial content without a valid Content-Range.", 206);
                }

                start = from;
                total = range.Length;
            }
            else
            {
                total = response.Content.Headers.ContentLength;
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new TransferResponse(body, response)
            {
                IsPartial = partial,
                Start = start,
                TotalSize = total,
                FinalUri = finalUri,
                ETag = response.Headers.ETag?.ToString(),
                LastModified = response.Content.Headers.LastModified,
            };
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Sends a request, following up to 20 redirects. Returns the final response with headers read.</summary>
    internal async Task<(HttpResponseMessage Response, Uri FinalUri, int Redirects)> SendAsync(
        HttpMethod method, RequestContext context, Action<HttpRequestMessage>? configure, CancellationToken cancellationToken)
    {
        if (!CanHandle(context.Url))
        {
            throw new DownloadException(DownloadErrorKind.InvalidAddress, $"Unsupported address: {context.Url}");
        }

        var client = clients.GetClient(context, CredentialFor(context));
        var uri = context.Url;
        var redirects = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(context.Timeout);

        while (true)
        {
            using var request = new HttpRequestMessage(method, uri)
            {
                // One TCP connection per segment is the point of multi-connection downloading.
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            ApplyHeaders(request, context, sameHost: string.Equals(uri.Host, context.Url.Host, StringComparison.OrdinalIgnoreCase));
            configure?.Invoke(request);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DownloadException(DownloadErrorKind.Timeout, $"No response from {uri.Host} within {context.Timeout.TotalSeconds:0} seconds.");
            }
            catch (HttpRequestException ex)
            {
                throw ErrorFor(ex, uri);
            }

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (++redirects > MaxRedirects)
                {
                    throw new DownloadException(DownloadErrorKind.TooManyRedirects, $"More than {MaxRedirects} redirects.");
                }

                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!CanHandle(uri))
                {
                    throw new DownloadException(DownloadErrorKind.InvalidAddress, $"Redirected to an unsupported address: {uri}");
                }

                if (response.StatusCode == HttpStatusCode.SeeOther && method != HttpMethod.Head)
                {
                    method = HttpMethod.Get;
                }

                continue;
            }

            return (response, uri, redirects);
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, RequestContext context, bool sameHost)
    {
        var headers = request.Headers;
        headers.TryAddWithoutValidation("User-Agent", context.UserAgent);
        headers.TryAddWithoutValidation("Accept", "*/*");
        headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrWhiteSpace(context.Referrer))
        {
            headers.TryAddWithoutValidation("Referer", context.Referrer);
        }

        // Cookies and credentials belong to the original site; never leak them to a redirect target on another host.
        if (sameHost)
        {
            if (!string.IsNullOrWhiteSpace(context.Cookies))
            {
                headers.TryAddWithoutValidation("Cookie", context.Cookies);
            }

        }

        if (context.ExtraHeaders is not null)
        {
            foreach (var (name, value) in context.ExtraHeaders)
            {
                headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    /// <summary>The login for a request: the download's, the address's (user:password@), a Site Login, or Windows.</summary>
    internal NetworkCredential? CredentialFor(RequestContext context)
    {
        if (context.HasCredentials)
        {
            return new NetworkCredential(context.UserName, context.Password ?? string.Empty);
        }

        var userInfo = context.Url.UserInfo;
        if (userInfo.Length > 0)
        {
            var separator = userInfo.IndexOf(':', StringComparison.Ordinal);
            var user = Uri.UnescapeDataString(separator < 0 ? userInfo : userInfo[..separator]);
            if (user.Length > 0)
            {
                return new NetworkCredential(user, separator < 0 ? string.Empty : Uri.UnescapeDataString(userInfo[(separator + 1)..]));
            }
        }

        if (siteCredentials?.Find(context.Url) is { } saved)
        {
            return saved;
        }

        return useWindowsAuthentication?.Invoke() == true ? CredentialCache.DefaultNetworkCredentials : null;
    }

    private static RangeConditionHeaderValue? IfRangeFor(ResourceValidator? validator)
    {
        if (validator is null)
        {
            return null;
        }

        // If-Range requires a strong ETag; otherwise fall back to the date.
        if (!string.IsNullOrEmpty(validator.ETag)
            && EntityTagHeaderValue.TryParse(validator.ETag, out var etag) && !etag.IsWeak)
        {
            return new RangeConditionHeaderValue(etag);
        }

        return validator.LastModified is { } date ? new RangeConditionHeaderValue(date) : null;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    internal static DownloadException ErrorFor(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        var text = $"HTTP {code} {response.ReasonPhrase}".TrimEnd();
        var kind = code switch
        {
            401 or 407 => DownloadErrorKind.AuthenticationRequired,
            403 or 404 or 410 => DownloadErrorKind.LinkExpired,
            429 or 503 => DownloadErrorKind.ServerBusy,
            _ => DownloadErrorKind.HttpError,
        };
        return new DownloadException(kind, text, code);
    }

    private static DownloadException ErrorFor(HttpRequestException ex, Uri uri)
    {
        var socket = ex.InnerException as SocketException ?? ex.InnerException?.InnerException as SocketException;
        if (socket?.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new DownloadException(DownloadErrorKind.ConnectionRefused, $"{uri.Host} refused the connection.", inner: ex);
        }

        if (socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData)
        {
            return new DownloadException(DownloadErrorKind.Network, $"Cannot resolve {uri.Host}.", inner: ex);
        }

        if (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return new DownloadException(DownloadErrorKind.HttpError, $"Secure connection to {uri.Host} failed: {ex.InnerException.Message}", 495, ex);
        }

        return new DownloadException(DownloadErrorKind.Network, ex.Message, inner: ex);
    }

    /// <summary>Response metadata shared by the HEAD and GET probes.</summary>
    private sealed record Metadata(Uri FinalUri, int Redirects, long? ContentLength, string? ContentType, string? ContentDisposition, string? ETag, DateTimeOffset? LastModified)
    {
        public static Metadata From(HttpResponseMessage response, Uri finalUri, int redirects)
        {
            string? disposition = null;
            if (response.Content.Headers.TryGetValues("Content-Disposition", out var values))
            {
                disposition = string.Join(", ", values);
            }

            return new Metadata(
                finalUri,
                redirects,
                response.Content.Headers.ContentLength,
                response.Content.Headers.ContentType?.ToString(),
                disposition,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified);
        }

        public Metadata MergeMissingFrom(Metadata? other) => other is null ? this : this with
        {
            ContentType = ContentType ?? other.ContentType,
            ContentDisposition = ContentDisposition ?? other.ContentDisposition,
            ETag = ETag ?? other.ETag,
            LastModified = LastModified ?? other.LastModified,
        };

        public ProbeResult ToProbe(long size, bool resumeSupported) => new()
        {
            FinalUri = FinalUri,
            Size = size,
            ResumeSupported = resumeSupported,
            FileName = FileNameResolver.Resolve(ContentDisposition, FinalUri, ContentType),
            ContentType = ContentType,
            ETag = ETag,
            LastModified = LastModified,
            RedirectCount = Redirects,
        };
    }
}
