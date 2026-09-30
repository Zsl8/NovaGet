using System.IO.Pipes;

namespace NovaGet.Core.Ipc;

/// <summary>Client side of the app pipe. One connection can carry many request/response pairs.</summary>
public sealed class PipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private PipeClient(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>Connects to the server; throws <see cref="TimeoutException"/> if it isn't listening in time.</summary>
    public static async Task<PipeClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue), cancellationToken).ConfigureAwait(false);
            return new PipeClient(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Tries to connect repeatedly until <paramref name="totalTimeout"/> elapses (the app may still be starting).</summary>
    public static async Task<PipeClient?> TryConnectWithRetryAsync(string pipeName, TimeSpan totalTimeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + totalTimeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            try
            {
                return await ConnectAsync(pipeName, remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MessageFraming.WriteAsync(_pipe, IpcJson.Serialize(request), cancellationToken).ConfigureAwait(false);
            var body = await MessageFraming.ReadAsync(_pipe, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The app closed the connection.");
            return IpcJson.Deserialize<IpcResponse>(body) ?? IpcResponse.Failure("Empty response.");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>One-shot helper: connect, send one request, disconnect.</summary>
    public static async Task<IpcResponse?> SendOnceAsync(string pipeName, IpcRequest request, TimeSpan connectTimeout, CancellationToken cancellationToken = default)
    {
        var client = await TryConnectWithRetryAsync(pipeName, connectTimeout, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return null;
        }

        await using (client.ConfigureAwait(false))
        {
            return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _lock.Dispose();
    }
}
