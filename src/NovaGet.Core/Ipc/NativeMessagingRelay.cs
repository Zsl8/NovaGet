using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NovaGet.Core.Ipc;

/// <summary>
/// The native messaging host's main loop: reads framed JSON from the browser (stdin), forwards each
/// message to the app as an <see cref="IpcRequestTypes.Native"/> request and writes the reply back (stdout).
/// The pipe connection is kept open and re-established if the app restarts.
/// </summary>
public sealed class NativeMessagingRelay
{
    /// <summary>Chrome refuses host-to-browser messages larger than 1 MB.</summary>
    public const int MaxOutgoingBytes = 1024 * 1024;

    /// <summary>Chrome allows browser-to-host messages up to 64 MiB; we accept up to 64 MiB as well.</summary>
    public const int MaxIncomingBytes = 64 * 1024 * 1024;

    private readonly Func<CancellationToken, Task<PipeClient?>> _connect;
    private readonly ILogger _logger;
    private readonly string? _browser;

    /// <param name="connect">Connects to the app, starting it if needed; returns null when it can't be reached.</param>
    /// <param name="browser">The browser that started the host (chrome, edge, firefox, …), passed on with every message.</param>
    public NativeMessagingRelay(Func<CancellationToken, Task<PipeClient?>> connect, ILogger? logger = null, string? browser = null)
    {
        _connect = connect;
        _logger = logger ?? NullLogger.Instance;
        _browser = browser;
    }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        PipeClient? client = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[]? message;
                try
                {
                    message = await MessageFraming.ReadAsync(input, MaxIncomingBytes, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    _logger.LogWarning(ex, "Invalid frame from the browser; stopping");
                    return;
                }

                if (message is null)
                {
                    return; // The browser closed stdin: the port was disconnected.
                }

                IpcResponse response;
                JsonElement payload;
                try
                {
                    using var document = JsonDocument.Parse(message);
                    payload = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    await WriteAsync(output, IpcResponse.Failure("Invalid JSON."), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                (response, client) = await ForwardAsync(client, payload, cancellationToken).ConfigureAwait(false);
                await WriteAsync(output, response, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<(IpcResponse Response, PipeClient? Client)> ForwardAsync(PipeClient? client, JsonElement payload, CancellationToken cancellationToken)
    {
        var request = new IpcRequest { Type = IpcRequestTypes.Native, Payload = payload, Args = _browser is null ? null : [_browser] };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            client ??= await _connect(cancellationToken).ConfigureAwait(false);
            if (client is null)
            {
                return (IpcResponse.Failure("NovaGet is not running."), null);
            }

            try
            {
                return (await client.SendAsync(request, cancellationToken).ConfigureAwait(false), client);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
            {
                // The app went away (e.g. restarted); reconnect once.
                _logger.LogInformation(ex, "Lost the connection to NovaGet; reconnecting");
                await client.DisposeAsync().ConfigureAwait(false);
                client = null;
            }
        }

        return (IpcResponse.Failure("NovaGet is not responding."), null);
    }

    private static async Task WriteAsync(Stream output, IpcResponse response, CancellationToken cancellationToken)
    {
        var bytes = IpcJson.Serialize(response);
        if (bytes.Length > MaxOutgoingBytes)
        {
            bytes = IpcJson.Serialize(IpcResponse.Failure("Response too large."));
        }

        await MessageFraming.WriteAsync(output, bytes, cancellationToken).ConfigureAwait(false);
    }
}
