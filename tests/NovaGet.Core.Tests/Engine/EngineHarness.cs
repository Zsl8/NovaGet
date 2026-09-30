using System.Security.Cryptography;
using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Http;
using NovaGet.Core.Models;
using NovaGet.Core.Tests.Data;
using NovaGet.Core.Tests.Infrastructure;
using NovaGet.Data.Repositories;
using NovaGet.TestServer;

namespace NovaGet.Core.Tests.Engine;

/// <summary>A test server, a migrated database and an engine with fast retry/checkpoint timings.</summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TestDatabase _db = new();
    private readonly HttpClientProvider _clients = new();

    private EngineHarness(TestHttpServer server, Func<EngineOptions, EngineOptions>? configure)
    {
        Server = server;
        Repository = new DownloadRepository(_db.Database, _db.Protector);
        Options = new EngineOptions
        {
            TempDirectory = _temp.Combine("temp"),
            RetryBaseDelay = TimeSpan.FromMilliseconds(20),
            RetryMaxDelay = TimeSpan.FromMilliseconds(200),
            CheckpointInterval = TimeSpan.FromMilliseconds(200),
            SpeedSampleInterval = TimeSpan.FromMilliseconds(100),
            Timeout = TimeSpan.FromSeconds(10),
            FreeSpaceMargin = 0,
        };
        if (configure is not null)
        {
            Options = configure(Options);
        }

        Engine = CreateEngine();
    }

    public TestHttpServer Server { get; }

    public DownloadRepository Repository { get; }

    public NovaGet.Data.SqliteDatabase Database => _db.Database;

    public EngineOptions Options { get; set; }

    public DownloadEngine Engine { get; private set; }

    public string SaveDirectory => _temp.Combine("downloads");

    public static async Task<EngineHarness> CreateAsync(Func<EngineOptions, EngineOptions>? configure = null) =>
        new(await TestHttpServer.StartAsync(), configure);

    public DownloadEngine CreateEngine() =>
        new(Repository, [new HttpTransferProtocol(_clients)], () => Options);

    /// <summary>Replaces the engine, as a restarted app would (the old one is simply abandoned).</summary>
    public void RestartEngine() => Engine = CreateEngine();

    public long Add(Uri url, string? fileName = null, Action<Download>? configure = null)
    {
        var download = new Download
        {
            Url = url.AbsoluteUri,
            OriginalUrl = url.AbsoluteUri,
            FileName = fileName ?? string.Empty,
            SavePath = SaveDirectory,
            CategoryId = Category.GeneralId,
            Status = DownloadStatus.Paused,
        };
        configure?.Invoke(download);
        return Repository.Insert(download);
    }

    public long Add(TestFile file, string? fileName = null, Action<Download>? configure = null) =>
        Add(Server.UrlFor(file), fileName, configure);

    /// <summary>Starts the download and waits for it to stop (Completed, Error or Paused).</summary>
    public async Task<DownloadStateChangedEventArgs> RunAsync(long id, TimeSpan? timeout = null)
    {
        var finished = WaitForEndAsync(id, timeout);
        Assert.True(Engine.Start(id), "Engine.Start returned false");
        return await finished;
    }

    /// <summary>Like <see cref="RunAsync"/>, also returning the most connections the engine had open at once.</summary>
    public async Task<(DownloadStateChangedEventArgs Result, int PeakConnections)> RunTrackingConnectionsAsync(long id, TimeSpan? timeout = null)
    {
        var finished = WaitForEndAsync(id, timeout);
        var peak = 0;
        Assert.True(Engine.Start(id), "Engine.Start returned false");
        while (!finished.IsCompleted)
        {
            if (Engine.GetProgress(id) is { } progress)
            {
                peak = Math.Max(peak, progress.ActiveConnections);
            }

            await Task.WhenAny(finished, Task.Delay(5));
        }

        return (await finished, peak);
    }

    public Task<DownloadStateChangedEventArgs> WaitForEndAsync(long id, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<DownloadStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = Engine;
        void Handler(object? sender, DownloadStateChangedEventArgs e)
        {
            if (e.Id == id && e.Status is DownloadStatus.Completed or DownloadStatus.Error or DownloadStatus.Paused)
            {
                engine.StateChanged -= Handler;
                tcs.TrySetResult(e);
            }
        }

        engine.StateChanged += Handler;
        return tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(60));
    }

    /// <summary>Polls until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? because = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time" + (because is null ? "." : $": {because}"));
            }

            await Task.Delay(20);
        }
    }

    public Download Get(long id) => Repository.Get(id) ?? throw new InvalidOperationException($"Download {id} not found.");

    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await Server.DisposeAsync();
        _clients.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }
}
