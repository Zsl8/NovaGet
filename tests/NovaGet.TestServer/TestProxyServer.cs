using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NovaGet.TestServer;

public enum TestProxyKind
{
    /// <summary>An HTTP proxy: CONNECT tunnels (for https) and absolute-form requests (for http).</summary>
    Http,

    /// <summary>A SOCKS5 proxy (RFC 1928), optionally asking for a user name and password (RFC 1929).</summary>
    Socks5,
}

/// <summary>A small proxy on 127.0.0.1 that relays connections and records where they went.</summary>
public sealed class TestProxyServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _targets = new();
    private readonly List<Task> _sessions = [];
    private readonly Task _acceptLoop;

    private TestProxyServer(TestProxyKind kind, (string User, string Password)? login)
    {
        Kind = kind;
        Login = login;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public TestProxyKind Kind { get; }

    public (string User, string Password)? Login { get; }

    public int Port { get; }

    /// <summary>One entry per relayed connection: "CONNECT host:port", "GET host:port" or "SOCKS host:port".</summary>
    public IReadOnlyCollection<string> Targets => _targets;

    public static TestProxyServer Start(TestProxyKind kind, (string User, string Password)? login = null) => new(kind, login);

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
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                if (Kind == TestProxyKind.Socks5)
                {
                    await SocksAsync(stream).ConfigureAwait(false);
                }
                else
                {
                    await HttpAsync(stream).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or FormatException)
        {
        }
    }

    private async Task HttpAsync(NetworkStream client)
    {
        var head = await ReadHeadAsync(client).ConfigureAwait(false);
        var requestLine = head[..head.IndexOf("\r\n", StringComparison.Ordinal)];
        var parts = requestLine.Split(' ');
        if (parts[0] == "CONNECT")
        {
            var (host, port) = SplitHostPort(parts[1]);
            _targets.Enqueue($"CONNECT {host}:{port}");
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(host, port, _stop.Token).ConfigureAwait(false);
            await client.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), _stop.Token).ConfigureAwait(false);
            await RelayAsync(client, upstream.GetStream()).ConfigureAwait(false);
        }
        else
        {
            // Absolute-form request: forward it as is (the test server accepts absolute-form targets).
            var target = new Uri(parts[1]);
            _targets.Enqueue($"{parts[0]} {target.Host}:{target.Port}");
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(target.Host, target.Port, _stop.Token).ConfigureAwait(false);
            var server = upstream.GetStream();
            await server.WriteAsync(Encoding.Latin1.GetBytes(head), _stop.Token).ConfigureAwait(false);
            await RelayAsync(client, server).ConfigureAwait(false);
        }
    }

    private async Task SocksAsync(NetworkStream client)
    {
        // Greeting: VER, NMETHODS, METHODS.
        var greeting = await ReadExactAsync(client, 2).ConfigureAwait(false);
        var methods = await ReadExactAsync(client, greeting[1]).ConfigureAwait(false);
        var wanted = Login is null ? (byte)0x00 : (byte)0x02;
        if (greeting[0] != 5 || Array.IndexOf(methods, wanted) < 0)
        {
            await client.WriteAsync(new byte[] { 5, 0xFF }, _stop.Token).ConfigureAwait(false);
            return;
        }

        await client.WriteAsync(new byte[] { 5, wanted }, _stop.Token).ConfigureAwait(false);
        if (Login is { } login)
        {
            var version = await ReadExactAsync(client, 2).ConfigureAwait(false);
            var user = Encoding.UTF8.GetString(await ReadExactAsync(client, version[1]).ConfigureAwait(false));
            var passwordLength = (await ReadExactAsync(client, 1).ConfigureAwait(false))[0];
            var password = Encoding.UTF8.GetString(await ReadExactAsync(client, passwordLength).ConfigureAwait(false));
            var ok = user == login.User && password == login.Password;
            await client.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 }, _stop.Token).ConfigureAwait(false);
            if (!ok)
            {
                return;
            }
        }

        // Request: VER, CMD (1 = CONNECT), RSV, ATYP, address, port.
        var request = await ReadExactAsync(client, 4).ConfigureAwait(false);
        string host = request[3] switch
        {
            1 => new IPAddress(await ReadExactAsync(client, 4).ConfigureAwait(false)).ToString(),
            3 => Encoding.ASCII.GetString(await ReadExactAsync(client, (await ReadExactAsync(client, 1).ConfigureAwait(false))[0]).ConfigureAwait(false)),
            4 => new IPAddress(await ReadExactAsync(client, 16).ConfigureAwait(false)).ToString(),
            _ => throw new FormatException("Unknown SOCKS address type."),
        };
        var port = BinaryPrimitives.ReadUInt16BigEndian(await ReadExactAsync(client, 2).ConfigureAwait(false));
        if (request[1] != 1)
        {
            await client.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }, _stop.Token).ConfigureAwait(false);
            return;
        }

        _targets.Enqueue($"SOCKS {host}:{port}");
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(host, port, _stop.Token).ConfigureAwait(false);
        await client.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, _stop.Token).ConfigureAwait(false);
        await RelayAsync(client, upstream.GetStream()).ConfigureAwait(false);
    }

    private async Task RelayAsync(Stream a, Stream b)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var up = CopyAsync(a, b, done.Token);
        var down = CopyAsync(b, a, done.Token);
        await Task.WhenAny(up, down).ConfigureAwait(false);
        await done.CancelAsync().ConfigureAwait(false);
    }

    private static async Task CopyAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        try
        {
            await from.CopyToAsync(to, 64 * 1024, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task<string> ReadHeadAsync(Stream stream)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        while (buffer.Count < 64 * 1024)
        {
            if (await stream.ReadAsync(one, _stop.Token).ConfigureAwait(false) == 0)
            {
                throw new IOException("Connection closed before the request head ended.");
            }

            buffer.Add(one[0]);
            var n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
            {
                return Encoding.Latin1.GetString(buffer.ToArray());
            }
        }

        throw new FormatException("Request head too long.");
    }

    private async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        var data = new byte[count];
        await stream.ReadExactlyAsync(data, _stop.Token).ConfigureAwait(false);
        return data;
    }

    private static (string Host, int Port) SplitHostPort(string authority)
    {
        var colon = authority.LastIndexOf(':');
        var host = authority[..colon].Trim('[', ']');
        return (host, int.Parse(authority[(colon + 1)..], CultureInfo.InvariantCulture));
    }
}
