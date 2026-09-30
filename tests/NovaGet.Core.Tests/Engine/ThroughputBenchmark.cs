using System.Diagnostics;
using NovaGet.Core.Models;
using Xunit.Abstractions;

namespace NovaGet.Core.Tests.Engine;

/// <summary>Opt-in (NOVAGET_BENCH=1): loopback throughput and CPU use of a 16-connection download.</summary>
public sealed class ThroughputBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("NOVAGET_BENCH") == "1";

    [Fact]
    public async Task Sixteen_connections_on_loopback()
    {
        if (!Enabled)
        {
            return;
        }

        await using var h = await EngineHarness.CreateAsync(o => o with { MaxConnections = 16 });
        var file = h.Server.AddFile("bench.bin", 1024L * 1024 * 1024);
        var id = h.Add(file);

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var result = await h.RunAsync(id, TimeSpan.FromMinutes(5));
        clock.Stop();
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalSeconds;

        Assert.Equal(DownloadStatus.Completed, result.Status);
        var mbps = file.Size / clock.Elapsed.TotalSeconds / (1024 * 1024);
        output.WriteLine($"1 GiB in {clock.Elapsed.TotalSeconds:0.00} s = {mbps:0} MB/s; process CPU {cpu:0.0} s " +
                         $"({cpu / clock.Elapsed.TotalSeconds / Environment.ProcessorCount:P0} of {Environment.ProcessorCount} cores, server included)");
    }
}
