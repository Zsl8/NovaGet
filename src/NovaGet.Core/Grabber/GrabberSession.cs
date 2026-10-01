using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Models;
using NovaGet.Core.Services;

namespace NovaGet.Core.Grabber;

/// <summary>Where a found file stands, compared with the project's earlier runs.</summary>
public enum GrabberItemState
{
    /// <summary>Not seen before (or never downloaded): checked.</summary>
    New,

    /// <summary>Downloaded before but different on the server now: checked.</summary>
    Changed,

    /// <summary>Downloaded before and the same: left unchecked.</summary>
    Unchanged,

    /// <summary>Handed to the download list in this or an earlier run.</summary>
    Queued,

    Downloaded,
}

/// <summary>A row of the grabber results window.</summary>
public sealed class GrabberItem
{
    public required Uri Url { get; init; }

    public Uri? PageUrl { get; init; }

    public string Extension { get; init; } = string.Empty;

    public long Size { get; set; } = -1;

    public bool Matches { get; init; }

    public GrabberItemState State { get; set; }

    public long? DownloadId { get; set; }

    /// <summary>Checked for download at first: matching files that are new or changed.</summary>
    public bool CheckedByDefault => Matches && State is GrabberItemState.New or GrabberItemState.Changed;
}

/// <summary>
/// One run of a grabber project: explores with <see cref="SiteCrawler"/>, compares each file with the project's earlier
/// results ("only new or changed files": a file downloaded before is checked with a HEAD request and compared by
/// ETag, Last-Modified or size), records the results and adds the chosen files to the download list.
/// </summary>
public sealed class GrabberSession
{
    private readonly GrabberProject _project;
    private readonly IGrabberRepository _repository;
    private readonly IDownloadProber _prober;
    private readonly IDownloadService _downloads;
    private readonly ICategoryRepository _categories;
    private readonly ILogger _logger;
    private readonly Channel<GrabbedFile> _found = Channel.CreateUnbounded<GrabbedFile>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<GrabberItem> _items = [];
    private readonly Dictionary<string, GrabberResult> _previous;
    private readonly string _root;

    public GrabberSession(
        GrabberProject project,
        GrabberSettings settings,
        IGrabberRepository repository,
        IPageFetcher fetcher,
        IDownloadProber prober,
        IDownloadService downloads,
        ICategoryRepository categories,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(repository);
        _project = project;
        Settings = settings;
        _repository = repository;
        _prober = prober;
        _downloads = downloads;
        _categories = categories;
        _logger = logger ?? NullLogger.Instance;
        _root = Environment.ExpandEnvironmentVariables(settings.SaveFolder.Trim());
        _previous = repository.GetResults(project.Id).ToDictionary(r => r.Url, StringComparer.Ordinal);
        Crawler = new SiteCrawler(settings, fetcher, logger, time);
        Crawler.FileFound += (_, file) => _found.Writer.TryWrite(file);
    }

    public GrabberSettings Settings { get; }

    public SiteCrawler Crawler { get; }

    public long ProjectId => _project.Id;

    /// <summary>Raised (on a background thread) when a file is listed.</summary>
    public event EventHandler<GrabberItem>? ItemFound;

    public IReadOnlyList<GrabberItem> Items
    {
        get
        {
            lock (_items)
            {
                return [.. _items];
            }
        }
    }

    public int Downloaded
    {
        get
        {
            lock (_items)
            {
                return _items.Count(i => i.State == GrabberItemState.Downloaded);
            }
        }
    }

    /// <summary>Explores the site; ends when exploring is done or stopped. Offline pages are rewritten at the end.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _project.LastRunAt = DateTime.UtcNow;
        _repository.UpdateProject(_project);
        var consumer = ConsumeAsync();
        try
        {
            await Crawler.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _found.Writer.TryComplete();
            await consumer.ConfigureAwait(false);
            if (Settings.ConvertLinks)
            {
                Crawler.RewriteSavedPages(Items.Where(i => i.Matches).Select(i => i.Url));
            }
        }
    }

    /// <summary>
    /// Adds the files to the download list in <paramref name="queueId"/> (Download selected: the main queue, which the
    /// caller then starts; Add selected to queue: the chosen one). Returns the new download ids.
    /// </summary>
    public IReadOnlyList<long> Download(IEnumerable<GrabberItem> items, long queueId)
    {
        ArgumentNullException.ThrowIfNull(items);
        var matcher = new CategoryMatcher(_categories.GetAll());
        var ids = new List<long>();
        foreach (var item in items.Where(i => i.State is not (GrabberItemState.Queued or GrabberItemState.Downloaded)))
        {
            var onSite = IsStartSite(item.Url);
            var request = new DownloadRequest
            {
                Url = item.Url.AbsoluteUri,
                Referrer = item.PageUrl?.AbsoluteUri,
                Cookies = onSite ? Settings.Cookies : null,
                AuthUser = onSite && Settings.UseAuthorization ? Settings.UserName : null,
                AuthPassword = onSite && Settings.UseAuthorization ? Settings.Password : null,
                QueueId = queueId,
                Size = item.Size,
                // A changed file replaces the copy from the earlier run.
                OverwriteExisting = item.State == GrabberItemState.Changed,
            };

            if (Settings.ConvertLinks)
            {
                // Offline browsing: the file goes where the rewritten pages expect it.
                var local = OfflinePaths.LocalPath(_root, item.Url, isPage: false);
                request = request with { SaveFolder = Path.GetDirectoryName(local), FileName = Path.GetFileName(local), OverwriteExisting = true };
            }
            else
            {
                var name = Uri.UnescapeDataString(item.Url.AbsolutePath.Split('/')[^1]);
                var category = matcher.Match(name.Length > 0 ? name : "file." + item.Extension);
                var folder = Settings.SaveByCategory && category.Id != Category.GeneralId
                    ? Path.Combine(_root, Engine.Naming.FileNameSanitizer.Sanitize(category.Name))
                    : _root;
                request = request with { SaveFolder = folder, CategoryId = category.Id };
            }

            var download = _downloads.Add(request);
            ids.Add(download.Id);
            item.DownloadId = download.Id;
            item.State = GrabberItemState.Queued;
            Save(item, GrabberResultStatus.Queued, previous: null);
        }

        return ids;
    }

    private bool IsStartSite(Uri url)
    {
        var start = new Uri(Settings.StartUrl.Trim());
        var site = start.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? start.Host[4..] : start.Host;
        return string.Equals(url.Host, start.Host, StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Host, site, StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var file in _found.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var item = await ClassifyAsync(file).ConfigureAwait(false);
                lock (_items)
                {
                    _items.Add(item);
                }

                ItemFound?.Invoke(this, item);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Grabber: could not record {Url}", file.Url);
            }
        }
    }

    private async Task<GrabberItem> ClassifyAsync(GrabbedFile file)
    {
        _previous.TryGetValue(file.Url.AbsoluteUri, out var previous);
        var item = new GrabberItem
        {
            Url = file.Url,
            PageUrl = file.PageUrl,
            Extension = file.Extension,
            Size = file.Size >= 0 ? file.Size : previous?.Size ?? -1,
            Matches = file.Matches,
            DownloadId = previous?.DownloadId,
        };

        if (previous?.Status == GrabberResultStatus.Downloaded)
        {
            item.State = await HasChangedAsync(file, previous).ConfigureAwait(false) ? GrabberItemState.Changed : GrabberItemState.Unchanged;
        }
        else if (previous?.Status == GrabberResultStatus.Queued && previous.DownloadId is { } id && _downloads.Find(id) is { } download)
        {
            item.State = download.Status == DownloadStatus.Completed ? GrabberItemState.Downloaded : GrabberItemState.Queued;
        }
        else
        {
            item.State = GrabberItemState.New;
            item.DownloadId = null;
        }

        var status = item.State switch
        {
            GrabberItemState.Queued => GrabberResultStatus.Queued,
            GrabberItemState.Downloaded or GrabberItemState.Unchanged => GrabberResultStatus.Downloaded,
            _ => GrabberResultStatus.Found,
        };
        Save(item, status, previous);
        return item;
    }

    /// <summary>Asks the server (HEAD) whether a file downloaded before is still the same.</summary>
    private async Task<bool> HasChangedAsync(GrabbedFile file, GrabberResult previous)
    {
        try
        {
            var probe = await _prober.ProbeAsync(new RequestContext { Url = file.Url, Referrer = file.PageUrl?.AbsoluteUri }, CancellationToken.None).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(previous.ETag) && !string.IsNullOrEmpty(probe.ETag))
            {
                return !string.Equals(previous.ETag, probe.ETag, StringComparison.Ordinal);
            }

            if (previous.LastModified is { } before && probe.LastModified is { } now)
            {
                return Math.Abs((now.UtcDateTime - DateTime.SpecifyKind(before, DateTimeKind.Utc)).TotalSeconds) > 1;
            }

            return previous.Size < 0 || probe.Size < 0 || previous.Size != probe.Size;
        }
        catch (DownloadException ex)
        {
            _logger.LogInformation("Grabber: could not check {Url} ({Error}); it is offered again", file.Url, ex.Message);
            return true;
        }
    }

    private void Save(GrabberItem item, GrabberResultStatus status, GrabberResult? previous)
    {
        previous ??= _repository.FindResult(_project.Id, item.Url.AbsoluteUri);
        _repository.SaveResult(new GrabberResult
        {
            ProjectId = _project.Id,
            Url = item.Url.AbsoluteUri,
            Type = item.Extension,
            Size = item.Size,
            Status = status,
            PageUrl = item.PageUrl?.AbsoluteUri,
            DownloadId = item.DownloadId,
            ETag = previous?.ETag,
            LastModified = previous?.LastModified,
            LocalPath = previous?.LocalPath,
            FoundAt = DateTime.UtcNow,
        });
    }
}

/// <summary>Records grabbed files' finished downloads (with their validators, for the next run's change check).</summary>
public sealed class GrabberDownloadTracker : IDisposable
{
    private readonly IDownloadService _downloads;
    private readonly IGrabberRepository _repository;

    public GrabberDownloadTracker(IDownloadService downloads, IGrabberRepository repository)
    {
        _downloads = downloads;
        _repository = repository;
        downloads.StateChanged += OnStateChanged;
    }

    public void Dispose() => _downloads.StateChanged -= OnStateChanged;

    private void OnStateChanged(object? sender, DownloadStateChangedEventArgs e)
    {
        if (e.Status == DownloadStatus.Completed && _downloads.Find(e.Id) is { } download)
        {
            _repository.MarkDownloaded(download.Id, download.Size, download.ETag, download.LastModified, download.FullPath);
        }
    }
}
