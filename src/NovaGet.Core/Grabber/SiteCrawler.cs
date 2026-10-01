using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Naming;
using NovaGet.Core.Services;

namespace NovaGet.Core.Grabber;

/// <summary>A response as the crawler sees it: where it ended up, what it is and, for pages and style sheets, its body.</summary>
public sealed record FetchedResource
{
    public required Uri Url { get; init; }

    public string? ContentType { get; init; }

    /// <summary>-1 when unknown.</summary>
    public long Size { get; init; } = -1;

    /// <summary>The body of an HTML page (or of a text resource when asked for); null otherwise.</summary>
    public byte[]? Body { get; init; }

    public string? FileName { get; init; }

    public bool IsHtml => ContentType is { } type && (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Fetches pages for the crawler (the app's implementation goes through the download engine's protocols).</summary>
public interface IPageFetcher
{
    /// <param name="text">Read the body of a text resource too (robots.txt, style sheets), not only of HTML.</param>
    Task<FetchedResource> FetchAsync(Uri url, Uri? referrer, bool text, CancellationToken cancellationToken);
}

/// <summary>A file the crawler found.</summary>
public sealed record GrabbedFile
{
    public required Uri Url { get; init; }

    /// <summary>The page it was found on.</summary>
    public Uri? PageUrl { get; init; }

    /// <summary>Lower-case extension without the dot ("" when unknown).</summary>
    public string Extension { get; init; } = string.Empty;

    public long Size { get; init; } = -1;

    public string? ContentType { get; init; }

    /// <summary>It is one of the chosen file types (and within the size limits).</summary>
    public bool Matches { get; init; }
}

/// <summary>A page the crawler loaded (or tried to).</summary>
public sealed record CrawledPage(Uri Url, int Depth, string? Error);

/// <summary>
/// The site grabber's explorer (section 14): breadth-first from the start page up to the depth, within the site (and
/// subdomains / other sites as configured), filtered by include/exclude wildcards, robots.txt, a page limit, a delay
/// between requests and a number of parallel page loads. It reports the files it finds; for offline browsing it also
/// saves the pages and later rewrites their links to the local copies.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The rate gate never creates a wait handle; nothing to dispose.")]
public sealed partial class SiteCrawler
{
    public const int MaxPagesHardLimit = 50_000;
    public const int MaxFilesHardLimit = 200_000;
    public const long MaxPageBytes = 10L * 1024 * 1024;

    private static readonly string[] s_pageExtensions =
        ["htm", "html", "xhtml", "shtml", "php", "php3", "php4", "php5", "asp", "aspx", "jsp", "jspx", "cfm", "cgi", "pl", "do", "action", "py"];

    private readonly GrabberSettings _settings;
    private readonly IPageFetcher _fetcher;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Uri _start;
    private readonly string _site;
    private readonly string _root;
    private readonly FileTypeFilter _filter;
    private readonly bool _scanStylesheets;
    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>();
    private readonly ConcurrentDictionary<string, byte> _seenPages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _seenFiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<RobotsRules>>> _robots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _savedPages = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _rate = new(1, 1);
    private readonly PauseGate _pause = new();
    private DateTimeOffset _nextRequest = DateTimeOffset.MinValue;
    private int _pending;
    private int _pagesStarted;
    private int _pagesExplored;
    private int _filesFound;

    public SiteCrawler(GrabberSettings settings, IPageFetcher fetcher, ILogger? logger = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _fetcher = fetcher;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _start = new Uri(settings.StartUrl.Trim());
        _site = _start.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? _start.Host[4..] : _start.Host;
        _root = Environment.ExpandEnvironmentVariables(settings.SaveFolder.Trim());
        _filter = new FileTypeFilter(settings.FileTypes, settings.MinSizeKB * 1024, settings.MaxSizeKB * 1024);
        _scanStylesheets = settings.ConvertLinks || _filter.Matches("png", -1) || _filter.Matches("jpg", -1) || _filter.Matches("woff2", -1);
    }

    /// <summary>Raised (on a worker thread) for every new file.</summary>
    public event EventHandler<GrabbedFile>? FileFound;

    /// <summary>Raised (on a worker thread) for every page loaded or failed.</summary>
    public event EventHandler<CrawledPage>? PageExplored;

    public int PagesExplored => Volatile.Read(ref _pagesExplored);

    public int FilesFound => Volatile.Read(ref _filesFound);

    public bool IsPaused => _pause.IsPaused;

    /// <summary>Pages saved for offline browsing (address → local file).</summary>
    public IReadOnlyDictionary<string, string> SavedPages => _savedPages;

    public void Pause() => _pause.Pause();

    public void Resume() => _pause.Resume();

    /// <summary>Explores until there is nothing left or <paramref name="cancellationToken"/> stops it ("Stop exploring").</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _seenPages.TryAdd(Key(_start), 0);
        Interlocked.Increment(ref _pagesStarted);
        Enqueue(new Work(_start, 0, 0, null, IsStylesheet: false));
        var workers = Enumerable.Range(0, Math.Clamp(_settings.MaxParallel, 1, 16)).Select(_ => WorkerAsync(cancellationToken)).ToList();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            _queue.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Offline browsing: rewrites every saved page so links to saved pages and to <paramref name="files"/> (which are
    /// downloaded to <see cref="OfflinePaths"/>) point to the local copies.
    /// </summary>
    public void RewriteSavedPages(IEnumerable<Uri> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var map = new Dictionary<string, string>(_savedPages, StringComparer.Ordinal);
        foreach (var file in files)
        {
            map.TryAdd(Key(file), OfflinePaths.LocalPath(_root, file, isPage: false));
        }

        foreach (var (url, path) in _savedPages.DistinctBy(p => p.Value).ToList())
        {
            try
            {
                var html = File.ReadAllText(path);
                var rewritten = LinkScanner.RewriteHtml(html, new Uri(url),
                    link => map.TryGetValue(Key(link), out var target) ? OfflinePaths.RelativeLink(path, target) : null);
                File.WriteAllText(path, rewritten, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not rewrite the saved page {Path}", path);
            }
        }
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        await foreach (var work in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await _pause.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (work.IsStylesheet)
                {
                    await ProcessStylesheetAsync(work, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ProcessPageAsync(work, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Grabber: {Url} failed: {Error}", work.Url, ex.Message);
                if (!work.IsStylesheet)
                {
                    PageExplored?.Invoke(this, new CrawledPage(work.Url, work.Depth, ex.Message));
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    _queue.Writer.TryComplete();
                }
            }
        }
    }

    private async Task ProcessPageAsync(Work work, CancellationToken cancellationToken)
    {
        if (!await AllowedByRobotsAsync(work.Url, cancellationToken).ConfigureAwait(false))
        {
            PageExplored?.Invoke(this, new CrawledPage(work.Url, work.Depth, "Blocked by robots.txt"));
            return;
        }

        await ThrottleAsync(work.Url, cancellationToken).ConfigureAwait(false);
        var fetched = await _fetcher.FetchAsync(work.Url, work.Referrer, text: false, cancellationToken).ConfigureAwait(false);
        if (!fetched.IsHtml || fetched.Body is null)
        {
            // A page link that turned out to be a file (download.php?id=3 → a ZIP).
            await ReportFileAsync(work.Url, work.Referrer, fetched, cancellationToken).ConfigureAwait(false);
            return;
        }

        _seenPages.TryAdd(Key(fetched.Url), 0);
        Interlocked.Increment(ref _pagesExplored);
        PageExplored?.Invoke(this, new CrawledPage(fetched.Url, work.Depth, null));
        var html = PageText.Decode(fetched.Body, fetched.ContentType);
        if (_settings.ConvertLinks)
        {
            SavePage(work.Url, fetched.Url, html);
        }

        foreach (var link in LinkScanner.ScanHtml(html, fetched.Url))
        {
            switch (link.Kind)
            {
                case LinkKind.Page when !LooksLikeFile(link.Url):
                    ConsiderPage(link.Url, work, fetched.Url);
                    break;
                case LinkKind.Stylesheet:
                    await ReportFileAsync(link.Url, fetched.Url, null, cancellationToken).ConfigureAwait(false);
                    ConsiderStylesheet(link.Url, fetched.Url);
                    break;
                default:
                    await ReportFileAsync(link.Url, fetched.Url, null, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task ProcessStylesheetAsync(Work work, CancellationToken cancellationToken)
    {
        await ThrottleAsync(work.Url, cancellationToken).ConfigureAwait(false);
        var fetched = await _fetcher.FetchAsync(work.Url, work.Referrer, text: true, cancellationToken).ConfigureAwait(false);
        if (fetched.Body is null)
        {
            return;
        }

        var css = PageText.Decode(fetched.Body, fetched.ContentType);
        foreach (var url in LinkScanner.ScanCss(css, fetched.Url))
        {
            if (url.AbsolutePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            {
                await ReportFileAsync(url, fetched.Url, null, cancellationToken).ConfigureAwait(false);
                ConsiderStylesheet(url, fetched.Url); // @import
            }
            else
            {
                await ReportFileAsync(url, fetched.Url, null, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ConsiderPage(Uri url, Work from, Uri pageUrl)
    {
        if (url.Scheme is not ("http" or "https"))
        {
            return;
        }

        var depth = from.Depth + 1;
        var onSite = IsOnSite(url);
        var offsite = onSite ? 0 : from.OffsiteDepth + 1;
        if (depth > _settings.Depth)
        {
            return;
        }

        if (!onSite && _settings.StayOnSite && !(_settings.FollowExternalLinks && offsite <= _settings.ExternalDepth))
        {
            return;
        }

        if (!PassesFilters(url, page: true) || !_seenPages.TryAdd(Key(url), 0))
        {
            return;
        }

        var limit = _settings.MaxPages > 0 ? Math.Min(_settings.MaxPages, MaxPagesHardLimit) : MaxPagesHardLimit;
        if (Interlocked.Increment(ref _pagesStarted) > limit)
        {
            return;
        }

        Enqueue(new Work(url, depth, offsite, pageUrl, IsStylesheet: false));
    }

    private void ConsiderStylesheet(Uri url, Uri referrer)
    {
        if (_scanStylesheets && url.Scheme is "http" or "https" && _seenPages.TryAdd("css:" + Key(url), 0))
        {
            Enqueue(new Work(url, 0, 0, referrer, IsStylesheet: true));
        }
    }

    private async Task ReportFileAsync(Uri url, Uri? pageUrl, FetchedResource? fetched, CancellationToken cancellationToken)
    {
        if (!_seenFiles.TryAdd(Key(url), 0) || FilesFound >= MaxFilesHardLimit || !PassesFilters(url, page: false))
        {
            return;
        }

        var contentType = fetched?.ContentType;
        var size = fetched?.Size ?? -1;
        var extension = ExtensionOf(fetched?.FileName ?? Uri.UnescapeDataString(url.AbsolutePath.Split('/')[^1]), contentType);
        if (size < 0 && _filter.HasSizeLimits && _filter.Matches(extension, -1) && url.Scheme is "http" or "https")
        {
            // Size limits need the size: ask the server (headers only).
            try
            {
                await ThrottleAsync(url, cancellationToken).ConfigureAwait(false);
                var probe = await _fetcher.FetchAsync(url, pageUrl, text: false, cancellationToken).ConfigureAwait(false);
                size = probe.Size;
                contentType ??= probe.ContentType;
            }
            catch (DownloadException ex)
            {
                _logger.LogDebug("Grabber: size of {Url} unknown: {Error}", url, ex.Message);
            }
        }

        var matches = _filter.Matches(extension, size);
        if (!matches && _settings.OnlyMatching)
        {
            return;
        }

        Interlocked.Increment(ref _filesFound);
        FileFound?.Invoke(this, new GrabbedFile { Url = url, PageUrl = pageUrl, Extension = extension, Size = size, ContentType = contentType, Matches = matches });
    }

    private void SavePage(Uri requested, Uri final, string html)
    {
        var path = OfflinePaths.LocalPath(_root, final, isPage: true);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, html, new UTF8Encoding(false));
            _savedPages[Key(final)] = path;
            _savedPages.TryAdd(Key(requested), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the page {Url} to {Path}", final, path);
        }
    }

    private async Task<bool> AllowedByRobotsAsync(Uri url, CancellationToken cancellationToken) =>
        !_settings.ObeyRobots || (await RobotsFor(url).WaitAsync(cancellationToken).ConfigureAwait(false)).IsAllowed(url);

    private Task<RobotsRules> RobotsFor(Uri url) =>
        _robots.GetOrAdd(url.GetLeftPart(UriPartial.Authority), authority => new Lazy<Task<RobotsRules>>(async () =>
        {
            try
            {
                var fetched = await _fetcher.FetchAsync(new Uri(authority + "/robots.txt"), null, text: true, CancellationToken.None).ConfigureAwait(false);
                return fetched.Body is null || fetched.IsHtml ? RobotsRules.AllowAll : RobotsRules.Parse(PageText.Decode(fetched.Body, fetched.ContentType));
            }
            catch (Exception ex) when (ex is DownloadException or IOException or HttpRequestException)
            {
                return RobotsRules.AllowAll; // no robots.txt (404) or unreachable: nothing is forbidden
            }
        })).Value;

    /// <summary>Keeps <see cref="GrabberSettings.DelayMs"/> (or a longer robots.txt Crawl-delay) between requests.</summary>
    private async Task ThrottleAsync(Uri url, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, _settings.DelayMs));
        if (_settings.ObeyRobots && _robots.TryGetValue(url.GetLeftPart(UriPartial.Authority), out var robots)
            && robots.Value.IsCompletedSuccessfully && robots.Value.Result.CrawlDelay is { } crawlDelay && crawlDelay > delay)
        {
            delay = crawlDelay;
        }

        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await _rate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _nextRequest - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }

            _nextRequest = _time.GetUtcNow() + delay;
        }
        finally
        {
            _rate.Release();
        }
    }

    private void Enqueue(Work work)
    {
        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(work))
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    private bool IsOnSite(Uri url)
    {
        var host = url.Host;
        if (string.Equals(host, _start.Host, StringComparison.OrdinalIgnoreCase) || string.Equals(host, _site, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "www." + _site, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _settings.ExploreSubdomains && host.EndsWith("." + _site, StringComparison.OrdinalIgnoreCase);
    }

    private bool PassesFilters(Uri url, bool page)
    {
        var text = url.AbsoluteUri;
        if (_settings.ExcludeFilters.Any(f => WildcardMatch(f, text)))
        {
            return false;
        }

        // Include filters choose the pages to explore; files are chosen by type.
        return !page || _settings.IncludeFilters.Count == 0 || _settings.IncludeFilters.Any(f => WildcardMatch(f, text));
    }

    /// <summary>A pattern with * or ? must match the whole address; plain text matches anywhere in it.</summary>
    internal static bool WildcardMatch(string pattern, string text)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0)
        {
            return false;
        }

        if (pattern.IndexOfAny(['*', '?']) < 0)
        {
            return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>A link with a file extension that isn't a page's (.zip, .jpg, …).</summary>
    internal static bool LooksLikeFile(Uri url)
    {
        var name = url.AbsolutePath.Split('/')[^1];
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            return false;
        }

        var extension = name[(dot + 1)..].ToLowerInvariant();
        return extension.Length <= 5 && extension.Any(char.IsLetter) && !s_pageExtensions.Contains(extension);
    }

    internal static string ExtensionOf(string name, string? contentType)
    {
        var dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1 && name.Length - dot <= 6)
        {
            return name[(dot + 1)..].ToLowerInvariant();
        }

        return MimeTypes.ExtensionFor(contentType)?.TrimStart('.').ToLowerInvariant() ?? string.Empty;
    }

    private static string Key(Uri url) => url.GetLeftPart(UriPartial.Query);

    private sealed record Work(Uri Url, int Depth, int OffsiteDepth, Uri? Referrer, bool IsStylesheet);

    /// <summary>"Pause": workers finish what they are loading, then wait.</summary>
    private sealed class PauseGate
    {
        private TaskCompletionSource? _paused;

        public bool IsPaused => Volatile.Read(ref _paused) is not null;

        public void Pause() => Interlocked.CompareExchange(ref _paused, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);

        public void Resume() => Interlocked.Exchange(ref _paused, null)?.TrySetResult();

        public Task WaitAsync(CancellationToken cancellationToken) =>
            Volatile.Read(ref _paused)?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }
}

/// <summary>Step 4's file filter: extension patterns (wildcards; "*" = everything) and optional size limits in bytes.</summary>
public sealed class FileTypeFilter(string patterns, long minBytes, long maxBytes)
{
    private readonly IReadOnlyList<string> _patterns = CategoryMatcher.SplitPatterns(patterns ?? string.Empty);

    /// <summary>"*" (or "*.*") on its own means every file, with or without an extension.</summary>
    private readonly bool _all = (patterns ?? string.Empty).Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Any(p => p.Trim() is "*" or "*.*");

    public bool HasSizeLimits => minBytes > 0 || maxBytes > 0;

    public bool Matches(string extension, long size)
    {
        var typeMatches = _all || (extension.Length > 0 && _patterns.Any(p => CategoryMatcher.ExtensionMatches(p, extension)));
        if (!typeMatches)
        {
            return false;
        }

        return size < 0 || ((minBytes <= 0 || size >= minBytes) && (maxBytes <= 0 || size <= maxBytes));
    }
}
