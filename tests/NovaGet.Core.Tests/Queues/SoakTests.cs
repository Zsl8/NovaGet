using System.Collections.Concurrent;
using System.Diagnostics;
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
using Xunit.Abstractions;

namespace NovaGet.Core.Tests.Queues;

/// <summary>The soak runs alone, so its load doesn't disturb the timing-sensitive engine tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SoakCollection
{
    public const string Name = "Soak";
}

/// <summary>
/// Section 19: 200 queued downloads, 10 at once, run over and over; memory must not grow by more than 50 MB after
/// the first round. CI runs it for 45 seconds; NOVAGET_SOAK=120 runs the full two hours.
/// </summary>
[Collection(SoakCollection.Name)]
public sealed class SoakTests(ITestOutputHelper output)
{
    private const int Files = 200;
    private const int AtOnce = 10;
    private const long MaxGrowth = 50L * 1024 * 1024;

    private static TimeSpan Duration =>
        int.TryParse(Environment.GetEnvironmentVariable("NOVAGET_SOAK"), out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromSeconds(45);

    [Fact]
    public async Task Two_hundred_queued_ten_at_once_stay_stable()
    {
        using var temp = new TempDirectory();
        await using var h = await EngineHarness.CreateAsync(o => o with { MaxConnections = 2, CheckpointInterval = TimeSpan.FromSeconds(1) });
        var paths = AppPaths.ForRoot(temp.Path);
        var service = new DownloadService(h.Repository, new CategoryRepository(h.Database), h.Engine, new SettingsService(paths), paths);
        var queues = new QueueRepository(h.Database);
        var main = queues.Get(DownloadQueue.MainQueueId)!;
        main.SimultaneousCount = AtOnce;
        queues.Update(main);
        using var manager = new QueueManager(service, queues, new DownloadProber([new HttpTransferProtocol(new HttpClientProvider())]), () => h.Options)
        {
            RetryDelay = TimeSpan.Zero,
        };
        var finished = new ConcurrentQueue<QueueEventArgs>();
        manager.QueueStopped += (_, e) => finished.Enqueue(e);

        var ids = new List<long>();
        for (var i = 0; i < Files; i++)
        {
            var file = h.Server.AddFile($"/soak/{i:D3}.bin", 256 * 1024, seed: i + 1);
            file.BytesPerSecond = 512 * 1024;
            ids.Add(service.Add(new DownloadRequest
            {
                Url = h.Server.UrlFor(file).AbsoluteUri,
                FileName = Path.GetFileName(file.Path),
                SaveFolder = h.SaveDirectory,
                QueueId = DownloadQueue.MainQueueId,
            }).Id);
        }

        var peak = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                peak = Math.Max(peak, h.Engine.RunningIds.Count);
                await Task.Delay(50).ConfigureAwait(false);
            }
        });

        var clock = Stopwatch.StartNew();
        long? baseline = null;
        var rounds = 0;
        var memory = 0L;
        while (rounds == 0 || clock.Elapsed < Duration)
        {
            finished.Clear();
            Assert.True(manager.Start(DownloadQueue.MainQueueId), "the queue did not start");
            await EngineHarness.WaitUntilAsync(() => !finished.IsEmpty, TimeSpan.FromMinutes(5), $"round {rounds + 1} finished");
            Assert.All(ids, id => Assert.Equal(DownloadStatus.Completed, service.Find(id)!.Status));
            rounds++;

            memory = Measure();
            baseline ??= memory; // after the first round: caches, pools and JIT are warm
            output.WriteLine($"round {rounds} at {clock.Elapsed:mm\\:ss}: {memory / (1024 * 1024)} MB");

            foreach (var id in ids)
            {
                await service.RedownloadAsync(id, start: false);
            }
        }

        await sampling.CancelAsync();
        await sampler;
        var growth = memory - baseline!.Value;
        output.WriteLine($"{rounds} rounds of {Files} files, peak {peak} at once, growth {growth / (1024 * 1024)} MB");
        Assert.True(rounds >= 2, "too few rounds to compare");
        Assert.Equal(AtOnce, peak);
        Assert.True(growth < MaxGrowth, $"memory grew by {growth / (1024 * 1024)} MB");
    }

    /// <summary>Private memory of the test process after a full collection.</summary>
    private static long Measure()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }
}
