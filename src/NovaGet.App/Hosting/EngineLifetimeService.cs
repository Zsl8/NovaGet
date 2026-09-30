using Microsoft.Extensions.Hosting;
using NovaGet.Core.Engine;
using NovaGet.Core.Settings;

namespace NovaGet.App.Hosting;

/// <summary>
/// Marks downloads interrupted by a crash as paused at startup, keeps the global speed limiter in sync with
/// settings, and saves progress on exit.
/// </summary>
internal sealed class EngineLifetimeService(DownloadEngine engine, ISettingsService settings) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        engine.RecoverInterrupted();
        ApplySpeedLimiter();
        settings.Changed += OnSettingsChanged;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnSettingsChanged;
        await engine.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplySpeedLimiter();

    private void ApplySpeedLimiter()
    {
        var limiter = settings.Current.SpeedLimiter;
        engine.SpeedLimits.SetGlobal(limiter.Enabled ? limiter.MaxKBps : null, limiter.ApplyToSchedulerQueuesOnly);
    }
}
