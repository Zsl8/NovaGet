using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using FluentFTP;
using FluentFTP.Exceptions;
using FluentFTP.Proxy.AsyncProxy;
using NovaGet.Core.Engine.Naming;
using NovaGet.Core.Network;
using NovaGet.Core.Security;
using NovaGet.Core.Settings;

namespace NovaGet.Core.Engine.Ftp;

/// <summary>
/// FTP and FTPS through FluentFTP. <c>ftp://</c> is plain FTP, <c>ftps://</c> implicit TLS (port 990 by default;
/// on port 21 it means explicit), <c>ftpes://</c> explicit TLS (AUTH TLS). Every connection of a download is its own
/// control + data connection: passive (EPSV/PASV), or active (EPRT/PORT) when "Use FTP in PASV mode" is off and no
/// proxy is in between. A segment starts with REST and ends when the engine stops reading.
/// Logins come from the address, the download, then Site Logins, else anonymous.
/// </summary>
public sealed class FtpTransferProtocol(
    ISiteCredentials? siteCredentials = null,
    Func<ProxySettings>? proxySettings = null,
    ISecretProtector? protector = null) : ITransferProtocol
{
    private const string AnonymousPassword = "anonymous@novaget.invalid";

    public bool CanHandle(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "ftp" or "ftps" or "ftpes";

    public async Task<ProbeResult> ProbeAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = RemotePath(context.Url);
        var client = await ConnectAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            var size = await client.GetFileSize(path, -1, cancellationToken).ConfigureAwait(false);
            if (size < 0 && !await client.FileExists(path, cancellationToken).ConfigureAwait(false))
            {
                throw new DownloadException(DownloadErrorKind.LinkExpired, $"The file {path} does not exist on the server.", 550);
            }

            DateTimeOffset? modified = null;
            try
            {
                var time = await client.GetModifiedTime(path, cancellationToken).ConfigureAwait(false);
                if (time != DateTime.MinValue)
                {
                    modified = new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc));
                }
            }
            catch (FtpCommandException)
            {
                // MDTM is optional.
            }

            var name = Uri.UnescapeDataString(context.Url.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);
            return new ProbeResult
            {
                FinalUri = context.Url,
                Size = size,
                ResumeSupported = size >= 0 && client.HasFeature(FtpCapability.REST),
                FileName = FileNameSanitizer.Sanitize(string.IsNullOrWhiteSpace(name) ? "download" : name, "download"),
                LastModified = modified,
            };
        }
        catch (Exception ex) when (Classify(ex) is { } error)
        {
            throw error;
        }
        finally
        {
            await DisconnectAsync(client).ConfigureAwait(false);
        }
    }

    public async Task<TransferResponse> OpenAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = request.Context;
        var start = request.UseRange ? request.Start : 0;
        var client = await ConnectAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            if (start > 0 && !client.HasFeature(FtpCapability.REST))
            {
                throw new DownloadException(DownloadErrorKind.RangeNotSupported, "The FTP server does not support resuming (REST).");
            }

            Stream body;
            try
            {
                body = await client.OpenRead(RemotePath(context.Url), FtpDataType.Binary, start, checkIfFileExists: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FtpCommandException ex) when (start > 0 && ex.CompletionCode is "500" or "502" or "504" or "350")
            {
                throw new DownloadException(DownloadErrorKind.RangeNotSupported, "The FTP server refused to resume: " + ex.Message, inner: ex);
            }

            return new TransferResponse(new FtpBodyStream(body, client), owner: null)
            {
                IsPartial = start > 0,
                Start = start,
                FinalUri = context.Url,
            };
        }
        catch (Exception ex)
        {
            Abandon(client);
            if (Classify(ex) is { } error)
            {
                throw error;
            }

            throw;
        }
    }

    /// <summary>Maps an FTP address to its encryption mode and port.</summary>
    internal static (FtpEncryptionMode Mode, int Port) Endpoint(Uri uri) => uri.Scheme switch
    {
        "ftps" when uri.Port is 21 => (FtpEncryptionMode.Explicit, 21),
        "ftps" => (FtpEncryptionMode.Implicit, uri.IsDefaultPort || uri.Port <= 0 ? 990 : uri.Port),
        "ftpes" => (FtpEncryptionMode.Explicit, uri.Port <= 0 ? 21 : uri.Port),
        _ => (FtpEncryptionMode.None, uri.Port <= 0 ? 21 : uri.Port),
    };

    /// <summary>The login: address user info, then the download's login, then Site Logins, else anonymous.</summary>
    internal NetworkCredential CredentialFor(RequestContext context)
    {
        var uri = context.Url;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var separator = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
            var user = Uri.UnescapeDataString(separator < 0 ? uri.UserInfo : uri.UserInfo[..separator]);
            var password = separator < 0 ? string.Empty : Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
            if (user.Length > 0)
            {
                return new NetworkCredential(user, password.Length > 0 ? password : context.Password ?? string.Empty);
            }
        }

        if (context.HasCredentials)
        {
            return new NetworkCredential(context.UserName, context.Password ?? string.Empty);
        }

        return siteCredentials?.Find(uri) ?? new NetworkCredential("anonymous", AnonymousPassword);
    }

    private static string RemotePath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        return string.IsNullOrEmpty(path) ? "/" : path;
    }

    private async Task<AsyncFtpClient> ConnectAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var uri = context.Url;
        var (mode, port) = Endpoint(uri);
        var credential = CredentialFor(context);
        var timeout = (int)Math.Clamp(context.Timeout.TotalMilliseconds, 1000, int.MaxValue);
        var config = new FtpConfig
        {
            EncryptionMode = mode,
            DataConnectionEncryption = mode != FtpEncryptionMode.None,
            DataConnectionType = proxySettings?.Invoke().FtpPassiveMode == false ? FtpDataConnectionType.AutoActive : FtpDataConnectionType.AutoPassive,
            ConnectTimeout = timeout,
            ReadTimeout = timeout,
            DataConnectionConnectTimeout = timeout,
            DataConnectionReadTimeout = timeout,
            RetryAttempts = 1,
            SocketKeepAlive = true,
            ValidateAnyCertificate = false,
            CheckCapabilities = true,
        };

        var client = CreateClient(uri.Host, port, credential, config);
        client.ValidateCertificate += (_, e) =>
            e.Accept = e.PolicyErrors == SslPolicyErrors.None || context.IgnoreCertificateErrors;
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (Exception ex)
        {
            Abandon(client);
            if (Classify(ex) is { } error)
            {
                throw error;
            }

            throw;
        }
    }

    /// <summary>A direct client, or one tunneled through the FTP/HTTP proxy or SOCKS from Options → Proxy/Socks.</summary>
    private AsyncFtpClient CreateClient(string host, int port, NetworkCredential credential, FtpConfig config)
    {
        var proxy = proxySettings?.Invoke();
        if (proxy is { Mode: ProxyMode.Manual } && protector is not null
            && !new BypassList(proxy.BypassList).Matches(new UriBuilder("ftp", host, port).Uri))
        {
            // A server can't connect back through a proxy: data connections are always passive there.
            config.DataConnectionType = FtpDataConnectionType.AutoPassive;
            if (proxy.Socks.Enabled && !string.IsNullOrWhiteSpace(proxy.Socks.Host))
            {
                var profile = Profile(host, port, credential, proxy.Socks.Host, proxy.Socks.Port, proxy.Socks.User, proxy.Socks.ProtectedPassword);
                AsyncFtpClientProxy socks = proxy.Socks.Version switch
                {
                    SocksVersion.Socks4 when !proxy.Socks.ResolveDnsThroughSocks => new AsyncFtpClientSocks4Proxy(profile),
                    SocksVersion.Socks4 or SocksVersion.Socks4a => new AsyncFtpClientSocks4aProxy(profile),
                    _ => new AsyncFtpClientSocks5Proxy(profile),
                };
                socks.Config = config;
                return socks;
            }

            var server = proxy.UseSameProxyForAllProtocols ? proxy.Http : proxy.Ftp;
            if (!string.IsNullOrWhiteSpace(server.Host))
            {
                var tunnel = new AsyncFtpClientHttp11Proxy(Profile(host, port, credential, server.Host, server.Port, server.User, server.ProtectedPassword))
                {
                    Config = config,
                };
                return tunnel;
            }
        }

        return new AsyncFtpClient(host, credential, port, config);
    }

    private FtpProxyProfile Profile(string host, int port, NetworkCredential credential, string proxyHost, int proxyPort, string proxyUser, string protectedPassword) => new()
    {
        FtpHost = host,
        FtpPort = port,
        FtpCredentials = credential,
        ProxyHost = proxyHost.Trim(),
        ProxyPort = proxyPort,
        ProxyCredentials = string.IsNullOrEmpty(proxyUser)
            ? null
            : new NetworkCredential(proxyUser, protector!.Unprotect(protectedPassword) ?? string.Empty),
    };

    /// <summary>Ends a session politely (QUIT), but never waits more than a moment for it.</summary>
    private static async Task DisconnectAsync(AsyncFtpClient client)
    {
        try
        {
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Abandon(client);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or FtpException or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Drops a session that failed (for example a "421 too many connections" greeting) without QUIT: the server has
    /// usually closed it already, and waiting for a reply would take the whole read timeout.
    /// </summary>
    private static void Abandon(AsyncFtpClient client)
    {
        try
        {
            client.Config.DisconnectWithQuit = false;
            client.Dispose();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or FtpException or InvalidOperationException)
        {
        }
    }

    /// <summary>Turns FluentFTP and socket failures into download errors (null when it isn't one of ours to map).</summary>
    internal static DownloadException? Classify(Exception exception)
    {
        switch (exception)
        {
            case DownloadException:
                return null;
            case FtpAuthenticationException auth:
                return new DownloadException(DownloadErrorKind.AuthenticationRequired, "FTP login failed: " + auth.Message, 530, auth);
            case FtpCommandException command:
                var code = int.TryParse(command.CompletionCode, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : (int?)null;
                return code switch
                {
                    421 => new DownloadException(DownloadErrorKind.ServerBusy, "The FTP server has too many connections: " + command.Message, 421, command),
                    530 => new DownloadException(DownloadErrorKind.AuthenticationRequired, "FTP login failed: " + command.Message, 530, command),
                    550 => new DownloadException(DownloadErrorKind.LinkExpired, "The file is not available on the FTP server: " + command.Message, 550, command),
                    >= 400 and < 500 => new DownloadException(DownloadErrorKind.Network, "FTP server error: " + command.Message, null, command),
                    _ => new DownloadException(DownloadErrorKind.HttpError, "FTP server error: " + command.Message, 400, command),
                };
            case FtpInvalidCertificateException or AuthenticationException:
                return new DownloadException(DownloadErrorKind.Unknown, "The FTP server's certificate is not trusted: " + exception.Message, null, exception);
            case FtpSecurityNotAvailableException:
                return new DownloadException(DownloadErrorKind.Unknown, "The FTP server does not support encryption (FTPS).", null, exception);
            case TimeoutException or OperationCanceledException { InnerException: TimeoutException }:
                return new DownloadException(DownloadErrorKind.Timeout, "The FTP server did not answer in time.", null, exception);
            case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                return new DownloadException(DownloadErrorKind.ConnectionRefused, "The FTP server refused the connection.", null, exception);
            case SocketException or IOException or FtpMissingSocketException:
                return new DownloadException(DownloadErrorKind.Network, "FTP connection failed: " + exception.Message, null, exception);
            case FtpException ftp when ftp.InnerException is { } inner:
                return Classify(inner) ?? new DownloadException(DownloadErrorKind.Network, ftp.Message, null, ftp);
            case FtpException ftp:
                return new DownloadException(DownloadErrorKind.Network, ftp.Message, null, ftp);
            default:
                return null;
        }
    }

    /// <summary>
    /// The body of one segment. Closing it must not wait for the server: the engine closes a connection as soon as
    /// its segment is complete, mid-file, so a synchronous close (which waits for the transfer's final reply) could
    /// block for the whole read timeout. The data channel gets a short async close, then the session ends.
    /// </summary>
    private sealed class FtpBodyStream(Stream data, AsyncFtpClient client) : Stream
    {
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
        private int _closed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => data.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => data.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            data.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            data.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
            {
                return;
            }

            if (data is FtpDataStream ftp)
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
                var close = ftp.CloseAsync(timeout.Token).AsTask();
                try
                {
                    await close.WaitAsync(CloseTimeout).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or SocketException or FtpException or ObjectDisposedException)
                {
                    // The reply to an interrupted transfer doesn't matter: the session ends below.
                }
            }

            _ = EndSessionAsync();
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _ = EndSessionAsync();
            }

            base.Dispose(disposing);
        }

        /// <summary>QUIT in the background (bounded), closing the sockets either way.</summary>
        private async Task EndSessionAsync()
        {
            try
            {
                await DisconnectAsync(client).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or FtpException)
            {
            }
        }
    }
}
