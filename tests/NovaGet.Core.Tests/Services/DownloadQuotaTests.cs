using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Http;
using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Services;
using NovaGet.Core.Services.Queues;
using NovaGet.Core.Settings;
using NovaGet.Core.Tests.Engine;
using NovaGet.Core.Tests.Infrastructure;
using NovaGet.Data.Repositories;

namespace NovaGet.Core.Tests.Services;

public sealed class DownloadQuotaTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
    private EngineHarness _h = null!;
    private SettingsService _settings = null!;
    private DownloadService _service = null!;
    private QueueManager _queues = null!;

    public async Task InitializeAsync()
    {
        _h = await EngineHarness.CreateAsync(o => o with { MaxConnections = 2 });
        var paths = AppPaths.ForRoot(_temp.Path);
        _settings = new SettingsService(paths);
        _settings.Update(s =>
        {
            s.Connection.DownloadLimit.Enabled = true;
            s.Connection.DownloadLimit.MaxMegabytes = 1;
            s.Connection.DownloadLimit.PeriodHours = 2;
        });
        _service = new DownloadService(_h.Repository, new CategoryRepository(_h.Database), _h.Engine, _settings, paths);
        _queues = new QueueManager(_service, new QueueRepository(_h.Database),
            new DownloadProber([new HttpTransferProtocol(new HttpClientProvider())]), () => _h.Options, _clock);
    }

    public async Task DisposeAsync()
    {
        _queues.Dispose();
        await _h.DisposeAsync();
        _temp.Dispose();
    }

    private DownloadQuotaService CreateQuota() =>
        new(_settings, _h.Engine, _service, _queues, Path.Combine(_temp.Path, "quota.json"), _clock);

    private Download AddSlow(string path, long size)
    {
        var file = _h.Server.AddFile(path, size, seed: size.GetHashCode());
        file.BytesPerSecond = 1_000_000;
        return _service.Add(new DownloadRequest { Url = _h.Server.UrlFor(file).AbsoluteUri, FileName = Path.GetFileName(path), SaveFolder = _h.SaveDirectory });
    }

    /// <summary>Ticks the quota until <paramref name="condition"/> holds.</summary>
    private static async Task TickUntilAsync(DownloadQuotaService quota, Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }

            await quota.TickAsync();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Reaching_the_limit_stops_everything_until_the_period_ends()
    {
        using var quota = CreateQuota();
        QuotaEventArgs? reached = null;
        quota.LimitReached += (_, e) => reached = e;
        var big = AddSlow("/quota/big.bin", 4_000_000);
        Assert.True(_service.Start(big.Id));

        await TickUntilAsync(quota, () => quota.IsBlocked, "limit reached");

        Assert.NotNull(reached);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromHours(2), reached!.ResumeAt);
        await EngineHarness.WaitUntilAsync(() => _service.Find(big.Id)!.Status == DownloadStatus.Paused, because: "paused by the limit");
        Assert.True(quota.UsedBytes >= 1024 * 1024);

        // Starting something else while blocked is undone.
        var refused = 0;
        quota.StartRefused += (_, _) => Interlocked.Increment(ref refused);
        var other = AddSlow("/quota/other.bin", 2_000_000);
        _service.Start(other.Id);
        await EngineHarness.WaitUntilAsync(() => Volatile.Read(ref refused) > 0 && _service.Find(other.Id)!.Status == DownloadStatus.Paused, because: "refused");

        // The period ends: both continue and finish.
        var resumed = false;
        quota.Resumed += (_, _) => resumed = true;
        _clock.Advance(TimeSpan.FromHours(2));
        _settings.Update(s => s.Connection.DownloadLimit.MaxMegabytes = 100);
        await quota.TickAsync();

        Assert.True(resumed);
        Assert.False(quota.IsBlocked);
        await EngineHarness.WaitUntilAsync(
            () => _service.Find(big.Id)!.Status == DownloadStatus.Completed && _service.Find(other.Id)!.Status == DownloadStatus.Completed,
            TimeSpan.FromSeconds(30), "both finished after the period");
    }

    [Fact]
    public async Task Running_queues_stop_without_finishing_and_start_again()
    {
        using var quota = CreateQuota();
        var queued = AddSlow("/quota/queued.bin", 3_000_000);
        _service.SetQueue([queued.Id], DownloadQueue.MainQueueId);
        var stopReasons = new List<QueueStopReason?>();
        _queues.QueueStopped += (_, e) => stopReasons.Add(e.Reason);
        _queues.Start(DownloadQueue.MainQueueId);

        await TickUntilAsync(quota, () => quota.IsBlocked, "limit reached");
        await EngineHarness.WaitUntilAsync(() => !_queues.IsRunning(DownloadQueue.MainQueueId), because: "queue stopped");
        Assert.Equal([QueueStopReason.Stopped], stopReasons);

        _clock.Advance(TimeSpan.FromHours(3));
        _settings.Update(s => s.Connection.DownloadLimit.Enabled = false);
        await quota.TickAsync();

        Assert.True(_queues.IsRunning(DownloadQueue.MainQueueId) || _service.Find(queued.Id)!.Status == DownloadStatus.Completed);
        await EngineHarness.WaitUntilAsync(() => _service.Find(queued.Id)!.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(30), "queue finished it");
    }

    [Fact]
    public async Task The_warning_can_let_the_period_continue()
    {
        _settings.Update(s => s.Connection.DownloadLimit.WarnBeforeStopping = true);
        using var quota = CreateQuota();
        var asked = 0;
        quota.ConfirmStop = _ =>
        {
            asked++;
            return false;
        };
        var big = AddSlow("/quota/warned.bin", 2_000_000);
        _service.Start(big.Id);

        await EngineHarness.WaitUntilAsync(() => _service.Find(big.Id)!.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(30), "finished");
        for (var i = 0; i < 3; i++)
        {
            await quota.TickAsync();
        }

        Assert.Equal(1, asked);
        Assert.False(quota.IsBlocked);
    }

    [Fact]
    public async Task Blocking_survives_a_restart()
    {
        using (var quota = CreateQuota())
        {
            var big = AddSlow("/quota/restart.bin", 3_000_000);
            _service.Start(big.Id);
            await TickUntilAsync(quota, () => quota.IsBlocked, "limit reached");
        }

        using var reloaded = CreateQuota();
        Assert.True(reloaded.IsBlocked);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromHours(2), reloaded.ResumeAt);
    }

    [Fact]
    public async Task Nothing_happens_when_limits_are_off()
    {
        _settings.Update(s => s.Connection.DownloadLimit.Enabled = false);
        using var quota = CreateQuota();
        var big = AddSlow("/quota/free.bin", 2_000_000);
        _service.Start(big.Id);

        await EngineHarness.WaitUntilAsync(() => _service.Find(big.Id)!.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(30), "finished");
        await quota.TickAsync();

        Assert.False(quota.IsBlocked);
        Assert.Equal(0, quota.UsedBytes);
    }
}
