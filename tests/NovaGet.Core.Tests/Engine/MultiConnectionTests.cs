using System.Diagnostics;
using NovaGet.Core.Engine;
using NovaGet.Core.Models;

namespace NovaGet.Core.Tests.Engine;

/// <summary>Milestone 3: dynamic segmentation, reuse, retries, crash-safe resume, limits.</summary>
public sealed class MultiConnectionTests
{
    private static Func<EngineOptions, EngineOptions> Connections(int n) => o => o with { MaxConnections = n };

    private static List<NovaGet.TestServer.RequestRecord> Transfers(EngineHarness h, NovaGet.TestServer.TestFile file) =>
        [.. h.Server.RequestsFor(file).Where(r => r.Method == "GET" && r.Range != "bytes=0-0")];

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public async Task Hash_matches_with_n_connections(int connections)
    {
        await using var h = await EngineHarness.CreateAsync(Connections(connections));
        var file = h.Server.AddFile($"iso-{connections}.iso", 6 * 1024 * 1024 + 12_345);
        file.BytesPerSecond = 2 * 1024 * 1024; // per request, so extra connections really help
        var id = h.Add(file);

        var (result, peak) = await h.RunTrackingConnectionsAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
        Assert.InRange(peak, 1, connections);
        if (connections > 1)
        {
            Assert.True(Transfers(h, file).Count >= connections, $"expected at least {connections} range requests, saw {Transfers(h, file).Count}");
            Assert.True(peak > connections / 2, $"peak concurrency was {peak}");
        }
    }

    [Fact]
    public async Task Server_without_ranges_uses_one_connection()
    {
        await using var h = await EngineHarness.CreateAsync(Connections(8));
        var file = h.Server.AddFile("single.bin", 3 * 1024 * 1024);
        file.SupportsRanges = false;
        file.BytesPerSecond = 4 * 1024 * 1024;
        var id = h.Add(file);

        var (result, peak) = await h.RunTrackingConnectionsAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.False(h.Get(id).ResumeCapable);
        Assert.Equal(1, peak);
        Assert.Single(Transfers(h, file));
    }

    [Fact]
    public async Task Random_drops_every_1_to_5_MB_still_produce_a_correct_file()
    {
        await using var h = await EngineHarness.CreateAsync(Connections(8));
        var file = h.Server.AddFile("drops.bin", 40 * 1024 * 1024);
        file.DropAfterBytes = (1024 * 1024, 5 * 1024 * 1024);
        var id = h.Add(file);

        var result = await h.RunAsync(id, TimeSpan.FromMinutes(2));

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
    }

    [Fact]
    public async Task Kill_and_restart_with_many_segments_resumes_correctly()
    {
        await using var h = await EngineHarness.CreateAsync(Connections(8));
        var file = h.Server.AddFile("crash8.iso", 8 * 1024 * 1024);
        file.BytesPerSecond = 256 * 1024;
        var id = h.Add(file);

        h.Engine.Start(id);
        await EngineHarness.WaitUntilAsync(
            () => h.Repository.GetSegments(id).Count >= 4 && h.Get(id).Downloaded > 512 * 1024,
            because: "several segments were checkpointed");
        await h.Engine.SimulateCrashAsync(id);

        var saved = h.Repository.GetSegments(id);
        h.RestartEngine();
        h.Engine.RecoverInterrupted();
        file.BytesPerSecond = 0;
        h.Server.ClearRequests();
        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
        // Resumed where each saved segment left off, never from zero.
        var resumedStarts = Transfers(h, file).Select(r => r.RangeStart).ToHashSet();
        Assert.Contains(saved.Where(s => s.CurrentByte <= s.EndByte), s => resumedStarts.Contains(s.CurrentByte));
        Assert.DoesNotContain(0L, resumedStarts);
    }

    [Fact]
    public async Task ETag_change_mid_download_asks_to_restart()
    {
        await using var h = await EngineHarness.CreateAsync(Connections(1));
        var file = h.Server.AddFile("etag-mid.bin", 2 * 1024 * 1024);
        file.BytesPerSecond = 512 * 1024;
        file.DropAfterBytes = (600 * 1024, 600 * 1024);
        file.DropLimit = 1; // the first transfer drops at 600 KB and must reconnect
        var id = h.Add(file, "etag-mid.bin", d =>
        {
            d.Size = file.Size;
            d.ResumeCapable = true;
            d.ETag = file.ETag;
        });

        var finished = h.WaitForEndAsync(id);
        h.Engine.Start(id);
        await EngineHarness.WaitUntilAsync(() => h.Engine.GetProgress(id)?.Downloaded > 200 * 1024);
        file.ChangeContent(newSeed: 99);

        var result = await finished;
        Assert.Equal(DownloadStatus.Error, result.Status);
        Assert.Equal(DownloadErrorKind.ServerFileChanged, result.ErrorKind);
    }

    [Fact]
    public async Task Connection_that_finishes_early_takes_over_the_largest_segment_on_its_reused_socket()
    {
        await using var h = await EngineHarness.CreateAsync(o => o with { MaxConnections = 2, MinSegmentSize = 16 * 1024 });
        var file = h.Server.AddFile("takeover.bin", 4 * 1024 * 1024);
        // The first connection (from byte 0) is slow; any other range is fast.
        file.BytesPerSecondByStart = start => start == 0 ? 256 * 1024 : 8 * 1024 * 1024;
        var id = h.Add(file);

        var (result, peak) = await h.RunTrackingConnectionsAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
        Assert.Equal(2, peak);
        var transfers = Transfers(h, file);
        var slow = transfers.Single(r => r.RangeStart == 0);
        var helper = transfers.First(r => r.RangeStart > 0);
        Assert.InRange(helper.RangeStart, 1024 * 1024, 3 * 1024 * 1024);   // the midpoint split of the whole file

        // After finishing its half, the fast connection took the second half of the slow segment's remainder
        // (below its original split point), on the same keep-alive socket.
        var takeovers = transfers.Where(r => r.RangeStart > 0 && r.RangeStart < helper.RangeStart).ToList();
        Assert.NotEmpty(takeovers);
        Assert.Contains(takeovers, r => r.ConnectionId == helper.ConnectionId);
        Assert.DoesNotContain(takeovers, r => r.ConnectionId == slow.ConnectionId);
    }

    [Fact]
    public async Task Server_connection_limit_lowers_the_count_for_the_host()
    {
        await using var h = await EngineHarness.CreateAsync(Connections(8));
        var file = h.Server.AddFile("limited.bin", 6 * 1024 * 1024);
        file.BytesPerSecond = 2 * 1024 * 1024;
        file.MaxConcurrentRequests = 3;
        var id = h.Add(file);

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
        Assert.Contains(h.Server.RequestsFor(file), r => r.Status == 503);
        var cap = h.Engine.HostLimits.Get(h.Server.BaseUri.Host);
        Assert.NotNull(cap);
        Assert.InRange(cap.Value, 1, 3);

        // A second download from the same host never opens more connections than the learned cap.
        var second = h.Server.AddFile("limited2.bin", 4 * 1024 * 1024);
        second.BytesPerSecond = 2 * 1024 * 1024;
        second.MaxConcurrentRequests = 3;
        var secondId = h.Add(second);
        var (secondResult, peak) = await h.RunTrackingConnectionsAsync(secondId);
        Assert.Equal(DownloadStatus.Completed, secondResult.Status);
        Assert.InRange(peak, 1, cap.Value);
    }

    [Fact]
    public async Task Range_ignored_by_extra_connection_falls_back_to_one_connection()
    {
        await using var h = await EngineHarness.CreateAsync(Connections(8));
        var file = h.Server.AddFile("liar.bin", 3 * 1024 * 1024);
        file.RangesOnlyForProbe = true;
        file.AdvertiseRanges = false;
        file.SendValidators = false;
        file.BytesPerSecond = 2 * 1024 * 1024;
        var id = h.Add(file);

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.False(h.Get(id).ResumeCapable);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
    }

    [Fact]
    public async Task Per_server_exception_caps_connections()
    {
        await using var h = await EngineHarness.CreateAsync(o => o with
        {
            MaxConnections = 16,
            ServerConnectionLimits = new Dictionary<string, int> { ["127.0.0.1"] = 2 },
        });
        var file = h.Server.AddFile("capped.bin", 4 * 1024 * 1024);
        file.BytesPerSecond = 2 * 1024 * 1024;
        var id = h.Add(file);

        var (_, peak) = await h.RunTrackingConnectionsAsync(id);

        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task Per_download_override_wins()
    {
        await using var h = await EngineHarness.CreateAsync(Connections(16));
        var file = h.Server.AddFile("override.bin", 4 * 1024 * 1024);
        file.BytesPerSecond = 2 * 1024 * 1024;
        var id = h.Add(file, configure: d => d.MaxConnections = 3);

        var (_, peak) = await h.RunTrackingConnectionsAsync(id);

        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task Progress_lists_every_connection_and_segment()
    {
        await using var h = await EngineHarness.CreateAsync(Connections(4));
        var file = h.Server.AddFile("progress4.bin", 8 * 1024 * 1024);
        file.BytesPerSecond = 512 * 1024;
        var id = h.Add(file);

        h.Engine.Start(id);
        await EngineHarness.WaitUntilAsync(() => h.Engine.GetProgress(id)?.Connections.Count(c => c.Downloaded > 0) == 4);
        var progress = h.Engine.GetProgress(id)!;
        await h.Engine.PauseAsync(id);

        Assert.Equal(4, progress.Segments.Count);
        Assert.Equal(progress.Downloaded, progress.Segments.Sum(s => s.Received - s.Start));
        Assert.Equal(file.Size - 1, progress.Segments.Max(s => s.End));
        Assert.All(progress.Connections, c => Assert.Equal("Receiving data...", c.Info));
    }

    [Fact]
    public async Task Speed_limiter_holds_500_KBps_within_10_percent_over_20_seconds()
    {
        await using var h = await EngineHarness.CreateAsync(Connections(8));
        const int limitKBps = 500;
        var file = h.Server.AddFile("limited-speed.bin", limitKBps * 1024L * 20);
        var id = h.Add(file, configure: d => d.SpeedLimitKBps = limitKBps);

        var receiving = new TaskCompletionSource();
        h.Engine.StateChanged += (_, e) =>
        {
            if (e.Id == id && e.Status == DownloadStatus.Receiving)
            {
                receiving.TrySetResult();
            }
        };
        var finished = h.WaitForEndAsync(id, TimeSpan.FromMinutes(1));
        h.Engine.Start(id);
        await receiving.Task;
        var clock = Stopwatch.StartNew();
        var result = await finished;
        clock.Stop();

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.InRange(clock.Elapsed.TotalSeconds, 18.0, 22.0);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
    }

    [Fact]
    public async Task Global_limiter_applies_and_can_be_changed_while_running()
    {
        await using var h = await EngineHarness.CreateAsync(Connections(4));
        h.Engine.SpeedLimits.SetGlobal(256, queueOnly: false);
        var file = h.Server.AddFile("global.bin", 4 * 1024 * 1024);
        var id = h.Add(file);

        var finished = h.WaitForEndAsync(id);
        h.Engine.Start(id);
        await Task.Delay(2000);
        var afterTwoSeconds = h.Engine.GetProgress(id)!.Downloaded;
        h.Engine.SpeedLimits.SetGlobal(null, queueOnly: false);
        var result = await finished;

        Assert.InRange(afterTwoSeconds, 300 * 1024, 900 * 1024);
        Assert.Equal(DownloadStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Global_limiter_can_be_restricted_to_queue_downloads()
    {
        await using var h = await EngineHarness.CreateAsync(Connections(4));
        h.Engine.SpeedLimits.SetGlobal(64, queueOnly: true);
        var file = h.Server.AddFile("manual.bin", 4 * 1024 * 1024);
        var id = h.Add(file);

        var clock = Stopwatch.StartNew();
        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "a manually started download must ignore a queue-only limit");
    }

    [Fact]
    public async Task Per_download_limit_can_be_changed_while_running()
    {
        await using var h = await EngineHarness.CreateAsync(Connections(4));
        var file = h.Server.AddFile("tunable.bin", 6 * 1024 * 1024);
        var id = h.Add(file, configure: d => d.SpeedLimitKBps = 128);

        var finished = h.WaitForEndAsync(id);
        h.Engine.Start(id);
        await Task.Delay(1500);
        Assert.InRange(h.Engine.GetProgress(id)!.Downloaded, 64 * 1024, 512 * 1024);

        h.Engine.SetSpeedLimit(id, null);
        var result = await finished.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(DownloadStatus.Completed, result.Status);
    }
}
