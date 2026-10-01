using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Models;

namespace NovaGet.Core.Engine;

public sealed class DownloadEngine : IDownloadEngine, IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, DownloadJob> _jobs = new();
    private readonly IDownloadRepository _repository;
    private readonly IReadOnlyList<ITransferProtocol> _protocols;
    private readonly Func<EngineOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    static DownloadEngine()
    {
        // Content-Disposition may name legacy charsets (windows-1252, shift_jis, ...).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public DownloadEngine(
        IDownloadRepository repository,
        IEnumerable<ITransferProtocol> protocols,
        Func<EngineOptions> options,
        ILoggerFactory? loggerFactory = null)
    {
        _repository = repository;
        _protocols = [.. protocols];
        _options = options;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<DownloadEngine>();
    }

    public event EventHandler<DownloadStateChangedEventArgs>? StateChanged;

    public SpeedLimits SpeedLimits { get; } = new();

    public TrafficCounter Traffic { get; } = new();

    public HostConnectionLimits HostLimits { get; } = new();

    public IReadOnlyCollection<long> RunningIds => [.. _jobs.Keys];

    public bool IsRunning(long downloadId) => _jobs.ContainsKey(downloadId);

    public bool Start(long downloadId, bool startedByQueue = false)
    {
        if (_jobs.ContainsKey(downloadId))
        {
            return false;
        }

        var download = _repository.Get(downloadId) ?? throw new KeyNotFoundException($"Download {downloadId} does not exist.");
        if (download.Status == DownloadStatus.Completed)
        {
            return false;
        }

        if (!Uri.TryCreate(download.Url, UriKind.Absolute, out var uri) || _protocols.FirstOrDefault(p => p.CanHandle(uri)) is not { } protocol)
        {
            _repository.UpdateStatus(downloadId, DownloadStatus.Error, "Unsupported address.");
            StateChanged?.Invoke(this, new DownloadStateChangedEventArgs(downloadId, DownloadStatus.Error, DownloadErrorKind.InvalidAddress, "Unsupported address."));
            return false;
        }

        var job = new DownloadJob(
            download,
            _options(),
            _repository,
            protocol,
            new JobServices(SpeedLimits, HostLimits, Traffic),
            startedByQueue,
            _loggerFactory.CreateLogger($"NovaGet.Download.{downloadId.ToString(CultureInfo.InvariantCulture)}"),
            finished => _jobs.TryRemove(KeyValuePair.Create(finished.Id, finished)));
        if (!_jobs.TryAdd(downloadId, job))
        {
            return false;
        }

        job.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
        job.Start();
        return true;
    }

    public Task PauseAsync(long downloadId) => StopAsync(downloadId, StopReason.Pause);

    public Task PauseAllAsync() => Task.WhenAll(_jobs.Keys.Select(id => StopAsync(id, StopReason.Pause)));

    public async Task RestartAsync(long downloadId)
    {
        await ResetAsync(downloadId).ConfigureAwait(false);
        Start(downloadId);
    }

    public async Task ResetAsync(long downloadId)
    {
        await StopAsync(downloadId, StopReason.Pause).ConfigureAwait(false);
        var download = _repository.Get(downloadId) ?? throw new KeyNotFoundException($"Download {downloadId} does not exist.");
        DeleteTempFiles(downloadId);
        _repository.SaveSegments(downloadId, []);
        download.Downloaded = 0;
        download.ResumeCapable = null; // probe again: size, validators and name may all have changed
        download.ETag = null;
        download.LastModified = null;
        download.CompletedAt = null;
        download.LastError = null;
        download.Status = DownloadStatus.Paused;
        if (download.Url != download.OriginalUrl && !string.IsNullOrEmpty(download.OriginalUrl))
        {
            download.Url = download.OriginalUrl;
        }

        _repository.Update(download);
    }

    public async Task RemoveAsync(long downloadId)
    {
        await StopAsync(downloadId, StopReason.Remove).ConfigureAwait(false);
        if (!_options().KeepTempFilesAfterCancel)
        {
            DeleteTempFiles(downloadId);
        }
    }

    public void SetSpeedLimit(long downloadId, int? kilobytesPerSecond)
    {
        if (_jobs.TryGetValue(downloadId, out var job))
        {
            job.SetSpeedLimit(kilobytesPerSecond);
        }
    }

    public DownloadProgress? GetProgress(long downloadId) =>
        _jobs.TryGetValue(downloadId, out var job) ? job.GetProgress() : null;

    public int RecoverInterrupted()
    {
        var count = 0;
        foreach (var download in _repository.GetAll())
        {
            if (download.Status.IsActive() && !_jobs.ContainsKey(download.Id))
            {
                _repository.UpdateStatus(download.Id, DownloadStatus.Paused, download.LastError);
                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogInformation("Marked {Count} interrupted download(s) as paused", count);
        }

        return count;
    }

    /// <summary>Saves progress of everything running (app exit).</summary>
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_jobs.Keys.Select(id => StopAsync(id, StopReason.Shutdown))).ConfigureAwait(false);
    }

    /// <summary>Tests only: abandons a running download the way a killed process would (no final save).</summary>
    internal Task SimulateCrashAsync(long downloadId) => StopAsync(downloadId, StopReason.Crash);

    private async Task StopAsync(long downloadId, StopReason reason)
    {
        if (!_jobs.TryGetValue(downloadId, out var job))
        {
            return;
        }

        job.Stop(reason);
        try
        {
            await job.Completion.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Download {Id} ended with an error while stopping", downloadId);
        }
    }

    private void DeleteTempFiles(long downloadId)
    {
        var directory = Path.Combine(_options().TempDirectory, downloadId.ToString(CultureInfo.InvariantCulture));
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete temp folder {Folder}", directory);
        }
    }
}
