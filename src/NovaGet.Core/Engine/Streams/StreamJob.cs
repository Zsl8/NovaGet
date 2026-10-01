using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine.Naming;
using NovaGet.Core.Engine.Storage;
using NovaGet.Core.Models;

namespace NovaGet.Core.Engine.Streams;

/// <summary>
/// Downloads an HLS or DASH stream (section 4.8): reads the manifest for the chosen quality, fetches the segments in
/// parallel (each retried on its own) into <c>&lt;TempDir&gt;\&lt;id&gt;\</c>, decrypts AES-128 segments, joins each
/// track and muxes the tracks with ffmpeg (<c>-c copy</c>) into MP4. Finished segments are files, so a paused or
/// crashed download continues where it was. DRM-protected streams stop with <see cref="DownloadErrorKind.ProtectedContent"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Same as DownloadJob: the stop token source is never disposed to avoid races with late Stop() calls.")]
internal sealed class StreamJob : IEngineJob
{
    private const string FingerprintFile = "stream.id";
    private static readonly string[] s_mp4Extensions = [".mp4", ".m4v", ".m4a", ".mov"];

    private readonly Download _download;
    private readonly IDownloadRepository _repository;
    private readonly IReadOnlyList<ITransferProtocol> _protocols;
    private readonly StreamManifestLoader _loader;
    private readonly IStreamMuxer _muxer;
    private readonly TransferThrottle _throttle;
    private readonly TrafficCounter _traffic;
    private readonly HostConnectionLimits _hostLimits;
    private readonly ILogger _logger;
    private readonly Action<IEngineJob> _onFinished;
    private readonly CancellationTokenSource _stop = new();
    private readonly SpeedMeter _speed = new();
    private readonly ConcurrentDictionary<Uri, Lazy<Task<byte[]>>> _keys = new();
    private readonly List<WorkerState> _workers = [];
    private volatile IReadOnlyList<SegmentWork> _work = [];
    private StopReason _stopReason;
    private volatile DownloadStatus _status;
    private volatile string? _message;

    public StreamJob(
        Download download,
        EngineOptions options,
        IDownloadRepository repository,
        IReadOnlyList<ITransferProtocol> protocols,
        IStreamMuxer muxer,
        JobServices services,
        bool startedByQueue,
        ILogger logger,
        Action<IEngineJob> onFinished)
    {
        _download = download;
        Options = options;
        _repository = repository;
        _protocols = protocols;
        _loader = new StreamManifestLoader(protocols);
        _muxer = muxer;
        _traffic = services.Traffic;
        _hostLimits = services.HostLimits;
        _logger = logger;
        _onFinished = onFinished;
        _status = download.Status;
        var limitKBps = download.SpeedLimitKBps ?? HostPattern.Lookup(options.HostSpeedLimitsKBps, Host) ?? 0;
        _throttle = new TransferThrottle(Math.Max(0, limitKBps) * 1024L, services.SpeedLimits, startedByQueue);
    }

    public long Id => _download.Id;

    public EngineOptions Options { get; }

    public Task Completion { get; private set; } = Task.CompletedTask;

    public event EventHandler<DownloadStateChangedEventArgs>? StateChanged;

    private string Host => Uri.TryCreate(_download.Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    private string TempDirectory => Path.Combine(Options.TempDirectory, Id.ToString(CultureInfo.InvariantCulture));

    public void Start() => Completion = Task.Run(RunAsync);

    public void Stop(StopReason reason)
    {
        if (_stopReason == StopReason.None)
        {
            _stopReason = reason;
        }

        _stop.Cancel();
    }

    public void SetSpeedLimit(int? kilobytesPerSecond)
    {
        _download.SpeedLimitKBps = kilobytesPerSecond;
        _throttle.SetLimit(kilobytesPerSecond);
    }

    public DownloadProgress GetProgress()
    {
        var work = _work;
        var downloaded = Downloaded(work);
        var size = EstimatedSize(work, downloaded);
        var speed = _status == DownloadStatus.Receiving ? _speed.BytesPerSecond : 0;
        ConnectionProgress[] connections;
        int active;
        lock (_workers)
        {
            connections = [.. _workers.Select(w => w.Snapshot())];
            active = _workers.Count(w => w.IsActive);
        }

        return new DownloadProgress
        {
            Id = Id,
            Status = _status,
            Size = size,
            Downloaded = downloaded,
            BytesPerSecond = speed,
            TimeLeft = SpeedMeter.TimeLeft(size, downloaded, speed),
            ResumeCapable = true,
            ActiveConnections = active,
            Connections = connections,
            Segments = SegmentBar(work, size),
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
            // The playlist is read from the current address, so "Refresh download address" works for streams too.
            var selection = (StreamSelection.FromJson(_download.StreamManifestJson)
                ?? new StreamSelection { Kind = StreamKind.Hls, ManifestUrl = _download.Url }) with { ManifestUrl = _download.Url };
            var tracks = await _loader.ResolveAsync(selection, BaseContext(), token).ConfigureAwait(false);
            if (tracks.Sum(t => t.Segments.Count) == 0)
            {
                throw new DownloadException(DownloadErrorKind.Unknown, "The stream has no segments.");
            }

            Prepare(tracks, selection);
            if (_work.Any(w => !w.Done))
            {
                SetStatus(DownloadStatus.Receiving, "Receiving data...");
                await ReceiveAllAsync(token).ConfigureAwait(false);
            }

            await FinishAsync(tracks, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            Stopped();
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
            _logger.LogError(ex, "File system error in stream download {Id}", Id);
            Failed(new DownloadException(DownloadErrorKind.FileSystem, ex.Message, inner: ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in stream download {Id}", Id);
            Failed(new DownloadException(DownloadErrorKind.Unknown, ex.Message, inner: ex));
        }
    }

    /// <summary>Lists the work and marks what earlier runs finished (segment files, or a joined track).</summary>
    private void Prepare(IReadOnlyList<MediaTrack> tracks, StreamSelection selection)
    {
        Directory.CreateDirectory(TempDirectory);
        var fingerprint = Fingerprint(tracks);
        var idPath = Path.Combine(TempDirectory, FingerprintFile);
        if (!File.Exists(idPath) || File.ReadAllText(idPath) != fingerprint)
        {
            // Another quality, or the stream changed since the last run: start over.
            foreach (var file in Directory.EnumerateFiles(TempDirectory))
            {
                File.Delete(file);
            }

            File.WriteAllText(idPath, fingerprint);
        }

        var work = new List<SegmentWork>();
        for (var t = 0; t < tracks.Count; t++)
        {
            var track = tracks[t];
            var items = new List<SegmentWork>();
            if (track.Init is { } init)
            {
                items.Add(new SegmentWork(t, -1, init, SegmentPath(t, -1)));
            }

            items.AddRange(track.Segments.Select((s, i) => new SegmentWork(t, i, s, SegmentPath(t, i))));
            var joined = TrackPath(t, track);
            var joinedLength = File.Exists(joined) ? new FileInfo(joined).Length : -1;
            foreach (var item in items)
            {
                if (joinedLength >= 0)
                {
                    item.MarkDone(joinedLength / items.Count);
                }
                else if (File.Exists(item.Path))
                {
                    item.MarkDone(new FileInfo(item.Path).Length);
                }
            }

            work.AddRange(items);
        }

        _work = work;
        var done = work.Count(w => w.Done);
        if (done > 0)
        {
            _logger.LogInformation("Resuming stream download {Id}: {Done} of {Total} segments already here", Id, done, work.Count);
        }

        if (string.IsNullOrWhiteSpace(_download.FileName))
        {
            var fromUrl = FileNameResolver.NameFromUrl(new Uri(selection.ManifestUrl)) ?? "video";
            _download.FileName = Path.GetFileNameWithoutExtension(fromUrl) + ".mp4";
        }

        _download.ResumeCapable = true;
        _repository.UpdateProbe(Id, _download.Url, _download.FileName, _download.Size, true, null, null);
    }

    private async Task ReceiveAllAsync(CancellationToken token)
    {
        var pending = new ConcurrentQueue<SegmentWork>(_work.Where(w => !w.Done));
        var count = Math.Clamp(Math.Min(MaxConnections(), pending.Count), 1, 32);
        _logger.LogInformation("Stream download {Id}: {Count} segments, {Connections} connection(s)", Id, pending.Count, count);
        using var connections = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var background = CancellationTokenSource.CreateLinkedTokenSource(token);
        var housekeeping = HousekeepingAsync(background.Token);
        try
        {
            var running = new List<Task>();
            lock (_workers)
            {
                _workers.Clear();
                for (var n = 1; n <= count; n++)
                {
                    var state = new WorkerState(n);
                    _workers.Add(state);
                    running.Add(Task.Run(() => WorkerAsync(state, pending, connections.Token), CancellationToken.None));
                }
            }

            Exception? fatal = null;
            while (running.Count > 0)
            {
                var finished = await Task.WhenAny(running).ConfigureAwait(false);
                running.Remove(finished);
                if (finished.IsFaulted && fatal is null && finished.Exception!.GetBaseException() is not OperationCanceledException and var error)
                {
                    fatal = error;
                    await connections.CancelAsync().ConfigureAwait(false);
                }
            }

            if (fatal is not null)
            {
                ExceptionDispatchInfo.Capture(fatal).Throw();
            }

            token.ThrowIfCancellationRequested();
        }
        finally
        {
            await background.CancelAsync().ConfigureAwait(false);
            await housekeeping.ConfigureAwait(false);
        }

        if (_work.Any(w => !w.Done))
        {
            throw new DownloadException(DownloadErrorKind.Network, "The download ended before all segments were received.");
        }
    }

    private int MaxConnections()
    {
        var configured = _download.MaxConnections ?? HostPattern.Lookup(Options.ServerConnectionLimits, Host) ?? Options.MaxConnections;
        var learned = _hostLimits.Get(Host) ?? int.MaxValue;
        return Math.Clamp(Math.Min(configured, learned), 1, 32);
    }

    private async Task WorkerAsync(WorkerState state, ConcurrentQueue<SegmentWork> pending, CancellationToken token)
    {
        try
        {
            while (pending.TryDequeue(out var work))
            {
                token.ThrowIfCancellationRequested();
                await DownloadWithRetriesAsync(state, work, token).ConfigureAwait(false);
            }

            state.Info = "Done";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            state.Info = "Stopped";
            throw;
        }
        catch (DownloadException ex)
        {
            state.Info = "Error: " + ex.Message;
            throw;
        }
        finally
        {
            state.IsActive = false;
        }
    }

    /// <summary>Section 4.8: each segment is retried on its own, with the usual backoff.</summary>
    private async Task DownloadWithRetriesAsync(WorkerState state, SegmentWork work, CancellationToken token)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await DownloadSegmentAsync(state, work, token).ConfigureAwait(false);
                return;
            }
            catch (DownloadException ex) when (ex.IsTransient && !token.IsCancellationRequested)
            {
                attempt++;
                _logger.LogInformation("Stream download {Id}, {Segment}: {Error} (failure {Count}/{Max})", Id, work.Label, ex.Message, attempt, Options.MaxRetries);
                if (attempt > Options.MaxRetries)
                {
                    throw;
                }

                var delay = Options.RetryDelay(attempt);
                state.Info = string.Create(CultureInfo.InvariantCulture, $"{work.Label}: {ex.Message} Retrying in {Math.Ceiling(delay.TotalSeconds):0} sec...");
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
    }

    private async Task DownloadSegmentAsync(WorkerState state, SegmentWork work, CancellationToken token)
    {
        var segment = work.Segment;
        var protocol = ProtocolFor(segment.Url);
        var part = work.Path + ".part";
        var range = segment.Range;

        // An unencrypted segment continues from what an earlier attempt wrote; an encrypted one starts over.
        var have = segment.Key is null && File.Exists(part) ? new FileInfo(part).Length : 0;
        if (range is not null && have >= range.Length)
        {
            have = 0;
        }

        var start = (range?.Offset ?? 0) + have;
        var useRange = range is not null || have > 0;
        state.Info = work.Label + ": Send GET...";
        var request = new TransferRequest(StreamRequests.For(BaseContext(), segment.Url), start, range?.End ?? -1, null, useRange);
        var response = await protocol.OpenAsync(request, token).ConfigureAwait(false);
        long written;
        await using (response.ConfigureAwait(false))
        {
            long skip = 0;
            if (useRange && !response.IsPartial)
            {
                // The server ignored the range and sends the resource from byte 0.
                if (range is null)
                {
                    have = 0;
                }
                else
                {
                    skip = start;
                }
            }
            else if (response.IsPartial && response.Start != start)
            {
                throw new DownloadException(DownloadErrorKind.HttpError,
                    string.Create(CultureInfo.InvariantCulture, $"The server sent data from byte {response.Start} instead of {start}."));
            }

            long? limit = range is null ? null : range.Length - have;
            work.SetReceived(have);
            state.Info = work.Label + ": Receiving data...";
            var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                written = await CopyAsync(state, work, response.Body, file, skip, limit, token).ConfigureAwait(false);
                await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (limit is { } expected && written < expected)
            {
                throw new DownloadException(DownloadErrorKind.Network, "The connection closed before the segment was complete.");
            }

            if (range is null && response.TotalSize is { } total && have + written != total)
            {
                throw new DownloadException(DownloadErrorKind.Network, "The connection closed before the segment was complete.");
            }
        }

        if (segment.Key is { } key)
        {
            state.Info = work.Label + ": Decrypting...";
            await DecryptAsync(part, work.Path, key, segment.Sequence, token).ConfigureAwait(false);
            File.Delete(part);
        }
        else
        {
            File.Move(part, work.Path, overwrite: true);
        }

        work.MarkDone(new FileInfo(work.Path).Length);
    }

    private async Task<long> CopyAsync(WorkerState state, SegmentWork work, Stream body, FileStream file, long skip, long? limit, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        long written = 0;
        try
        {
            while (limit is null || written < limit)
            {
                var want = Math.Min(buffer.Length, _throttle.MaxReadSize);
                if (skip > 0)
                {
                    want = (int)Math.Min(want, skip);
                }
                else if (limit is { } max)
                {
                    want = (int)Math.Min(want, max - written);
                }

                readTimeout.CancelAfter(Options.Timeout);
                int read;
                try
                {
                    read = await body.ReadAsync(buffer.AsMemory(0, want), readTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new DownloadException(DownloadErrorKind.Timeout, $"No data received for {Options.Timeout.TotalSeconds:0} seconds.");
                }
                catch (IOException ex)
                {
                    throw new DownloadException(DownloadErrorKind.Network, "The connection was interrupted.", inner: ex);
                }

                if (read == 0)
                {
                    break;
                }

                _traffic.Add(read);
                await _throttle.ThrottleAsync(read, token).ConfigureAwait(false);
                if (skip > 0)
                {
                    skip -= read;
                    continue;
                }

                // Never cancel a write half-way: the part file must hold whole reads.
                await file.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
                written += read;
                work.AddReceived(read);
                state.Add(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return written;
    }

    /// <summary>AES-128 CBC with PKCS#7 padding; the IV is the playlist's or the segment's sequence number (RFC 8216 5.2).</summary>
    private async Task DecryptAsync(string source, string target, SegmentKey key, long sequence, CancellationToken token)
    {
        var keyBytes = await KeyAsync(key.KeyUrl, token).ConfigureAwait(false);
        var iv = key.Iv ?? SequenceIv(sequence);
        var output = target + ".dec";
        try
        {
            using var aes = Aes.Create();
            using var decryptor = aes.CreateDecryptor(keyBytes, iv);
            var input = File.OpenRead(source);
            await using (input.ConfigureAwait(false))
            {
                var crypto = new CryptoStream(input, decryptor, CryptoStreamMode.Read);
                await using (crypto.ConfigureAwait(false))
                {
                    var file = File.Create(output);
                    await using (file.ConfigureAwait(false))
                    {
                        await crypto.CopyToAsync(file, token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (CryptographicException ex)
        {
            File.Delete(source);
            File.Delete(output);
            throw new DownloadException(DownloadErrorKind.Unknown, "A stream segment could not be decrypted.", inner: ex);
        }

        File.Move(output, target, overwrite: true);
    }

    internal static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), sequence);
        return iv;
    }

    /// <summary>Each key is fetched once per run (a failed fetch is tried again by the next segment that needs it).</summary>
    private async Task<byte[]> KeyAsync(Uri url, CancellationToken token)
    {
        var lazy = _keys.GetOrAdd(url, u => new Lazy<Task<byte[]>>(() => FetchKeyAsync(u, _stop.Token)));
        try
        {
            return await lazy.Value.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception) when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            _keys.TryRemove(KeyValuePair.Create(url, lazy));
            throw;
        }
    }

    private async Task<byte[]> FetchKeyAsync(Uri url, CancellationToken token)
    {
        var protocol = ProtocolFor(url);
        var response = await protocol.OpenAsync(new TransferRequest(StreamRequests.For(BaseContext(), url), 0, -1, null, UseRange: false), token).ConfigureAwait(false);
        await using (response.ConfigureAwait(false))
        {
            var key = new byte[17];
            var length = 0;
            int read;
            while (length < key.Length && (read = await response.Body.ReadAsync(key.AsMemory(length), token).ConfigureAwait(false)) > 0)
            {
                length += read;
            }

            if (length != 16)
            {
                throw new DownloadException(DownloadErrorKind.Unknown, "The stream's decryption key is not valid.");
            }

            return key[..16];
        }
    }

    /// <summary>Joins each track, muxes them (or keeps them as they are without ffmpeg) and moves the result into place.</summary>
    private async Task FinishAsync(IReadOnlyList<MediaTrack> tracks, CancellationToken token)
    {
        SetStatus(DownloadStatus.Merging, "Merging...");
        EnsureFreeSpace();
        var inputs = new List<MuxInput>();
        for (var t = 0; t < tracks.Count; t++)
        {
            var path = TrackPath(t, tracks[t]);
            if (!File.Exists(path))
            {
                await JoinAsync(t, path, token).ConfigureAwait(false);
            }

            inputs.Add(new MuxInput(path, tracks[t].Kind));
        }

        var current = _repository.Get(Id) ?? _download;
        var directory = current.SavePath;
        Directory.CreateDirectory(directory);
        var name = FileNameSanitizer.Sanitize(string.IsNullOrWhiteSpace(current.FileName) ? _download.FileName : current.FileName);
        var results = new List<(string Source, string Name)>();
        if (_muxer.IsAvailable)
        {
            var merged = Path.Combine(TempDirectory, "output.mp4");
            try
            {
                await _muxer.MuxAsync(inputs, merged, token).ConfigureAwait(false);
                results.Add((merged, Mp4Name(name)));
            }
            catch (StreamMuxException ex)
            {
                _logger.LogWarning(ex, "Stream download {Id}: merging failed; the tracks are kept as downloaded", Id);
            }
        }
        else
        {
            _logger.LogInformation("Stream download {Id}: ffmpeg is not available; the tracks are kept as downloaded", Id);
        }

        if (results.Count == 0)
        {
            results.AddRange(RawResults(tracks, inputs, name));
        }

        var finalNames = new List<string>();
        foreach (var (source, wanted) in results)
        {
            var finalName = current.OverwriteExisting ? wanted : FileNameSanitizer.MakeUnique(directory, wanted);
            await Task.Run(() => File.Move(source, Path.Combine(directory, finalName), overwrite: current.OverwriteExisting), CancellationToken.None).ConfigureAwait(false);
            finalNames.Add(finalName);
        }

        // Checksum (of the main file), Mark of the Web and virus scan for every file delivered.
        string? warning = null;
        for (var i = 0; i < finalNames.Count; i++)
        {
            var subject = current;
            if (i > 0)
            {
                subject = current.Clone();
                subject.ChecksumExpected = null;
            }

            warning ??= await FileFinisher.FinishAsync(subject, Path.Combine(directory, finalNames[i]), Options, SetStatus, _logger, CancellationToken.None).ConfigureAwait(false);
        }

        DeleteTempDirectory();
        var primary = finalNames[0];
        var size = new FileInfo(Path.Combine(directory, primary)).Length;
        _download.FileName = primary;
        _download.SavePath = directory;
        _download.Size = size;
        _download.Downloaded = size;
        _download.Status = DownloadStatus.Completed;
        _download.CompletedAt = DateTime.UtcNow;
        _download.LastError = null;
        _repository.SaveSegments(Id, []);
        _repository.MarkCompleted(Id, primary, size, _download.CompletedAt.Value);
        _logger.LogInformation("Stream download {Id} complete: {Path}", Id, Path.Combine(directory, primary));
        Finish(DownloadStatus.Completed, warning, warning is null ? DownloadErrorKind.None : DownloadErrorKind.ChecksumMismatch);
    }

    /// <summary>Without ffmpeg (or when it fails): the joined tracks themselves; TS stays TS, fragmented MP4 is playable as is.</summary>
    private static IEnumerable<(string Source, string Name)> RawResults(IReadOnlyList<MediaTrack> tracks, List<MuxInput> inputs, string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        for (var t = 0; t < tracks.Count; t++)
        {
            var ts = tracks[t].Container == "ts";
            if (t == 0)
            {
                yield return (inputs[t].Path, ts ? baseName + ".ts" : Mp4Name(name));
            }
            else
            {
                var suffix = tracks[t].Kind == TrackKind.Audio ? " (audio)" : string.Create(CultureInfo.InvariantCulture, $" ({t + 1})");
                yield return (inputs[t].Path, baseName + suffix + (ts ? ".ts" : tracks[t].Kind == TrackKind.Audio ? ".m4a" : ".mp4"));
            }
        }
    }

    private static string Mp4Name(string name) =>
        s_mp4Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) ? name : Path.GetFileNameWithoutExtension(name) + ".mp4";

    /// <summary>Init segment then media segments, in order, into one file; the segment files are deleted afterwards.</summary>
    private async Task JoinAsync(int track, string path, CancellationToken token)
    {
        var items = _work.Where(w => w.Track == track).OrderBy(w => w.Index).ToList();
        var part = path + ".part";
        var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous);
        await using (output.ConfigureAwait(false))
        {
            foreach (var item in items)
            {
                var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (input.ConfigureAwait(false))
                {
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                }
            }
        }

        File.Move(part, path, overwrite: true);
        foreach (var item in items)
        {
            File.Delete(item.Path);
        }
    }

    private void EnsureFreeSpace()
    {
        // Joining and muxing each write about one more copy of what was downloaded.
        var needed = Downloaded(_work) + Options.FreeSpaceMargin;
        if (DiskSpace.AvailableBytes(Options.TempDirectory) is { } available && available < needed)
        {
            throw new DownloadException(DownloadErrorKind.DiskFull,
                string.Create(CultureInfo.InvariantCulture, $"Not enough disk space to merge the stream: {needed / (1024 * 1024)} MB needed, {available / (1024 * 1024)} MB free."));
        }
    }

    private async Task HousekeepingAsync(CancellationToken token)
    {
        var lastCheckpoint = DateTime.UtcNow;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(Options.SpeedSampleInterval, token).ConfigureAwait(false);
                _speed.Sample(Downloaded(_work), DateTime.UtcNow);
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
    }

    /// <summary>Segment files are the resume state; the database only keeps the byte count for the list.</summary>
    private void Checkpoint()
    {
        try
        {
            _download.Downloaded = Downloaded(_work);
            _repository.UpdateProgress(Id, _download.Downloaded, _status, _download.LastTryAt);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Checkpoint failed for stream download {Id}", Id);
        }
    }

    private void Stopped()
    {
        switch (_stopReason)
        {
            case StopReason.Crash:
                _onFinished(this);
                return;
            case StopReason.Remove:
                if (!Options.KeepTempFilesAfterCancel)
                {
                    DeleteTempDirectory();
                }

                Finish(DownloadStatus.Paused, null, DownloadErrorKind.None, persist: false);
                return;
            default:
                Checkpoint();
                Finish(DownloadStatus.Paused, null, DownloadErrorKind.None);
                return;
        }
    }

    private void Failed(DownloadException error)
    {
        _logger.LogWarning(error, "Stream download {Id} stopped: {Kind} {Message}", Id, error.Kind, error.Message);
        Checkpoint();
        var status = error.Kind == DownloadErrorKind.DiskFull ? DownloadStatus.Paused : DownloadStatus.Error;
        Finish(status, error.Message, error.Kind);
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

    private RequestContext BaseContext() => RequestContext.For(_download, Options.UserAgent, Options.Timeout);

    private ITransferProtocol ProtocolFor(Uri url) =>
        _protocols.FirstOrDefault(p => p.CanHandle(url))
        ?? throw new DownloadException(DownloadErrorKind.InvalidAddress, $"Unsupported address in the stream: {url.Scheme}:");

    private string SegmentPath(int track, int index) =>
        Path.Combine(TempDirectory, string.Create(CultureInfo.InvariantCulture, $"t{track}-{(index < 0 ? "init" : index.ToString("D6", CultureInfo.InvariantCulture))}.seg"));

    private string TrackPath(int track, MediaTrack media) =>
        Path.Combine(TempDirectory, string.Create(CultureInfo.InvariantCulture, $"t{track}.{(media.Container == "ts" ? "ts" : "mp4")}"));

    /// <summary>Identifies the chosen tracks' segments (addresses without query strings, which often carry expiring tokens).</summary>
    internal static string Fingerprint(IReadOnlyList<MediaTrack> tracks)
    {
        var text = new StringBuilder();
        foreach (var track in tracks)
        {
            text.Append(track.Kind).Append('|').Append(track.Container).Append('\n');
            foreach (var segment in track.Init is null ? track.Segments : [track.Init, .. track.Segments])
            {
                text.Append(segment.Url.GetLeftPart(UriPartial.Path)).Append('|').Append(segment.Range?.Offset).Append('-').Append(segment.Range?.Length).Append('\n');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    private static long Downloaded(IReadOnlyList<SegmentWork> work)
    {
        long total = 0;
        foreach (var item in work)
        {
            total += item.Received;
        }

        return total;
    }

    /// <summary>The size is only known at the end; until then, the average finished segment times the segment count.</summary>
    private static long EstimatedSize(IReadOnlyList<SegmentWork> work, long downloaded)
    {
        long doneBytes = 0;
        var done = 0;
        foreach (var item in work.Where(w => w.Done && w.Index >= 0))
        {
            doneBytes += item.Received;
            done++;
        }

        if (done == 0)
        {
            return -1;
        }

        if (work.All(w => w.Done))
        {
            return downloaded;
        }

        var media = work.Count(w => w.Index >= 0);
        return Math.Max(downloaded, (long)(doneBytes / (double)done * media));
    }

    /// <summary>The segment bar: finished runs and the segments in progress, laid out evenly over the estimated size.</summary>
    private static List<SegmentProgress> SegmentBar(IReadOnlyList<SegmentWork> work, long size)
    {
        if (size <= 0 || work.Count == 0)
        {
            return [];
        }

        var unit = size / (double)work.Count;
        var average = Math.Max(1, work.Where(w => w.Done).Select(w => w.Received).DefaultIfEmpty(1).Average());
        var bar = new List<SegmentProgress>();
        long? runStart = null;
        long runEnd = 0;
        for (var i = 0; i < work.Count; i++)
        {
            var start = (long)(i * unit);
            var end = Math.Max(start, (long)((i + 1) * unit) - 1);
            var item = work[i];
            if (item.Done)
            {
                runStart ??= start;
                runEnd = end;
                continue;
            }

            if (runStart is { } open)
            {
                bar.Add(new SegmentProgress(open, runEnd, runEnd + 1, false));
                runStart = null;
            }

            if (item.Received > 0)
            {
                var fraction = Math.Min(0.99, item.Received / average);
                bar.Add(new SegmentProgress(start, end, start + (long)(fraction * (end - start + 1)), true));
            }
        }

        if (runStart is { } last)
        {
            bar.Add(new SegmentProgress(last, runEnd, runEnd + 1, false));
        }

        return bar;
    }

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

    /// <summary>One segment (or init segment, index -1) of one track.</summary>
    private sealed class SegmentWork(int track, int index, MediaSegment segment, string path)
    {
        private long _received;
        private volatile bool _done;

        public int Track { get; } = track;

        public int Index { get; } = index;

        public MediaSegment Segment { get; } = segment;

        public string Path { get; } = path;

        public string Label => Index < 0
            ? string.Create(CultureInfo.InvariantCulture, $"Track {Track + 1} header")
            : string.Create(CultureInfo.InvariantCulture, $"Segment {Index + 1}");

        public bool Done => _done;

        public long Received => Interlocked.Read(ref _received);

        public void SetReceived(long bytes) => Interlocked.Exchange(ref _received, bytes);

        public void AddReceived(long bytes) => Interlocked.Add(ref _received, bytes);

        public void MarkDone(long bytes)
        {
            Interlocked.Exchange(ref _received, bytes);
            _done = true;
        }
    }

    /// <summary>A row of the progress dialog's connection list.</summary>
    private sealed class WorkerState(int number)
    {
        private long _downloaded;
        private volatile string _info = "Connecting...";
        private volatile bool _active = true;

        public bool IsActive
        {
            get => _active;
            set => _active = value;
        }

        public string Info
        {
            get => _info;
            set => _info = value;
        }

        public void Add(long bytes) => Interlocked.Add(ref _downloaded, bytes);

        public ConnectionProgress Snapshot() => new(number, Interlocked.Read(ref _downloaded), _info);
    }
}
