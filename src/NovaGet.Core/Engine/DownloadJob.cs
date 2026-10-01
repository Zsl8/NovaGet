using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine.Naming;
using NovaGet.Core.Engine.Storage;
using NovaGet.Core.Models;

namespace NovaGet.Core.Engine;

internal enum StopReason
{
    None,

    /// <summary>User pressed Pause/Stop: save progress, status Paused.</summary>
    Pause,

    /// <summary>App is exiting: save progress, status Paused.</summary>
    Shutdown,

    /// <summary>Download is being removed: don't save anything.</summary>
    Remove,

    /// <summary>Tests only: vanish like a killed process (no final checkpoint or status).</summary>
    Crash,
}

/// <summary>
/// Runs one download from start (or resume) to completion: probe, temp file, connections, checkpoints,
/// validation of resumed responses, and the final move to the Save To folder.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The stop token source has no timer or wait handle; not disposing it avoids races with late Stop() calls.")]
internal sealed class DownloadJob : IEngineJob
{
    private readonly Download _download;
    private readonly IDownloadRepository _repository;
    private readonly ITransferProtocol _protocol;
    private readonly ILogger _logger;
    private readonly Action<IEngineJob> _onFinished;
    private readonly CancellationTokenSource _stop = new();
    private readonly SpeedMeter _speed = new();
    private readonly object _checkpointGate = new();
    private readonly List<ConnectionWorker> _workers = [];
    private readonly List<Task> _running = [];
    private readonly TransferThrottle _throttle;
    private readonly TrafficCounter _traffic;
    private readonly HostConnectionLimits _hostLimits;
    private CancellationToken _workersToken;
    private int _maxConnections = 1;
    private StopReason _stopReason;
    private int _consecutiveFailures;
    private volatile DownloadStatus _status;
    private volatile string? _message;
    private TempFile? _file;
    private SegmentMap? _map;

    public DownloadJob(
        Download download,
        EngineOptions options,
        IDownloadRepository repository,
        ITransferProtocol protocol,
        JobServices services,
        bool startedByQueue,
        ILogger logger,
        Action<IEngineJob> onFinished)
    {
        _download = download;
        Options = options;
        _repository = repository;
        _protocol = protocol;
        _traffic = services.Traffic;
        _hostLimits = services.HostLimits;
        _logger = logger;
        _onFinished = onFinished;
        _status = download.Status;
        var limitKBps = download.SpeedLimitKBps ?? HostPattern.Lookup(options.HostSpeedLimitsKBps, Host) ?? 0;
        _throttle = new TransferThrottle(Math.Max(0, limitKBps) * 1024L, services.SpeedLimits, startedByQueue);
    }

    /// <summary>Host of the current address (used for per-server limits).</summary>
    public string Host => Uri.TryCreate(_download.Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    /// <summary>Changes this download's own speed limit while it runs (null/0 = unlimited).</summary>
    public void SetSpeedLimit(int? kilobytesPerSecond)
    {
        _download.SpeedLimitKBps = kilobytesPerSecond;
        _throttle.SetLimit(kilobytesPerSecond);
    }

    public long Id => _download.Id;

    public EngineOptions Options { get; }

    public SegmentMap Map => _map ?? throw new InvalidOperationException("The download has not been prepared.");

    public Task Completion { get; private set; } = Task.CompletedTask;

    public DownloadStatus Status => _status;

    public event EventHandler<DownloadStateChangedEventArgs>? StateChanged;

    public void Start() => Completion = Task.Run(RunAsync);

    public void Stop(StopReason reason)
    {
        if (_stopReason == StopReason.None)
        {
            _stopReason = reason;
        }

        _stop.Cancel();
    }

    public DownloadProgress GetProgress()
    {
        var map = _map;
        var size = map?.Size ?? _download.Size;
        var downloaded = map?.ReceivedBytes ?? _download.Downloaded;
        var speed = _status == DownloadStatus.Receiving ? _speed.BytesPerSecond : 0;
        ConnectionProgress[] connections;
        int active;
        lock (_workers)
        {
            connections = [.. _workers.Select(w => w.Snapshot())];
            active = _running.Count(t => !t.IsCompleted);
        }

        return new DownloadProgress
        {
            Id = Id,
            Status = _status,
            Size = size,
            Downloaded = downloaded,
            BytesPerSecond = speed,
            TimeLeft = SpeedMeter.TimeLeft(size, downloaded, speed),
            ResumeCapable = _download.ResumeCapable,
            ActiveConnections = active,
            Connections = connections,
            Segments = map?.Snapshot() ?? [],
            Message = _message,
        };
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        try
        {
            _download.LastTryAt = DateTime.UtcNow;
            SetStatus(DownloadStatus.Connecting, "Connecting...");
            await PrepareAsync(token).ConfigureAwait(false);

            if (!Map.IsComplete)
            {
                SetStatus(DownloadStatus.Receiving, "Receiving data...");
                await ReceiveAllAsync(token).ConfigureAwait(false);
            }

            await FinishAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            await StoppedAsync().ConfigureAwait(false);
        }
        catch (DownloadException ex)
        {
            Failed(ex);
        }
        catch (IOException ex) when (DiskSpace.IsDiskFull(ex))
        {
            Failed(new DownloadException(DownloadErrorKind.DiskFull, "Not enough disk space.", inner: ex));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "File system error in download {Id}", Id);
            Failed(new DownloadException(DownloadErrorKind.FileSystem, ex.Message, inner: ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in download {Id}", Id);
            Failed(new DownloadException(DownloadErrorKind.Unknown, ex.Message, inner: ex));
        }
        finally
        {
            _file?.Dispose();
            _file = null;
        }
    }

    private async Task PrepareAsync(CancellationToken token)
    {
        var saved = _repository.GetSegments(Id);
        var tempPath = FindTempFile();

        if (saved.Count > 0 && tempPath is not null && _download.ResumeCapable != false)
        {
            _file = TempFile.OpenExisting(tempPath, _download.Size);
        }

        if (_file is not null)
        {
            _map = SegmentMap.Restore(saved, _download.Size);
            _logger.LogInformation("Resuming download {Id} at {Bytes} bytes", Id, _map.WrittenBytes);
        }
        else
        {
            if (saved.Count > 0)
            {
                _logger.LogInformation("Download {Id} restarts from the beginning (temp file missing or no resume support)", Id);
            }

            if (_download.ResumeCapable is null || _download.Size < 0 || string.IsNullOrWhiteSpace(_download.FileName))
            {
                await ProbeAsync(token).ConfigureAwait(false);
            }

            DeleteTempDirectory();
            _file = TempFile.Create(TempPathFor(_download.FileName), _download.Size);
            _map = SegmentMap.Create(_download.Size);
            _download.Downloaded = 0;
            Checkpoint();
        }

        EnsureFreeSpace();
    }

    private async Task ProbeAsync(CancellationToken token)
    {
        var probe = await _protocol.ProbeAsync(RequestContextFor(), token).ConfigureAwait(false);
        _download.Url = probe.FinalUri.AbsoluteUri;
        if (string.IsNullOrWhiteSpace(_download.OriginalUrl))
        {
            _download.OriginalUrl = _download.Url;
        }

        if (string.IsNullOrWhiteSpace(_download.FileName))
        {
            _download.FileName = probe.FileName;
        }

        _download.Size = probe.Size;
        _download.ResumeCapable = probe.ResumeSupported;
        _download.ETag = probe.ETag;
        _download.LastModified = probe.LastModified?.UtcDateTime;
        _repository.UpdateProbe(Id, _download.Url, _download.FileName, _download.Size, _download.ResumeCapable, _download.ETag, _download.LastModified);
    }

    private void EnsureFreeSpace()
    {
        if (Map.Size <= 0)
        {
            return;
        }

        var needed = Map.Size - Map.WrittenBytes + Options.FreeSpaceMargin;
        if (DiskSpace.AvailableBytes(Options.TempDirectory) is { } available && available < needed)
        {
            throw new DownloadException(DownloadErrorKind.DiskFull,
                string.Create(CultureInfo.InvariantCulture, $"Not enough disk space: {needed / (1024 * 1024)} MB needed, {available / (1024 * 1024)} MB free."));
        }
    }

    private async Task ReceiveAllAsync(CancellationToken token)
    {
        using var background = CancellationTokenSource.CreateLinkedTokenSource(token);
        var housekeeping = HousekeepingAsync(background.Token);
        try
        {
            while (true)
            {
                try
                {
                    await RunConnectionsAsync(token).ConfigureAwait(false);
                    break;
                }
                catch (DownloadException ex) when (ex.Kind == DownloadErrorKind.RangeNotSupported && _download.ResumeCapable != false)
                {
                    FallBackToSingleConnection(ex);
                }
            }
        }
        finally
        {
            await background.CancelAsync().ConfigureAwait(false);
            await housekeeping.ConfigureAwait(false);
        }

        if (!Map.IsComplete)
        {
            throw new DownloadException(DownloadErrorKind.Network, "The download ended before all data was received.");
        }
    }

    /// <summary>
    /// Starts with one connection; every connection that receives its first bytes opens the next one, up to the
    /// limit. Waits until all have finished. The first fatal error stops the others and is rethrown.
    /// </summary>
    private async Task RunConnectionsAsync(CancellationToken token)
    {
        using var connections = CancellationTokenSource.CreateLinkedTokenSource(token);
        _workersToken = connections.Token;
        _maxConnections = ComputeMaxConnections();
        _logger.LogInformation("Download {Id}: up to {Max} connection(s)", Id, _maxConnections);
        TrySpawnConnection(force: true);

        Exception? fatal = null;
        while (true)
        {
            Task[] running;
            lock (_workers)
            {
                running = [.. _running];
            }

            if (running.Length == 0)
            {
                break;
            }

            var finished = await Task.WhenAny(running).ConfigureAwait(false);
            lock (_workers)
            {
                _running.Remove(finished);
            }

            if (finished.IsFaulted && fatal is null)
            {
                var error = finished.Exception!.GetBaseException();
                if (error is not OperationCanceledException)
                {
                    fatal = error;
                    await connections.CancelAsync().ConfigureAwait(false);
                }
            }
        }

        if (fatal is not null)
        {
            ExceptionDispatchInfo.Capture(fatal).Throw();
        }

        token.ThrowIfCancellationRequested();
    }

    private int ComputeMaxConnections()
    {
        if (_download.ResumeCapable != true || Map.Size <= 0)
        {
            return 1;
        }

        var configured = _download.MaxConnections
            ?? HostPattern.Lookup(Options.ServerConnectionLimits, Host)
            ?? Options.MaxConnections;
        var learned = _hostLimits.Get(Host) ?? int.MaxValue;
        return Math.Clamp(Math.Min(configured, learned), 1, 32);
    }

    /// <summary>Opens another connection if the limit allows and there is work to hand it.</summary>
    private void TrySpawnConnection(bool force = false)
    {
        lock (_workers)
        {
            if (_workersToken.IsCancellationRequested)
            {
                return;
            }

            var active = _running.Count(t => !t.IsCompleted);
            if (!force && (active >= _maxConnections || !Map.HasWorkFor(Options.MinSegmentSize)))
            {
                return;
            }

            var worker = new ConnectionWorker(_workers.Count + 1, this, _protocol);
            _workers.Add(worker);
            worker.FirstBytesReceived += _ => TrySpawnConnection();
            var token = _workersToken;
            _running.Add(Task.Run(() => worker.RunAsync(token), CancellationToken.None));
        }
    }

    /// <summary>
    /// A connection hit 429/503 or was refused while others are running: the server limits connections.
    /// Lower the cap for this download and (for the session) for the host; the caller closes its connection.
    /// </summary>
    public bool TryYieldConnection(ConnectionWorker worker, DownloadException error)
    {
        lock (_workers)
        {
            var active = _running.Count(t => !t.IsCompleted);
            if (active <= 1)
            {
                return false;
            }

            _maxConnections = Math.Max(1, Math.Min(_maxConnections, active - 1));
            var cap = _hostLimits.Lower(Host, _maxConnections);
            _logger.LogInformation("Download {Id}: {Host} limits connections ({Error}); connection {Connection} closed, cap now {Cap}",
                Id, Host, error.Message, worker.Number, cap);
            return true;
        }
    }

    /// <summary>The server answered a range request with the whole file: continue with one connection from byte 0.</summary>
    private void FallBackToSingleConnection(DownloadException reason)
    {
        _logger.LogWarning("Download {Id}: {Reason} Switching to a single connection from the beginning.", Id, reason.Message);
        _download.ResumeCapable = false;
        _repository.UpdateResumeCapable(Id, false);
        Map.Reset(Map.Size);
        lock (_workers)
        {
            _workers.Clear();
            _running.Clear();
        }

        Volatile.Write(ref _consecutiveFailures, 0);
        Checkpoint();
    }

    /// <summary>Samples speed every 500 ms and checkpoints every 2 s until cancelled.</summary>
    private async Task HousekeepingAsync(CancellationToken token)
    {
        var lastCheckpoint = DateTime.UtcNow;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(Options.SpeedSampleInterval, token).ConfigureAwait(false);
                _speed.Sample(Map.ReceivedBytes, DateTime.UtcNow);
                if (DateTime.UtcNow - lastCheckpoint >= Options.CheckpointInterval)
                {
                    lastCheckpoint = DateTime.UtcNow;
                    Checkpoint();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Checkpoint failed for download {Id}", Id);
        }
    }

    /// <summary>
    /// Saves a crash-safe resume point: snapshot the written offsets, flush the file to disk, then persist
    /// the snapshot. Anything written after the snapshot is simply fetched again after a crash.
    /// </summary>
    private void Checkpoint()
    {
        lock (_checkpointGate)
        {
            var map = _map;
            if (map is null)
            {
                return;
            }

            var segments = map.ToPersisted();
            var written = segments.Sum(s => s.CurrentByte - s.StartByte);
            _file?.FlushToDisk();
            _repository.SaveSegments(Id, segments);
            _download.Downloaded = written;
            _repository.UpdateProgress(Id, written, _status, _download.LastTryAt);
        }
    }

    /// <summary>
    /// Work for a free connection: an unowned segment first, otherwise (dynamic segmentation) the second half
    /// of the segment with the most bytes left. Its keep-alive socket is reused for the new range.
    /// </summary>
    public LiveSegment? NextSegment(ConnectionWorker worker)
    {
        var pending = Map.AcquirePending(worker);
        if (pending is not null || _download.ResumeCapable != true || Map.Size <= 0)
        {
            return pending;
        }

        return Map.SplitLargest(worker, Options.MinSegmentSize);
    }

    /// <summary>Positions a segment for a new request: at its last written byte, or at its start when the server can't resume.</summary>
    public TransferRequest CreateRequest(LiveSegment segment)
    {
        var useRange = _download.ResumeCapable != false;
        if (useRange)
        {
            Map.RewindToWritten(segment);
        }
        else
        {
            Map.RewindToStart(segment);
        }

        return new TransferRequest(RequestContextFor(), segment.Received, useRange ? segment.End : -1, Validator(), useRange);
    }

    /// <summary>Checks that a response really continues the same file at the offset we asked for.</summary>
    public void ValidateResponse(LiveSegment segment, TransferResponse response)
    {
        var resuming = segment.Received > 0;
        if (resuming && !response.IsPartial)
        {
            // The server ignored the range and is sending the file from byte 0: either If-Range failed
            // because the file changed, or the server doesn't really do ranges.
            if (!Validator().IsEmpty && ValidatorsDiffer(response))
            {
                throw new DownloadException(DownloadErrorKind.ServerFileChanged, "The file on the server has changed.");
            }

            throw new DownloadException(DownloadErrorKind.RangeNotSupported, "The server ignored a range request.");
        }

        if (response.IsPartial && response.Start != segment.Received)
        {
            throw new DownloadException(DownloadErrorKind.HttpError,
                string.Create(CultureInfo.InvariantCulture, $"The server sent data from byte {response.Start} instead of {segment.Received}."));
        }

        if (response.TotalSize is not { } total)
        {
            return;
        }

        if (Map.Size >= 0 && total != Map.Size)
        {
            if (resuming || Map.WrittenBytes > 0)
            {
                throw new DownloadException(DownloadErrorKind.ServerFileChanged, "The file on the server has changed (its size is different).")
                {
                    ReportedSize = total,
                };
            }

            ResizeFreshDownload(total);
        }
        else if (Map.Size < 0 && segment.Start == 0)
        {
            ResizeFreshDownload(total);
        }
    }

    private bool ValidatorsDiffer(TransferResponse response)
    {
        if (!string.IsNullOrEmpty(_download.ETag) && !string.IsNullOrEmpty(response.ETag))
        {
            return !string.Equals(_download.ETag, response.ETag, StringComparison.Ordinal);
        }

        if (_download.LastModified is { } ours && response.LastModified is { } theirs)
        {
            return Math.Abs((theirs.UtcDateTime - DateTime.SpecifyKind(ours, DateTimeKind.Utc)).TotalSeconds) > 1;
        }

        // A 200 without validators after an If-Range: assume the file changed (the safe reading).
        return string.IsNullOrEmpty(response.ETag) && response.LastModified is null;
    }

    private ResourceValidator Validator()
    {
        // Weak ETags can't be used with If-Range; the protocol then falls back to Last-Modified.
        var etag = _download.ETag;
        var strongETag = string.IsNullOrEmpty(etag) || etag.StartsWith("W/", StringComparison.Ordinal) ? null : etag;
        DateTimeOffset? modified = _download.LastModified is { } lm ? new DateTimeOffset(DateTime.SpecifyKind(lm, DateTimeKind.Utc)) : null;
        return new ResourceValidator(strongETag, modified);
    }

    private void ResizeFreshDownload(long size)
    {
        _logger.LogInformation("Download {Id}: size is {Size} bytes", Id, size);
        Map.SetSize(size);
        _file?.SetLength(size);
        _download.Size = size;
        _repository.UpdateSize(Id, size);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, long offset)
    {
        var file = _file ?? throw new InvalidOperationException("The temp file is not open.");
        try
        {
            // Never cancel a write half-way: the bytes are valid and the checkpoint must match the file.
            await file.WriteAsync(data, offset, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException ex) when (DiskSpace.IsDiskFull(ex))
        {
            throw new DownloadException(DownloadErrorKind.DiskFull, "Not enough disk space.", inner: ex);
        }
    }

    public void OnBytesReceived(int count)
    {
        _traffic.Add(count);
        Volatile.Write(ref _consecutiveFailures, 0);
    }

    /// <inheritdoc cref="TransferThrottle.MaxReadSize"/>
    public int MaxReadSize => _throttle.MaxReadSize;

    /// <inheritdoc cref="TransferThrottle.ThrottleAsync"/>
    public ValueTask ThrottleAsync(int count, CancellationToken token) => _throttle.ThrottleAsync(count, token);

    /// <summary>Counts a transient failure; returns false once the retry budget is used up.</summary>
    public bool RegisterFailure(ConnectionWorker worker, DownloadException error)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        _logger.LogInformation("Download {Id} connection {Connection}: {Error} (failure {Count}/{Max})",
            Id, worker.Number, error.Message, failures, Options.MaxRetries);
        return failures <= Options.MaxRetries;
    }

    private async Task FinishAsync()
    {
        SetStatus(DownloadStatus.Assembling, "Assembling...");
        var file = _file!;
        file.FlushToDisk();
        var size = Map.Size;
        if (Map.WrittenBytes != size || file.Length != size)
        {
            throw new DownloadException(DownloadErrorKind.Unknown,
                string.Create(CultureInfo.InvariantCulture, $"Size check failed: expected {size} bytes, have {Map.WrittenBytes} written and {file.Length} on disk."));
        }

        var tempPath = file.Path;
        file.Dispose();
        _file = null;

        // The user may have changed the name or folder while the download ran: use the saved values.
        var current = _repository.Get(Id) ?? _download;
        var directory = current.SavePath;
        Directory.CreateDirectory(directory);
        var name = FileNameSanitizer.Sanitize(string.IsNullOrWhiteSpace(current.FileName) ? _download.FileName : current.FileName);
        if (!current.OverwriteExisting)
        {
            name = FileNameSanitizer.MakeUnique(directory, name);
        }

        var target = Path.Combine(directory, name);
        await Task.Run(() => File.Move(tempPath, target, overwrite: current.OverwriteExisting)).ConfigureAwait(false);

        if (Options.KeepServerFileDate && _download.LastModified is { } modified)
        {
            File.SetLastWriteTimeUtc(target, modified);
        }

        // Checksum, Mark of the Web and virus scan; a stop request doesn't interrupt them (the file is already in place).
        var warning = await FileFinisher.FinishAsync(current, target, Options, SetStatus, _logger, CancellationToken.None).ConfigureAwait(false);
        DeleteTempDirectory();
        _download.FileName = name;
        _download.SavePath = directory;
        _download.Size = size;
        _download.Downloaded = size;
        _download.Status = DownloadStatus.Completed;
        _download.CompletedAt = DateTime.UtcNow;
        _download.LastError = null;
        _repository.SaveSegments(Id, []);
        _repository.MarkCompleted(Id, name, size, _download.CompletedAt.Value);
        _logger.LogInformation("Download {Id} complete: {Path}", Id, target);
        Finish(DownloadStatus.Completed, warning, warning is null ? DownloadErrorKind.None : DownloadErrorKind.ChecksumMismatch);
    }

    private Task StoppedAsync()
    {
        switch (_stopReason)
        {
            case StopReason.Crash:
                _onFinished(this);
                return Task.CompletedTask;
            case StopReason.Remove:
                _file?.Dispose();
                _file = null;
                if (!Options.KeepTempFilesAfterCancel)
                {
                    DeleteTempDirectory();
                }

                Finish(DownloadStatus.Paused, null, DownloadErrorKind.None, persist: false);
                return Task.CompletedTask;
            default:
                SafeCheckpoint();
                Finish(DownloadStatus.Paused, null, DownloadErrorKind.None);
                return Task.CompletedTask;
        }
    }

    private void Failed(DownloadException error)
    {
        _logger.LogWarning(error, "Download {Id} stopped: {Kind} {Message}", Id, error.Kind, error.Message);
        SafeCheckpoint();

        // "Not enough disk space" pauses the download so it can simply be resumed after freeing space.
        var status = error.Kind == DownloadErrorKind.DiskFull ? DownloadStatus.Paused : DownloadStatus.Error;
        Finish(status, error.Message, error.Kind);
    }

    private void SafeCheckpoint()
    {
        try
        {
            Checkpoint();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Final checkpoint failed for download {Id}", Id);
        }
    }

    private void Finish(DownloadStatus status, string? error, DownloadErrorKind kind, bool persist = true)
    {
        _status = status;
        _message = error;
        _download.Status = status;
        _download.LastError = error;
        if (persist)
        {
            _repository.UpdateStatus(Id, status, error);
        }

        // Unregister first so handlers of the event can immediately start the download again.
        _onFinished(this);
        StateChanged?.Invoke(this, new DownloadStateChangedEventArgs(Id, status, kind, error));
    }

    private void SetStatus(DownloadStatus status, string message)
    {
        _status = status;
        _message = message;
        _download.Status = status;
        _repository.UpdateProgress(Id, _download.Downloaded, status, _download.LastTryAt);
        StateChanged?.Invoke(this, new DownloadStateChangedEventArgs(Id, status, DownloadErrorKind.None, message));
    }

    private RequestContext RequestContextFor() => RequestContext.For(_download, Options.UserAgent, Options.Timeout);

    private string TempDirectory => Path.Combine(Options.TempDirectory, Id.ToString(CultureInfo.InvariantCulture));

    private string TempPathFor(string fileName)
    {
        var name = FileNameSanitizer.Truncate(FileNameSanitizer.Sanitize(fileName), 120);
        return Path.Combine(TempDirectory, name + ".ngpart");
    }

    private string? FindTempFile() =>
        Directory.Exists(TempDirectory) ? Directory.EnumerateFiles(TempDirectory, "*.ngpart").FirstOrDefault() : null;

    private void DeleteTempDirectory()
    {
        try
        {
            if (Directory.Exists(TempDirectory))
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete temp folder {Folder}", TempDirectory);
        }
    }
}
