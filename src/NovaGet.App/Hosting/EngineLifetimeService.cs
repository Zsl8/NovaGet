using Microsoft.Extensions.Hosting;
using NovaGet.Core.Engine;

namespace NovaGet.App.Hosting;

/// <summary>Marks downloads interrupted by a crash as paused at startup and saves progress on exit.</summary>
internal sealed class EngineLifetimeService(DownloadEngine engine) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        engine.RecoverInterrupted();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await engine.DisposeAsync().ConfigureAwait(false);
}
