using System.Globalization;
using NovaGet.TestServer;

// Manual testing: serves /file-100m.bin (100 MB, ranges) and /slow.bin (20 MB at 1 MB/s per connection).
var port = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 8080;
await using var server = await TestHttpServer.StartAsync(port);
server.AddFile("file-100m.bin", 100L * 1024 * 1024);
server.AddFile("slow.bin", 20L * 1024 * 1024, seed: 2).BytesPerSecond = 1024 * 1024;
server.AddFile("noranges.bin", 10L * 1024 * 1024, seed: 3).SupportsRanges = false;
Console.WriteLine($"NovaGet test server listening on {server.BaseUri} (Ctrl+C to stop)");
var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    done.TrySetResult();
};
await done.Task;
