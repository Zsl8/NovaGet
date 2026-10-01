using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NovaGet.TestServer;

public enum FtpTlsMode
{
    /// <summary>Plain FTP; AUTH TLS is refused.</summary>
    None,

    /// <summary>Plain connection that may be upgraded with AUTH TLS (explicit FTPS).</summary>
    Explicit,

    /// <summary>TLS from the first byte (implicit FTPS).</summary>
    Implicit,
}

/// <summary>
/// A small FTP/FTPS server for engine tests: login, SIZE, MDTM, REST, RETR over passive (PASV/EPSV) data connections,
/// optional TLS on control and data channels, throttling and a connection limit (421). Files are <see cref="TestFile"/>s.
/// </summary>
public sealed class TestFtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, TestFile> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate;
    private readonly List<Task> _sessions = [];
    private readonly Task _acceptLoop;
    private int _connections;
    private int _peakConnections;

    private TestFtpServer(FtpTlsMode tls)
    {
        TlsMode = tls;
        _certificate = CreateCertificate();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public FtpTlsMode TlsMode { get; }

    public int Port { get; }

    /// <summary>User name → password. Anonymous logins are accepted when <see cref="AllowAnonymous"/> is on.</summary>
    public ConcurrentDictionary<string, string> Users { get; } = new(StringComparer.Ordinal);

    public bool AllowAnonymous { get; set; } = true;

    /// <summary>Refuse REST (servers without resume support).</summary>
    public bool RefuseRest { get; set; }

    /// <summary>Connections beyond this get "421 Too many connections" (0 = unlimited).</summary>
    public int MaxConnections { get; set; }

    /// <summary>Every command received, e.g. <c>REST 1024</c> (passwords are masked).</summary>
    public IReadOnlyCollection<string> Commands => _commands;

    public int PeakConnections => Volatile.Read(ref _peakConnections);

    public static Task<TestFtpServer> StartAsync(FtpTlsMode tls = FtpTlsMode.None) => Task.FromResult(new TestFtpServer(tls));

    public TestFile AddFile(string path, long size, int seed = 1)
    {
        var file = new TestFile(path, size, seed);
        _files[path] = file;
        return file;
    }

    /// <summary>The address of a file, e.g. <c>ftp://127.0.0.1:2121/dir/a.bin</c> (<c>ftps://</c> for implicit TLS, <c>ftpes://</c> for explicit).</summary>
    public Uri UrlFor(string path, string? user = null, string? password = null)
    {
        var scheme = TlsMode switch
        {
            FtpTlsMode.Implicit => "ftps",
            FtpTlsMode.Explicit => "ftpes",
            _ => "ftp",
        };
        var userInfo = user is null ? string.Empty : Uri.EscapeDataString(user) + (password is null ? string.Empty : ":" + Uri.EscapeDataString(password)) + "@";
        return new Uri($"{scheme}://{userInfo}127.0.0.1:{Port}{path}");
    }

    public Uri UrlFor(TestFile file) => UrlFor(file.Path);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        Task[] sessions;
        lock (_sessions)
        {
            sessions = [.. _sessions];
        }

        await Task.WhenAny(Task.WhenAll(sessions), Task.Delay(2000)).ConfigureAwait(false);
        _certificate.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            var session = Task.Run(() => SessionAsync(client));
            lock (_sessions)
            {
                _sessions.Add(session);
            }
        }
    }

    private async Task SessionAsync(TcpClient client)
    {
        var active = Interlocked.Increment(ref _connections);
        int peak;
        while (active > (peak = Volatile.Read(ref _peakConnections)) && Interlocked.CompareExchange(ref _peakConnections, active, peak) != peak)
        {
        }

        try
        {
            using (client)
            {
                client.NoDelay = true;
                Stream control = client.GetStream();
                if (TlsMode == FtpTlsMode.Implicit)
                {
                    control = await AuthenticateAsync(control).ConfigureAwait(false);
                }

                var session = new Session(this, control);
                if (MaxConnections > 0 && active > MaxConnections)
                {
                    await session.ReplyAsync("421 Too many connections from this IP.").ConfigureAwait(false);
                    return;
                }

                await session.RunAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or AuthenticationException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
        }
    }

    private async Task<SslStream> AuthenticateAsync(Stream stream)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, _stop.Token).ConfigureAwait(false);
        return ssl;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Re-import so the private key is usable by SslStream on every platform.
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    private sealed class Session(TestFtpServer server, Stream control)
    {
        private static readonly Encoding Latin1 = Encoding.Latin1;
        private Stream _control = control;
        private StreamReader _reader = new(control, Latin1, false, 1024, leaveOpen: true);
        private string? _user;
        private bool _loggedIn;
        private bool _protectData;
        private long _restart;
        private TcpListener? _passive;

        public async Task ReplyAsync(string line)
        {
            var bytes = Latin1.GetBytes(line + "\r\n");
            await _control.WriteAsync(bytes).ConfigureAwait(false);
            await _control.FlushAsync().ConfigureAwait(false);
        }

        public async Task RunAsync(CancellationToken stop)
        {
            await ReplyAsync("220 NovaGet test FTP server ready.").ConfigureAwait(false);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync(stop).ConfigureAwait(false);
                    if (line is null)
                    {
                        return;
                    }

                    var space = line.IndexOf(' ', StringComparison.Ordinal);
                    var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
                    var argument = space < 0 ? string.Empty : line[(space + 1)..];
                    server._commands.Enqueue(verb == "PASS" ? "PASS ***" : line);
                    if (!await HandleAsync(verb, argument).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
            finally
            {
                _passive?.Stop();
            }
        }

        private async Task<bool> HandleAsync(string verb, string argument)
        {
            switch (verb)
            {
                case "USER":
                    _user = argument;
                    _loggedIn = false;
                    await ReplyAsync("331 Password required.").ConfigureAwait(false);
                    return true;
                case "PASS":
                    var anonymous = _user is "anonymous" or "ftp";
                    _loggedIn = (anonymous && server.AllowAnonymous)
                        || (_user is not null && server.Users.TryGetValue(_user, out var password) && password == argument);
                    await ReplyAsync(_loggedIn ? "230 Logged in." : "530 Login incorrect.").ConfigureAwait(false);
                    return true;
                case "AUTH":
                    if (server.TlsMode != FtpTlsMode.Explicit || !argument.StartsWith("TLS", StringComparison.OrdinalIgnoreCase))
                    {
                        await ReplyAsync("502 TLS not available.").ConfigureAwait(false);
                        return true;
                    }

                    await ReplyAsync("234 Proceed with negotiation.").ConfigureAwait(false);
                    _control = await server.AuthenticateAsync(_control).ConfigureAwait(false);
                    _reader = new StreamReader(_control, Latin1, false, 1024, leaveOpen: true);
                    return true;
                case "PBSZ":
                    await ReplyAsync("200 PBSZ=0").ConfigureAwait(false);
                    return true;
                case "PROT":
                    _protectData = argument.Trim().Equals("P", StringComparison.OrdinalIgnoreCase);
                    await ReplyAsync("200 Protection level set.").ConfigureAwait(false);
                    return true;
                case "SYST":
                    await ReplyAsync("215 UNIX Type: L8").ConfigureAwait(false);
                    return true;
                case "FEAT":
                    await ReplyAsync("211-Features:").ConfigureAwait(false);
                    foreach (var feature in Features())
                    {
                        await ReplyAsync(" " + feature).ConfigureAwait(false);
                    }

                    await ReplyAsync("211 End").ConfigureAwait(false);
                    return true;
                case "OPTS":
                case "TYPE":
                case "MODE":
                case "STRU":
                case "NOOP":
                case "CLNT":
                    await ReplyAsync("200 OK.").ConfigureAwait(false);
                    return true;
                case "PWD":
                    await ReplyAsync("257 \"/\" is the current directory.").ConfigureAwait(false);
                    return true;
                case "CWD":
                    await ReplyAsync("250 OK.").ConfigureAwait(false);
                    return true;
                case "QUIT":
                    await ReplyAsync("221 Goodbye.").ConfigureAwait(false);
                    return false;
                case "ABOR":
                    await ReplyAsync("226 Abort successful.").ConfigureAwait(false);
                    return true;
            }

            if (!_loggedIn)
            {
                await ReplyAsync("530 Please log in.").ConfigureAwait(false);
                return true;
            }

            switch (verb)
            {
                case "SIZE":
                    await ReplyAsync(Find(argument) is { } sized ? $"213 {sized.Size}" : "550 No such file.").ConfigureAwait(false);
                    return true;
                case "MDTM":
                    await ReplyAsync(Find(argument) is { } dated
                        ? "213 " + dated.LastModified.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                        : "550 No such file.").ConfigureAwait(false);
                    return true;
                case "REST":
                    if (server.RefuseRest || !long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                    {
                        await ReplyAsync("502 REST not supported.").ConfigureAwait(false);
                        return true;
                    }

                    _restart = offset;
                    await ReplyAsync($"350 Restarting at {offset}.").ConfigureAwait(false);
                    return true;
                case "EPSV":
                    var epsv = OpenPassive();
                    await ReplyAsync($"229 Entering Extended Passive Mode (|||{epsv}|)").ConfigureAwait(false);
                    return true;
                case "PASV":
                    var pasv = OpenPassive();
                    await ReplyAsync($"227 Entering Passive Mode (127,0,0,1,{pasv / 256},{pasv % 256}).").ConfigureAwait(false);
                    return true;
                case "RETR":
                    await RetrieveAsync(argument).ConfigureAwait(false);
                    return true;
                default:
                    await ReplyAsync("502 Command not implemented.").ConfigureAwait(false);
                    return true;
            }
        }

        private IEnumerable<string> Features()
        {
            yield return "SIZE";
            yield return "MDTM";
            if (!server.RefuseRest)
            {
                yield return "REST STREAM";
            }

            yield return "EPSV";
            yield return "PASV";
            yield return "UTF8";
            if (server.TlsMode != FtpTlsMode.None)
            {
                yield return "AUTH TLS";
                yield return "PBSZ";
                yield return "PROT";
            }
        }

        private TestFile? Find(string path)
        {
            path = path.Trim();
            if (!path.StartsWith('/'))
            {
                path = "/" + path;
            }

            return server._files.TryGetValue(path, out var file) ? file : null;
        }

        private int OpenPassive()
        {
            _passive?.Stop();
            _passive = new TcpListener(IPAddress.Loopback, 0);
            _passive.Start(1);
            return ((IPEndPoint)_passive.LocalEndpoint).Port;
        }

        private async Task RetrieveAsync(string path)
        {
            var file = Find(path);
            var start = _restart;
            _restart = 0;
            if (file is null)
            {
                await ReplyAsync("550 No such file.").ConfigureAwait(false);
                return;
            }

            if (_passive is null)
            {
                await ReplyAsync("425 Use PASV or EPSV first.").ConfigureAwait(false);
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var data = await _passive.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            _passive.Stop();
            _passive = null;
            await ReplyAsync($"150 Opening BINARY mode data connection for {file.Path} ({file.Size} bytes).").ConfigureAwait(false);

            Stream stream = data.GetStream();
            var complete = false;
            try
            {
                if (_protectData)
                {
                    stream = await server.AuthenticateAsync(stream).ConfigureAwait(false);
                }

                await SendAsync(file, start, stream).ConfigureAwait(false);
                complete = true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or AuthenticationException)
            {
                // The client closed the data connection early (end of its segment).
            }
            finally
            {
                if (stream is SslStream ssl)
                {
                    try
                    {
                        await ssl.ShutdownAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                    {
                    }
                }

                await stream.DisposeAsync().ConfigureAwait(false);
            }

            await ReplyAsync(complete ? "226 Transfer complete." : "426 Connection closed; transfer aborted.").ConfigureAwait(false);
        }

        private static async Task SendAsync(TestFile file, long start, Stream stream)
        {
            var buffer = new byte[16 * 1024];
            var size = file.Size;
            var position = start;
            var rate = file.BytesPerSecond;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (position < size)
            {
                var count = (int)Math.Min(buffer.Length, size - position);
                if (rate > 0)
                {
                    count = Math.Min(count, Math.Max(512, rate / 20));
                }

                ContentGenerator.Fill(file.Seed, position, buffer.AsSpan(0, count));
                await stream.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                position += count;
                if (rate > 0)
                {
                    var due = TimeSpan.FromSeconds((double)(position - start) / rate) - clock.Elapsed;
                    if (due > TimeSpan.Zero)
                    {
                        await Task.Delay(due).ConfigureAwait(false);
                    }
                }
            }

            await stream.FlushAsync().ConfigureAwait(false);
        }
    }
}
