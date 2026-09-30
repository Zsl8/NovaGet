using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NovaGet.Core;
using NovaGet.Core.Ipc;

namespace NovaGet.App.Hosting;

/// <summary>Runs the <c>\\.\pipe\NovaGet.Main</c> server for the lifetime of the app.</summary>
internal sealed class IpcServerHostedService(IIpcRequestHandler handler, ILogger<PipeServer> pipeLogger, ILogger<IpcServerHostedService> logger)
    : IHostedService, IAsyncDisposable
{
    private PipeServer? _server;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _server = new PipeServer(AppInfo.PipeName, handler, pipeLogger);
            _server.Start();
            logger.LogInformation("IPC server listening on pipe {Pipe}", AppInfo.PipeName);
        }
        catch (IOException ex)
        {
            // Another user's session may already own the pipe name; the app still works, only forwarding is lost.
            logger.LogError(ex, "Could not start the IPC server");
            _server = null;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
