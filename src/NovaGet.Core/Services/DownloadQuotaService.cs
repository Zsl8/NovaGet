using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Models;
using NovaGet.Core.Services.Queues;
using NovaGet.Core.Settings;

namespace NovaGet.Core.Services;

public sealed class QuotaEventArgs(DateTimeOffset resumeAt, long usedBytes, long limitBytes) : EventArgs
{
    /// <summary>When the period ends and downloads continue.</summary>
    public DateTimeOffset ResumeAt { get; } = resumeAt;

    public long UsedBytes { get; } = usedBytes;

    public long LimitBytes { get; } = limitBytes;
}

/// <summary>
/// Options → Connection → "Download no more than N MBytes every H hours". A period starts with the first byte
/// downloaded after the previous one ended. When the limit is reached everything stops (running queues first, so
/// their after-queue actions don't run) until the period ends; then the stopped queues and downloads continue.
/// Downloads started while blocked are stopped again. The state survives restarts (<c>quota.json</c>).
/// </summary>
public sealed class DownloadQuotaService : IDisposable
{
    private const long Megabyte = 1024 * 1024;
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private readonly ISettingsService _settings;
    private readonly IDownloadEngine _engine;
    private readonly IDownloadService _downloads;
    private readonly IQueueManager _queues;
    private readonly string _stateFile;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _tick = new(1, 1);
    private readonly QuotaState _state;
    private long _lastTotal;

    public DownloadQuotaService(
        ISettingsService settings,
        IDownloadEngine engine,
        IDownloadService downloads,
        IQueueManager queues,
        string stateFile,
        TimeProvider? time = null,
        ILogger<DownloadQuotaService>? logger = null)
    {
        _settings = settings;
        _engine = engine;
        _downloads = downloads;
        _queues = queues;
        _stateFile = stateFile;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<DownloadQuotaService>.Instance;
        _state = Load(stateFile);
        _lastTotal = engine.Traffic.TotalBytes;
        _downloads.StateChanged += OnStateChanged;
    }

    /// <summary>Asked before stopping when "Show warning before stopping downloads" is on. True stops now; false lets this period run on.</summary>
    public Func<QuotaEventArgs, bool>? ConfirmStop { get; set; }

    public event EventHandler<QuotaEventArgs>? LimitReached;

    public event EventHandler? Resumed;

    /// <summary>A download was started while blocked and has been stopped again.</summary>
    public event EventHandler<QuotaEventArgs>? StartRefused;

    public bool IsBlocked => _state.BlockedUntil is not null;

    public DateTimeOffset? ResumeAt => _state.BlockedUntil;

    /// <summary>Bytes downloaded in the current period.</summary>
    public long UsedBytes => _state.UsedBytes;

    /// <summary>Called every second or so: counts new bytes, ends periods, blocks when over the limit.</summary>
    public async Task TickAsync()
    {
        if (!await _tick.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await TickCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _tick.Release();
        }
    }

    public void Dispose()
    {
        _downloads.StateChanged -= OnStateChanged;
        _tick.Dispose();
    }

    private async Task TickCoreAsync()
    {
        var now = _time.GetUtcNow();
        var limit = _settings.Current.Connection.DownloadLimit;
        var total = _engine.Traffic.TotalBytes;
        var delta = Math.Max(0, total - _lastTotal);
        _lastTotal = total;
        var changed = false;

        if (!limit.Enabled)
        {
            if (IsBlocked)
            {
                await ResumeAsync().ConfigureAwait(false);
            }

            if (_state.PeriodStart is not null)
            {
                _state.Reset();
                Save();
            }

            return;
        }

        var period = TimeSpan.FromHours(Math.Max(1, limit.PeriodHours));
        if (_state.PeriodStart is { } started && now >= started + period)
        {
            var wasBlocked = IsBlocked;
            _state.Reset();
            changed = true;
            if (wasBlocked)
            {
                await ResumeAsync().ConfigureAwait(false);
            }
        }

        if (delta > 0)
        {
            _state.PeriodStart ??= now;
            _state.UsedBytes += delta;
            changed = true;
        }

        var limitBytes = Math.Max(1, limit.MaxMegabytes) * Megabyte;
        if (!IsBlocked && !_state.IgnoreThisPeriod && _state.PeriodStart is { } start && _state.UsedBytes >= limitBytes)
        {
            var args = new QuotaEventArgs(start + period, _state.UsedBytes, limitBytes);
            if (limit.WarnBeforeStopping && ConfirmStop is { } confirm && !confirm(args))
            {
                _logger.LogInformation("Download limit reached; the user chose to continue this period");
                _state.IgnoreThisPeriod = true;
            }
            else
            {
                await BlockAsync(args).ConfigureAwait(false);
            }

            changed = true;
        }

        if (changed)
        {
            Save();
        }
    }

    private async Task BlockAsync(QuotaEventArgs args)
    {
        _logger.LogInformation("Download limit of {Limit} bytes reached; downloads resume at {ResumeAt}", args.LimitBytes, args.ResumeAt);
        _state.BlockedUntil = args.ResumeAt;
        _state.PausedQueues = [.. _queues.RunningQueueIds];
        _state.PausedDownloads = [.. _downloads.GetAll().Where(d => d.Status.IsActive()).Select(d => d.Id)];
        Save();
        await _queues.StopAllAsync().ConfigureAwait(false);
        await _downloads.StopAllAsync().ConfigureAwait(false);
        LimitReached?.Invoke(this, args);
    }

    private Task ResumeAsync()
    {
        _logger.LogInformation("Download limit period ended; resuming");
        var queues = _state.PausedQueues;
        var downloads = _state.PausedDownloads;
        _state.BlockedUntil = null;
        _state.PausedQueues = [];
        _state.PausedDownloads = [];
        Save();

        foreach (var queue in queues)
        {
            _queues.Start(queue);
        }

        foreach (var id in downloads)
        {
            if (_downloads.Find(id) is { } download && download.Status.IsResumable() && !(download.QueueId is { } q && queues.Contains(q)))
            {
                _downloads.Start(id);
            }
        }

        Resumed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private void OnStateChanged(object? sender, DownloadStateChangedEventArgs e)
    {
        if (e.Status != DownloadStatus.Connecting || _state.BlockedUntil is not { } resumeAt)
        {
            return;
        }

        if (!_state.PausedDownloads.Contains(e.Id))
        {
            _state.PausedDownloads = [.. _state.PausedDownloads, e.Id];
        }

        _ = _downloads.StopAsync(e.Id);
        StartRefused?.Invoke(this, new QuotaEventArgs(resumeAt, _state.UsedBytes, Math.Max(1, _settings.Current.Connection.DownloadLimit.MaxMegabytes) * Megabyte));
    }

    private static QuotaState Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<QuotaState>(File.ReadAllText(file), s_json) ?? new QuotaState() : new QuotaState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new QuotaState();
        }
    }

    private void Save()
    {
        try
        {
            var temp = _stateFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, s_json));
            File.Move(temp, _stateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the download limit state");
        }
    }

    private sealed class QuotaState
    {
        public DateTimeOffset? PeriodStart { get; set; }

        public long UsedBytes { get; set; }

        public bool IgnoreThisPeriod { get; set; }

        public DateTimeOffset? BlockedUntil { get; set; }

        public List<long> PausedQueues { get; set; } = [];

        public List<long> PausedDownloads { get; set; } = [];

        public void Reset()
        {
            PeriodStart = null;
            UsedBytes = 0;
            IgnoreThisPeriod = false;
        }
    }
}
