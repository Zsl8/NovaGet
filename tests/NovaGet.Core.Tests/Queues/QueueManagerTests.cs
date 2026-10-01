using System.Collections.Concurrent;
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
using NovaGet.TestServer;

namespace NovaGet.Core.Tests.Queues;

public sealed class QueueManagerTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 5, 1, 59, 50, TimeSpan.Zero)); // a Monday
    private readonly ConcurrentQueue<QueueEventArgs> _stopped = new();
    private readonly ConcurrentQueue<QueueEventArgs> _started = new();
    private EngineHarness _h = null!;
    private DownloadService _service = null!;
    private QueueRepository _queues = null!;
    private QueueManager _manager = null!;

    public async Task InitializeAsync()
    {
        _h = await EngineHarness.CreateAsync(o => o with { MaxConnections = 2 });
        var paths = AppPaths.ForRoot(_temp.Path);
        _service = new DownloadService(_h.Repository, new CategoryRepository(_h.Database), _h.Engine, new SettingsService(paths), paths);
        _queues = new QueueRepository(_h.Database);
        var prober = new DownloadProber([new HttpTransferProtocol(new HttpClientProvider())]);
        _manager = new QueueManager(_service, _queues, prober, () => _h.Options, _clock) { RetryDelay = TimeSpan.Zero };
        _manager.QueueStopped += (_, e) => _stopped.Enqueue(e);
        _manager.QueueStarted += (_, e) => _started.Enqueue(e);
    }

    public async Task DisposeAsync()
    {
        _manager.Dispose();
        await _h.DisposeAsync();
        _temp.Dispose();
    }

    private Download AddToQueue(TestFile file, long queueId = DownloadQueue.MainQueueId) => _service.Add(new DownloadRequest
    {
        Url = _h.Server.UrlFor(file).AbsoluteUri,
        FileName = Path.GetFileName(file.Path),
        SaveFolder = _h.SaveDirectory,
        QueueId = queueId,
    });

    private Download AddToQueue(string path, long queueId = DownloadQueue.MainQueueId) => _service.Add(new DownloadRequest
    {
        Url = _h.Server.UrlFor(path).AbsoluteUri,
        FileName = Path.GetFileName(path),
        SaveFolder = _h.SaveDirectory,
        QueueId = queueId,
    });

    private void Configure(long queueId, Action<DownloadQueue> change)
    {
        var queue = _queues.Get(queueId)!;
        change(queue);
        _queues.Update(queue);
    }

    /// <summary>
    /// Samples how many downloads are active at once. Polls the list (which is updated before events are raised);
    /// counting events would see one download's "connecting" before another's "completed" handler ran.
    /// </summary>
    private CancellationTokenSource TrackPeak(out Func<int> peak)
    {
        var max = 0;
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var active = _service.GetAll().Count(d => d.Status.IsActive());
                if (active > Volatile.Read(ref max))
                {
                    Volatile.Write(ref max, active);
                }

                await Task.Delay(2);
            }
        });
        peak = () => Volatile.Read(ref max);
        return cts;
    }

    [Fact]
    public async Task Runs_files_one_at_a_time_in_queue_order_and_finishes()
    {
        var files = Enumerable.Range(1, 3).Select(i =>
        {
            var f = _h.Server.AddFile($"/q/file{i}.bin", 150_000, seed: i);
            f.BytesPerSecond = 1_000_000;
            return f;
        }).ToList();
        var added = files.Select(f => AddToQueue(f)).ToList();
        var completedOrder = new ConcurrentQueue<long>();
        _service.StateChanged += (_, e) =>
        {
            if (e.Status == DownloadStatus.Completed)
            {
                completedOrder.Enqueue(e.Id);
            }
        };
        using var tracking = TrackPeak(out var peak);

        Assert.True(_manager.Start(DownloadQueue.MainQueueId));
        Assert.False(_manager.Start(DownloadQueue.MainQueueId)); // already running
        await EngineHarness.WaitUntilAsync(() => !_stopped.IsEmpty, TimeSpan.FromSeconds(30), "queue finished");

        var stop = Assert.Single(_stopped);
        Assert.Equal(QueueStopReason.Finished, stop.Reason);
        Assert.Single(_started);
        Assert.Equal(added.Select(d => d.Id), completedOrder);
        Assert.Equal(1, peak());
        Assert.All(added, d => Assert.Equal(DownloadStatus.Completed, _service.Find(d.Id)!.Status));
        Assert.False(_manager.IsRunning(DownloadQueue.MainQueueId));
    }

    [Fact]
    public async Task Runs_the_configured_number_of_files_at_once()
    {
        Configure(DownloadQueue.MainQueueId, q => q.SimultaneousCount = 2);
        var added = Enumerable.Range(1, 4).Select(i =>
        {
            var f = _h.Server.AddFile($"/q/par{i}.bin", 200_000, seed: i);
            f.BytesPerSecond = 500_000;
            return AddToQueue(f);
        }).ToList();
        using var tracking = TrackPeak(out var peak);

        _manager.Start(DownloadQueue.MainQueueId);
        await EngineHarness.WaitUntilAsync(() => !_stopped.IsEmpty, TimeSpan.FromSeconds(30), "queue finished");

        Assert.Equal(2, peak());
        Assert.All(added, d => Assert.Equal(DownloadStatus.Completed, _service.Find(d.Id)!.Status));
    }

    [Fact]
    public async Task A_failing_file_is_retried_then_skipped()
    {
        Configure(DownloadQueue.MainQueueId, q => q.Schedule.RetriesPerFile = 2);
        var missing = AddToQueue("/q/missing.bin");
        var good = AddToQueue(_h.Server.AddFile("/q/good.bin", 50_000));
        var errors = 0;
        _service.StateChanged += (_, e) =>
        {
            if (e.Id == missing.Id && e.Status == DownloadStatus.Error)
            {
                Interlocked.Increment(ref errors);
            }
        };

        _manager.Start(DownloadQueue.MainQueueId);
        await EngineHarness.WaitUntilAsync(() => !_stopped.IsEmpty, TimeSpan.FromSeconds(30), "queue finished");

        Assert.Equal(3, Volatile.Read(ref errors)); // first try + 2 retries
        Assert.Equal(QueueStopReason.Finished, Assert.Single(_stopped).Reason);
        Assert.Equal(DownloadStatus.Error, _service.Find(missing.Id)!.Status);
        Assert.Equal(DownloadStatus.Completed, _service.Find(good.Id)!.Status);
    }

    [Fact]
    public async Task Stop_queue_stops_its_downloads()
    {
        var slow = _h.Server.AddFile("/q/slow.bin", 5_000_000);
        slow.BytesPerSecond = 100_000;
        var added = AddToQueue(slow);

        _manager.Start(DownloadQueue.MainQueueId);
        await EngineHarness.WaitUntilAsync(() => _service.Find(added.Id)!.Status == DownloadStatus.Receiving, because: "receiving");
        await _manager.StopAsync(DownloadQueue.MainQueueId);

        Assert.Equal(DownloadStatus.Paused, _service.Find(added.Id)!.Status);
        Assert.Equal(QueueStopReason.Stopped, Assert.Single(_stopped).Reason);
        Assert.False(_manager.IsRunning(DownloadQueue.MainQueueId));
    }

    [Fact]
    public async Task Stopping_a_file_by_hand_moves_on_and_skips_the_finish_actions()
    {
        var slow = _h.Server.AddFile("/q/slow.bin", 5_000_000);
        slow.BytesPerSecond = 100_000;
        var first = AddToQueue(slow);
        var second = AddToQueue(_h.Server.AddFile("/q/next.bin", 50_000));

        _manager.Start(DownloadQueue.MainQueueId);
        await EngineHarness.WaitUntilAsync(() => _service.Find(first.Id)!.Status == DownloadStatus.Receiving, because: "receiving");
        await _service.StopAsync(first.Id);
        await EngineHarness.WaitUntilAsync(() => !_stopped.IsEmpty, TimeSpan.FromSeconds(30), "queue ended");

        Assert.Equal(DownloadStatus.Completed, _service.Find(second.Id)!.Status);
        Assert.Equal(DownloadStatus.Paused, _service.Find(first.Id)!.Status);
        Assert.Equal(QueueStopReason.Stopped, Assert.Single(_stopped).Reason);
    }

    [Fact]
    public async Task Files_added_to_a_running_queue_start()
    {
        Configure(DownloadQueue.MainQueueId, q => q.SimultaneousCount = 2);
        var slow = _h.Server.AddFile("/q/slow.bin", 5_000_000);
        slow.BytesPerSecond = 100_000;
        var first = AddToQueue(slow);
        _manager.Start(DownloadQueue.MainQueueId);
        await EngineHarness.WaitUntilAsync(() => _service.Find(first.Id)!.Status == DownloadStatus.Receiving, because: "receiving");

        var late = AddToQueue(_h.Server.AddFile("/q/late.bin", 50_000));

        await EngineHarness.WaitUntilAsync(() => _service.Find(late.Id)!.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(20), "late file done");
        await _manager.StopAsync(DownloadQueue.MainQueueId);
    }

    [Fact]
    public async Task Schedule_starts_and_stops_the_queue()
    {
        Configure(DownloadQueue.MainQueueId, q =>
        {
            q.Schedule.Mode = ScheduleMode.Daily;
            q.Schedule.Days = [.. Enum.GetValues<DayOfWeek>()];
            q.Schedule.StartEnabled = true;
            q.Schedule.StartTime = new TimeSpan(2, 0, 0);
            q.Schedule.StopEnabled = true;
            q.Schedule.StopTime = new TimeSpan(2, 30, 0);
        });
        var slow = _h.Server.AddFile("/q/slow.bin", 5_000_000);
        slow.BytesPerSecond = 100_000;
        var added = AddToQueue(slow);

        _clock.Advance(TimeSpan.FromSeconds(5)); // 01:59:55
        _manager.Tick();
        Assert.False(_manager.IsRunning(DownloadQueue.MainQueueId));

        _clock.Advance(TimeSpan.FromSeconds(15)); // 02:00:10
        _manager.Tick();
        Assert.True(_manager.IsRunning(DownloadQueue.MainQueueId));
        await EngineHarness.WaitUntilAsync(() => _service.Find(added.Id)!.Status.IsActive(), because: "started by the schedule");

        _clock.Advance(TimeSpan.FromMinutes(30)); // 02:30:10
        _manager.Tick();
        await EngineHarness.WaitUntilAsync(() => !_stopped.IsEmpty, because: "stopped by the schedule");
        Assert.Equal(QueueStopReason.StopTime, Assert.Single(_stopped).Reason);
        Assert.Equal(DownloadStatus.Paused, _service.Find(added.Id)!.Status);
    }

    [Fact]
    public void Schedule_does_not_start_on_other_days_or_late()
    {
        Configure(DownloadQueue.MainQueueId, q =>
        {
            q.Schedule.StartEnabled = true;
            q.Schedule.StartTime = new TimeSpan(2, 0, 0);
            q.Schedule.Days = [DayOfWeek.Tuesday]; // today is Monday
        });
        AddToQueue(_h.Server.AddFile("/q/a.bin", 1000));

        _clock.Advance(TimeSpan.FromSeconds(20));
        _manager.Tick();
        Assert.False(_manager.IsRunning(DownloadQueue.MainQueueId));

        // Tuesday 02:00, but the PC was asleep until 02:10: too late.
        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(10));
        _manager.Tick();
        Assert.False(_manager.IsRunning(DownloadQueue.MainQueueId));
    }

    [Fact]
    public async Task Queues_start_by_name_and_on_app_start()
    {
        var night = _queues.Insert(new DownloadQueue { Name = "Night", Schedule = new QueueSchedule { StartOnAppStartup = true } });
        AddToQueue(_h.Server.AddFile("/q/n.bin", 1000), night);

        Assert.False(_manager.Start("no such queue"));
        _manager.OnAppStarted();
        await EngineHarness.WaitUntilAsync(() => _stopped.Any(e => e.Queue.Id == night), because: "night queue finished");
        Assert.False(_manager.IsRunning(DownloadQueue.MainQueueId));

        Assert.True(_manager.Start("NIGHT"));
        Assert.True(_manager.Start("main"));
    }

    [Fact]
    public async Task Synchronization_queue_downloads_changed_files_again()
    {
        Configure(DownloadQueue.SyncQueueId, q => q.Schedule.SyncKeepBackup = true);
        var file = _h.Server.AddFile("/sync/report.bin", 64_000, seed: 1);
        var unchanged = _h.Server.AddFile("/sync/same.bin", 32_000, seed: 5);
        var added = AddToQueue(file, DownloadQueue.SyncQueueId);
        var same = AddToQueue(unchanged, DownloadQueue.SyncQueueId);

        // Starting the synchronization queue downloads what isn't there yet; it then stays running.
        _manager.Start(DownloadQueue.SyncQueueId);
        await EngineHarness.WaitUntilAsync(
            () => _service.Find(added.Id)!.Status == DownloadStatus.Completed && _service.Find(same.Id)!.Status == DownloadStatus.Completed,
            because: "first download");
        Assert.True(_manager.IsRunning(DownloadQueue.SyncQueueId));
        var path = _service.Find(added.Id)!.FullPath;
        var original = await File.ReadAllBytesAsync(path);
        var sameWritten = File.GetLastWriteTimeUtc(_service.Find(same.Id)!.FullPath);

        file.ChangeContent(newSeed: 2);
        var changed = await _manager.SynchronizeAsync(_queues.Get(DownloadQueue.SyncQueueId)!);
        Assert.Equal(1, changed);
        await EngineHarness.WaitUntilAsync(() => _service.Find(added.Id)!.Status == DownloadStatus.Completed, because: "downloaded again");

        var current = _service.Find(added.Id)!;
        Assert.Equal(path, current.FullPath); // replaced, not "report (2).bin"
        Assert.Equal(file.Content(), await File.ReadAllBytesAsync(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(path + ".bak"));
        Assert.Equal(sameWritten, File.GetLastWriteTimeUtc(_service.Find(same.Id)!.FullPath));

        // Nothing changed since: nothing to do.
        Assert.Equal(0, await _manager.SynchronizeAsync(_queues.Get(DownloadQueue.SyncQueueId)!));
        await _manager.StopAsync(DownloadQueue.SyncQueueId);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
