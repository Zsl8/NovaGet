using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NovaGet.Core.Ipc;

/// <summary>
/// Named pipe server for <c>\\.\pipe\NovaGet.Main</c>. Each connection may send any number of
/// framed JSON requests; each gets exactly one framed response. The pipe is created with
/// <see cref="PipeOptions.CurrentUserOnly"/>, which on Windows sets an ACL for the current user only.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly IIpcRequestHandler _handler;
    private readonly ILogger _logger;
    private readonly int _maxMessageBytes;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = [];
    private readonly object _gate = new();
    private Task? _acceptLoop;

    public PipeServer(string pipeName, IIpcRequestHandler handler, ILogger<PipeServer>? logger = null, int maxMessageBytes = MessageFraming.DefaultMaxMessageBytes)
    {
        _pipeName = pipeName;
        _handler = handler;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _maxMessageBytes = maxMessageBytes;
    }

    /// <summary>Starts accepting clients. Returns once the first pipe instance exists, so clients can connect immediately.</summary>
    public void Start()
    {
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("The pipe server is already running.");
        }

        var first = CreatePipe();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(first, _cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] pending;
        lock (_gate)
        {
            pending = [.. _connections];
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private NamedPipeServerStream CreatePipe() => new(
        _pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task AcceptLoopAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Pipe accept failed; recreating the pipe");
                await pipe.DisposeAsync().ConfigureAwait(false);
                pipe = CreatePipe();
                continue;
            }

            // Create the next listening instance before serving this one. On Unix all instances share one
            // listening socket that closes when the last instance is disposed; if the served connection finished
            // first, clients waiting in the backlog would be reset.
            var connected = pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Could not create another pipe instance; IPC stopped after this client");
                await ServeAsync(connected, cancellationToken).ConfigureAwait(false);
                return;
            }

            var task = Task.Run(() => ServeAsync(connected, cancellationToken), CancellationToken.None);
            lock (_gate)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(task);
            }
        }

        await pipe.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var body = await MessageFraming.ReadAsync(pipe, _maxMessageBytes, cancellationToken).ConfigureAwait(false);
                    if (body is null)
                    {
                        return;
                    }

                    IpcResponse response;
                    IpcRequest? request = null;
                    try
                    {
                        request = IpcJson.DeserializeRequest(body);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Malformed IPC request");
                    }

                    if (request is null || string.IsNullOrWhiteSpace(request.Type))
                    {
                        response = IpcResponse.Failure("Malformed request.");
                    }
                    else
                    {
                        try
                        {
                            response = await _handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogError(ex, "IPC handler failed for {Type}", request.Type);
                            response = IpcResponse.Failure("Internal error.");
                        }
                    }

                    await MessageFraming.WriteAsync(pipe, IpcJson.Serialize(response), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "IPC client sent an invalid frame; closing the connection");
            }
            catch (IOException)
            {
                // Client went away mid-conversation.
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
