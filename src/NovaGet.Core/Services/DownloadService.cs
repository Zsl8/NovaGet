using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Naming;
using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Settings;

namespace NovaGet.Core.Services;

public sealed class DownloadService : IDownloadService
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Download> _downloads = [];
    private readonly IDownloadRepository _repository;
    private readonly ICategoryRepository _categories;
    private readonly IDownloadEngine _engine;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger _logger;

    public DownloadService(
        IDownloadRepository repository,
        ICategoryRepository categories,
        IDownloadEngine engine,
        ISettingsService settings,
        AppPaths paths,
        ILogger<DownloadService>? logger = null)
    {
        _repository = repository;
        _categories = categories;
        _engine = engine;
        _settings = settings;
        _paths = paths;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        foreach (var download in repository.GetAll())
        {
            _downloads[download.Id] = download;
        }

        engine.StateChanged += OnEngineStateChanged;
    }

    public event EventHandler<DownloadListChangedEventArgs>? Changed;

    public event EventHandler<DownloadStateChangedEventArgs>? StateChanged;

    public IReadOnlyList<Download> GetAll()
    {
        lock (_gate)
        {
            return [.. _downloads.Values.OrderBy(d => d.Id).Select(d => d.Clone())];
        }
    }

    public Download? Find(long id)
    {
        lock (_gate)
        {
            return _downloads.TryGetValue(id, out var download) ? download.Clone() : null;
        }
    }

    public Download Add(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fileName = string.IsNullOrWhiteSpace(request.FileName) ? string.Empty : FileNameSanitizer.Sanitize(request.FileName);
        var matcher = new CategoryMatcher(_categories.GetAll());
        var category = request.CategoryId is { } categoryId ? _categories.Get(categoryId) ?? matcher.General : matcher.Match(fileName);
        var folder = string.IsNullOrWhiteSpace(request.SaveFolder)
            ? SaveLocationResolver.FolderFor(category, _settings.Current, _paths)
            : request.SaveFolder;

        var download = new Download
        {
            Url = request.Url,
            OriginalUrl = string.IsNullOrWhiteSpace(request.OriginalUrl) ? request.Url : request.OriginalUrl,
            Referrer = request.Referrer,
            FileName = fileName,
            SavePath = folder,
            CategoryId = category.Id,
            Size = request.Size,
            Status = request.QueueId is null ? DownloadStatus.Paused : DownloadStatus.Queued,
            ResumeCapable = request.ResumeCapable,
            Description = request.Description,
            UserAgent = request.UserAgent,
            Cookies = request.Cookies,
            AuthUser = request.AuthUser,
            AuthPassword = request.AuthPassword,
            MaxConnections = request.MaxConnections,
            SpeedLimitKBps = request.SpeedLimitKBps,
            ETag = request.ETag,
            LastModified = request.LastModified,
            IsStream = request.IsStream,
            StreamManifestJson = request.StreamManifestJson,
            OverwriteExisting = request.OverwriteExisting,
            AddedAt = DateTime.UtcNow,
        };

        lock (_gate)
        {
            if (request.QueueId is { } queueId)
            {
                download.QueueId = queueId;
                download.QueuePosition = NextQueuePosition(queueId);
            }

            _repository.Insert(download);
            _downloads[download.Id] = download;
        }

        Raise(DownloadListChange.Added, [download.Id]);
        return download.Clone();
    }

    public Download? FindByUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        lock (_gate)
        {
            return _downloads.Values
                .Where(d => SameAddress(d.OriginalUrl, url) || SameAddress(d.Url, url))
                .OrderByDescending(d => d.Id)
                .FirstOrDefault()?.Clone();
        }
    }

    public async Task<string?> MoveOrRenameAsync(long id, string folder, string fileName)
    {
        var name = FileNameSanitizer.Sanitize(fileName);
        Download download;
        lock (_gate)
        {
            if (!_downloads.TryGetValue(id, out var cached))
            {
                return "The download no longer exists.";
            }

            download = cached.Clone();
        }

        if (download.Status == DownloadStatus.Completed)
        {
            var source = download.FullPath;
            if (!File.Exists(source))
            {
                return $"The file \"{source}\" doesn't exist any more.";
            }

            try
            {
                Directory.CreateDirectory(folder);
                if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(Path.Combine(folder, name)), StringComparison.OrdinalIgnoreCase))
                {
                    name = FileNameSanitizer.MakeUnique(folder, name);
                    var target = Path.Combine(folder, name);
                    await Task.Run(() => File.Move(source, target)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _logger.LogWarning(ex, "Move/rename of download {Id} failed", id);
                return ex.Message;
            }
        }

        download.SavePath = folder;
        download.FileName = name;
        Save(download);
        return null;
    }

    private static bool SameAddress(string? a, string b)
    {
        if (string.IsNullOrEmpty(a))
        {
            return false;
        }

        if (Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y))
        {
            // Scheme and host are case-insensitive; path and query are not.
            return Uri.Compare(x, y, UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0;
        }

        return string.Equals(a, b, StringComparison.Ordinal);
    }

    public bool Start(long id, bool startedByQueue = false)
    {
        lock (_gate)
        {
            if (!_downloads.TryGetValue(id, out var download) || download.Status == DownloadStatus.Completed)
            {
                return false;
            }
        }

        return _engine.Start(id, startedByQueue);
    }

    public Task StopAsync(long id) => _engine.PauseAsync(id);

    public Task StopAllAsync() => _engine.PauseAllAsync();

    public async Task RemoveAsync(IReadOnlyCollection<long> ids, bool deleteFiles)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var removed = new List<long>();
        foreach (var id in ids)
        {
            Download? download;
            lock (_gate)
            {
                _downloads.TryGetValue(id, out download);
            }

            if (download is null)
            {
                continue;
            }

            await _engine.RemoveAsync(id).ConfigureAwait(false);
            if (deleteFiles && download.Status == DownloadStatus.Completed)
            {
                TryDeleteFile(download.FullPath);
            }

            _repository.Delete(id);
            lock (_gate)
            {
                _downloads.Remove(id);
            }

            removed.Add(id);
        }

        if (removed.Count > 0)
        {
            Raise(DownloadListChange.Removed, removed);
        }
    }

    public int RemoveCompleted()
    {
        List<long> completed;
        lock (_gate)
        {
            completed = [.. _downloads.Values.Where(d => d.Status == DownloadStatus.Completed).Select(d => d.Id)];
            foreach (var id in completed)
            {
                _repository.Delete(id);
                _downloads.Remove(id);
            }
        }

        if (completed.Count > 0)
        {
            Raise(DownloadListChange.Removed, completed);
        }

        return completed.Count;
    }

    public async Task RedownloadAsync(long id, bool start = true)
    {
        await _engine.PauseAsync(id).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_downloads.TryGetValue(id, out var download))
            {
                return;
            }

            if (download.Status == DownloadStatus.Completed)
            {
                // Clear the completed state so the engine accepts it; the engine resets the rest. The new copy
                // replaces the old file instead of getting a numbered name.
                download.Status = DownloadStatus.Paused;
                download.CompletedAt = null;
                download.OverwriteExisting = true;
                _repository.Update(download);  // not running: a full write is safe
            }
        }

        if (start)
        {
            await _engine.RestartAsync(id).ConfigureAwait(false);
        }
        else
        {
            await _engine.ResetAsync(id).ConfigureAwait(false);
        }

        Refresh(id);
    }

    public void SetQueue(IReadOnlyCollection<long> ids, long? queueId)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var changed = new List<long>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (!_downloads.TryGetValue(id, out var download) || download.QueueId == queueId)
                {
                    continue;
                }

                var oldQueue = download.QueueId;
                download.QueueId = queueId;
                download.QueuePosition = queueId is { } q ? NextQueuePosition(q) : 0;
                if (queueId is null && download.Status == DownloadStatus.Queued)
                {
                    download.Status = DownloadStatus.Paused;
                }
                else if (queueId is not null && download.Status == DownloadStatus.Paused)
                {
                    download.Status = DownloadStatus.Queued;
                }

                _repository.UpdateDetails(download);
                changed.Add(id);
                if (oldQueue is { } previous)
                {
                    changed.AddRange(Renumber(previous));
                }
            }
        }

        if (changed.Count > 0)
        {
            Raise(DownloadListChange.Updated, [.. changed.Distinct()]);
        }
    }

    public void MoveInQueue(long id, int delta)
    {
        long[] changed;
        lock (_gate)
        {
            if (!_downloads.TryGetValue(id, out var download) || download.QueueId is not { } queueId || delta == 0)
            {
                return;
            }

            var ordered = _downloads.Values.Where(d => d.QueueId == queueId).OrderBy(d => d.QueuePosition).ThenBy(d => d.Id).ToList();
            var index = ordered.IndexOf(download);
            var target = Math.Clamp(index + delta, 0, ordered.Count - 1);
            if (target == index)
            {
                return;
            }

            ordered.RemoveAt(index);
            ordered.Insert(target, download);
            changed = [.. ApplyPositions(ordered)];
        }

        if (changed.Length > 0)
        {
            Raise(DownloadListChange.Updated, changed);
        }
    }

    public void SetCategory(IReadOnlyCollection<long> ids, long categoryId)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var changed = new List<long>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (_downloads.TryGetValue(id, out var download) && download.CategoryId != categoryId)
                {
                    download.CategoryId = categoryId;
                    _repository.UpdateDetails(download);
                    changed.Add(id);
                }
            }
        }

        if (changed.Count > 0)
        {
            Raise(DownloadListChange.Updated, changed);
        }
    }

    public void Save(Download download)
    {
        ArgumentNullException.ThrowIfNull(download);
        lock (_gate)
        {
            _repository.UpdateDetails(download);
        }

        Refresh(download.Id);
    }

    public DownloadStatistics GetStatistics()
    {
        int total;
        lock (_gate)
        {
            total = _downloads.Count;
        }

        var running = _engine.RunningIds;
        var speed = running.Select(_engine.GetProgress).Sum(p => p?.BytesPerSecond ?? 0);
        return new DownloadStatistics(total, running.Count, speed);
    }

    private void OnEngineStateChanged(object? sender, DownloadStateChangedEventArgs e)
    {
        Refresh(e.Id);
        StateChanged?.Invoke(this, e);
    }

    public void Reload(long id) => Refresh(id);

    private void Refresh(long id)
    {
        var fresh = _repository.Get(id);
        lock (_gate)
        {
            if (fresh is null || !_downloads.ContainsKey(id))
            {
                return; // removed meanwhile
            }

            _downloads[id] = fresh;
        }

        Raise(DownloadListChange.Updated, [id]);
    }

    private int NextQueuePosition(long queueId) =>
        _downloads.Values.Where(d => d.QueueId == queueId).Select(d => d.QueuePosition).DefaultIfEmpty(0).Max() + 1;

    private List<long> Renumber(long queueId) =>
        ApplyPositions([.. _downloads.Values.Where(d => d.QueueId == queueId).OrderBy(d => d.QueuePosition).ThenBy(d => d.Id)]);

    private List<long> ApplyPositions(List<Download> ordered)
    {
        var changed = new List<long>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].QueuePosition != i + 1)
            {
                ordered[i].QueuePosition = i + 1;
                _repository.UpdateDetails(ordered[i]);
                changed.Add(ordered[i].Id);
            }
        }

        return changed;
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    private void Raise(DownloadListChange change, IReadOnlyList<long> ids) =>
        Changed?.Invoke(this, new DownloadListChangedEventArgs(change, ids));
}
