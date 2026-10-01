using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Models;

namespace NovaGet.Core.Services.Queues;

public enum QueueStopReason
{
    /// <summary>Every file finished or ran out of retries: "after the queue finishes" actions apply.</summary>
    Finished,

    /// <summary>Stop queue, Stop all, or the user stopped files of the queue while it ran.</summary>
    Stopped,

    /// <summary>The schedule's stop time.</summary>
    StopTime,
}

public sealed class QueueEventArgs(DownloadQueue queue, QueueStopReason? reason = null) : EventArgs
{
    public DownloadQueue Queue { get; } = queue;

    /// <summary>Null for <see cref="IQueueManager.QueueStarted"/>.</summary>
    public QueueStopReason? Reason { get; } = reason;
}

/// <summary>Runs download queues (section 10): N files at a time in queue order, schedules, and the synchronization queue.</summary>
public interface IQueueManager
{
    bool IsRunning(long queueId);

    IReadOnlyCollection<long> RunningQueueIds { get; }

    /// <summary>Starts a queue now. False if it doesn't exist or already runs.</summary>
    bool Start(long queueId);

    /// <summary>Starts a queue by name (command line <c>/startqueue</c>), ignoring case.</summary>
    bool Start(string name);

    Task StopAsync(long queueId);

    Task StopAsync(string name);

    Task StopAllAsync();

    /// <summary>Starts queues marked "Start queue on NovaGet startup" (and the synchronization queue's "At startup").</summary>
    void OnAppStarted();

    /// <summary>The scheduler tick (every 15 s): start and stop times, retries, synchronization checks.</summary>
    void Tick();

    event EventHandler<QueueEventArgs>? QueueStarted;

    event EventHandler<QueueEventArgs>? QueueStopped;

    /// <summary>Runs (on the starting thread) before a queue's first file starts, e.g. to connect a VPN.</summary>
    Action<DownloadQueue>? BeforeStart { get; set; }
}

public sealed class QueueManager : IQueueManager, IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private readonly IDownloadService _downloads;
    private readonly IQueueRepository _queues;
    private readonly IDownloadProber _prober;
    private readonly Func<EngineOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<long, RunState> _running = [];
    private DateTimeOffset _lastTick;
    private int _syncing;

    public QueueManager(
        IDownloadService downloads,
        IQueueRepository queues,
        IDownloadProber prober,
        Func<EngineOptions> options,
        TimeProvider? time = null,
        ILogger<QueueManager>? logger = null)
    {
        _downloads = downloads;
        _queues = queues;
        _prober = prober;
        _options = options;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<QueueManager>.Instance;
        _lastTick = _time.GetUtcNow();
        _downloads.StateChanged += OnStateChanged;
        _downloads.Changed += OnListChanged;
    }

    public event EventHandler<QueueEventArgs>? QueueStarted;

    public event EventHandler<QueueEventArgs>? QueueStopped;

    public Action<DownloadQueue>? BeforeStart { get; set; }

    /// <summary>Delay before a failed file of a running queue is tried again.</summary>
    public TimeSpan RetryDelay { get; init; } = TickInterval;

    public IReadOnlyCollection<long> RunningQueueIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _running.Keys];
            }
        }
    }

    public bool IsRunning(long queueId)
    {
        lock (_gate)
        {
            return _running.ContainsKey(queueId);
        }
    }

    public bool Start(long queueId)
    {
        var queue = _queues.Get(queueId);
        if (queue is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_running.TryAdd(queueId, new RunState { NextSync = _time.GetUtcNow() }))
            {
                return false;
            }
        }

        _logger.LogInformation("Queue {Queue} started", queue.Name);
        try
        {
            BeforeStart?.Invoke(queue);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Preparing queue {Queue} failed", queue.Name);
        }

        QueueStarted?.Invoke(this, new QueueEventArgs(queue));
        if (queue.IsSyncQueue)
        {
            _ = SynchronizeAsync(queue);
        }
        else
        {
            Fill(queueId);
        }

        return true;
    }

    public bool Start(string name) => Find(name) is { } queue && Start(queue.Id);

    public Task StopAsync(long queueId) => StopAsync(queueId, QueueStopReason.Stopped);

    public Task StopAsync(string name) => Find(name) is { } queue ? StopAsync(queue.Id) : Task.CompletedTask;

    public async Task StopAllAsync()
    {
        foreach (var id in RunningQueueIds)
        {
            await StopAsync(id).ConfigureAwait(false);
        }
    }

    public void OnAppStarted()
    {
        foreach (var queue in _queues.GetAll())
        {
            if (queue.Schedule.StartOnAppStartup || (queue.IsSyncQueue && queue.Schedule.SyncAtStartup))
            {
                Start(queue.Id);
            }
        }
    }

    public void Tick()
    {
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var from = TimeZoneInfo.ConvertTime(_lastTick, zone).DateTime;
        var to = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        _lastTick = now;

        foreach (var queue in _queues.GetAll())
        {
            var schedule = queue.Schedule;
            var running = IsRunning(queue.Id);
            if (running && schedule.StopEnabled && ScheduleMath.Crossed(schedule, schedule.StopTime, from, to))
            {
                _logger.LogInformation("Queue {Queue} reached its stop time", queue.Name);
                _ = StopAsync(queue.Id, QueueStopReason.StopTime);
            }
            else if (!running && schedule.StartEnabled && ScheduleMath.Crossed(schedule, schedule.StartTime, from, to))
            {
                _logger.LogInformation("Queue {Queue} reached its start time", queue.Name);
                Start(queue.Id);
            }
            else if (running && queue.IsSyncQueue)
            {
                bool due;
                lock (_gate)
                {
                    due = _running.TryGetValue(queue.Id, out var state) && now >= state.NextSync;
                }

                if (due)
                {
                    _ = SynchronizeAsync(queue);
                }
                else
                {
                    Fill(queue.Id);
                }
            }
            else if (running)
            {
                Fill(queue.Id); // failed files waiting for their retry
            }
        }
    }

    public void Dispose()
    {
        _downloads.StateChanged -= OnStateChanged;
        _downloads.Changed -= OnListChanged;
    }

    /// <summary>Synchronization queue: checks finished files for changes on the server, then downloads what changed.</summary>
    internal async Task<int> SynchronizeAsync(DownloadQueue queue)
    {
        if (Interlocked.Exchange(ref _syncing, 1) == 1)
        {
            return 0;
        }

        var changed = 0;
        try
        {
            var options = _options();
            foreach (var download in _downloads.GetAll().Where(d => d.QueueId == queue.Id && d.Status == DownloadStatus.Completed))
            {
                if (!IsRunning(queue.Id))
                {
                    break;
                }

                ProbeResult probe;
                try
                {
                    probe = await _prober.ProbeAsync(RequestContext.For(download, options.UserAgent, options.Timeout), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is DownloadException or HttpRequestException or IOException or OperationCanceledException or UriFormatException)
                {
                    _logger.LogInformation("Synchronization check of {File} failed: {Error}", download.FileName, ex.Message);
                    continue;
                }

                var path = download.FullPath;
                if (!SyncComparer.HasChanged(download, probe, File.Exists(path)))
                {
                    continue;
                }

                _logger.LogInformation("{File} changed on the server; downloading it again", download.FileName);
                if (queue.Schedule.SyncKeepBackup && File.Exists(path))
                {
                    try
                    {
                        File.Copy(path, path + ".bak", overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Could not keep a backup of {File}", path);
                    }
                }

                await _downloads.RedownloadAsync(download.Id, start: false).ConfigureAwait(false);
                changed++;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_running.TryGetValue(queue.Id, out var state))
                {
                    state.NextSync = _time.GetUtcNow() + TimeSpan.FromMinutes(Math.Max(1, queue.SyncIntervalMinutes));
                }
            }

            Volatile.Write(ref _syncing, 0);
        }

        Fill(queue.Id);
        return changed;
    }

    private DownloadQueue? Find(string name)
    {
        name = name.Trim();
        return _queues.GetAll().FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? (name.Equals("main", StringComparison.OrdinalIgnoreCase) ? _queues.Get(DownloadQueue.MainQueueId) : null);
    }

    private async Task StopAsync(long queueId, QueueStopReason reason)
    {
        var queue = _queues.Get(queueId);
        List<long> toStop;
        lock (_gate)
        {
            if (!_running.TryGetValue(queueId, out var state) || state.Stopping)
            {
                return;
            }

            state.Stopping = true;
            toStop = [.. state.InFlight];
        }

        toStop.AddRange(_downloads.GetAll().Where(d => d.QueueId == queueId && d.Status.IsActive()).Select(d => d.Id));
        foreach (var id in toStop.Distinct())
        {
            await _downloads.StopAsync(id).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _running.Remove(queueId);
        }

        _logger.LogInformation("Queue {Queue} stopped ({Reason})", queue?.Name ?? queueId.ToString(System.Globalization.CultureInfo.InvariantCulture), reason);
        if (queue is not null)
        {
            QueueStopped?.Invoke(this, new QueueEventArgs(queue, reason));
        }
    }

    /// <summary>Starts waiting files until the queue's "files at the same time" are running; finishes the queue when nothing is left.</summary>
    private void Fill(long queueId)
    {
        var queue = _queues.Get(queueId);
        List<long> toStart;
        QueueStopReason? finished = null;
        lock (_gate)
        {
            if (!_running.TryGetValue(queueId, out var state) || state.Stopping)
            {
                return;
            }

            if (queue is null)
            {
                _running.Remove(queueId);
                return;
            }

            state.Retries = Math.Max(0, queue.Schedule.RetriesPerFile);
            var now = _time.GetUtcNow();
            var items = _downloads.GetAll()
                .Where(d => d.QueueId == queueId)
                .OrderBy(d => d.QueuePosition)
                .ThenBy(d => d.Id)
                .ToList();
            state.InFlight.RemoveWhere(id => !items.Exists(d => d.Id == id));

            var active = items.Count(d => d.Status.IsActive() || state.InFlight.Contains(d.Id));
            var waiting = items
                .Where(d => d.Status.IsResumable() && !state.Skipped.Contains(d.Id) && !state.InFlight.Contains(d.Id))
                .ToList();
            var ready = waiting.Where(d => !state.RetryAt.TryGetValue(d.Id, out var at) || at <= now);
            toStart = [.. ready.Take(Math.Max(0, Math.Max(1, queue.SimultaneousCount) - active)).Select(d => d.Id)];
            foreach (var id in toStart)
            {
                state.InFlight.Add(id);
                state.RetryAt.Remove(id);
            }

            if (active == 0 && waiting.Count == 0 && !queue.IsSyncQueue)
            {
                finished = state.UserStopped ? QueueStopReason.Stopped : QueueStopReason.Finished;
                _running.Remove(queueId);
            }
        }

        var refused = false;
        foreach (var id in toStart)
        {
            bool started;
            try
            {
                started = _downloads.Start(id, startedByQueue: true);
            }
            catch (KeyNotFoundException)
            {
                started = false;
            }

            if (!started)
            {
                refused = true;
                lock (_gate)
                {
                    if (_running.TryGetValue(queueId, out var state))
                    {
                        state.InFlight.Remove(id);
                        state.Skipped.Add(id);
                    }
                }
            }
        }

        if (refused)
        {
            Fill(queueId);
        }

        if (finished is { } reason)
        {
            _logger.LogInformation("Queue {Queue} finished ({Reason})", queue!.Name, reason);
            QueueStopped?.Invoke(this, new QueueEventArgs(queue, reason));
        }
    }

    private void OnStateChanged(object? sender, DownloadStateChangedEventArgs e)
    {
        if (e.Status is not (DownloadStatus.Completed or DownloadStatus.Error or DownloadStatus.Paused))
        {
            return;
        }

        long? queueId = null;
        lock (_gate)
        {
            foreach (var (id, state) in _running)
            {
                if (!state.InFlight.Remove(e.Id))
                {
                    continue;
                }

                queueId = id;
                if (state.Stopping)
                {
                    break;
                }

                if (e.Status == DownloadStatus.Error)
                {
                    var failures = state.Failures[e.Id] = state.Failures.GetValueOrDefault(e.Id) + 1;
                    if (failures <= state.Retries)
                    {
                        state.RetryAt[e.Id] = _time.GetUtcNow() + RetryDelay;
                    }
                    else
                    {
                        state.Skipped.Add(e.Id);
                    }
                }
                else if (e.Status == DownloadStatus.Paused)
                {
                    // The user stopped this file: the queue moves on without it.
                    state.Skipped.Add(e.Id);
                    state.UserStopped = true;
                }

                break;
            }
        }

        if (queueId is { } queue)
        {
            ThreadPool.QueueUserWorkItem(_ => Fill(queue));
        }
    }

    /// <summary>Files added to (or moved within) a running queue start without waiting for the next tick.</summary>
    private void OnListChanged(object? sender, DownloadListChangedEventArgs e)
    {
        if (e.Change == DownloadListChange.Removed)
        {
            return;
        }

        foreach (var id in RunningQueueIds)
        {
            ThreadPool.QueueUserWorkItem(_ => Fill(id));
        }
    }

    private sealed class RunState
    {
        public HashSet<long> InFlight { get; } = [];

        public Dictionary<long, int> Failures { get; } = [];

        public Dictionary<long, DateTimeOffset> RetryAt { get; } = [];

        public HashSet<long> Skipped { get; } = [];

        public int Retries { get; set; } = 10;

        public bool UserStopped { get; set; }

        public bool Stopping { get; set; }

        public DateTimeOffset NextSync { get; set; }
    }
}
