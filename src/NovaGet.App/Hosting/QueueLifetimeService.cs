using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NovaGet.Core.Services.Queues;

namespace NovaGet.App.Hosting;

/// <summary>The scheduler clock: ticks the queue manager every 15 seconds (section 10).</summary>
internal sealed class QueueLifetimeService(IQueueManager queues, ILogger<QueueLifetimeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(QueueManager.TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    queues.Tick();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Scheduler tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
