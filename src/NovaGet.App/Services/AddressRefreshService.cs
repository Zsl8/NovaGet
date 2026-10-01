using System.Windows;
using Microsoft.Extensions.Logging;
using NovaGet.App.Localization;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Engine.Naming;
using NovaGet.Core.Integration;
using NovaGet.Core.Models;
using NovaGet.Core.Services;

namespace NovaGet.App.Services;

/// <summary>
/// "Refresh download address" (section 4.5): the referring page opens in the browser, and the next download the extension
/// catches for the same file (same name, or from the same site) gives the existing download its new address. The download
/// then continues from the bytes it already has.
/// </summary>
internal sealed class AddressRefreshService(
    IDownloadService downloads,
    Lazy<DownloadUiService> downloadUi,
    ILogger<AddressRefreshService> logger)
{
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private Pending? _pending;
    private RefreshWaitDialog? _window;

    public long? WaitingFor
    {
        get
        {
            lock (_gate)
            {
                return _pending is { } p && p.Expires > DateTime.UtcNow ? p.DownloadId : null;
            }
        }
    }

    /// <summary>Starts waiting for the new address and opens the page the file came from.</summary>
    public void Start(long downloadId, bool openBrowser = true)
    {
        if (downloads.Find(downloadId) is not { } download || download.Status == DownloadStatus.Completed)
        {
            return;
        }

        var original = Uri.TryCreate(string.IsNullOrWhiteSpace(download.OriginalUrl) ? download.Url : download.OriginalUrl, UriKind.Absolute, out var o) ? o : null;
        var page = Uri.TryCreate(download.Referrer, UriKind.Absolute, out var r) && r.Scheme is "http" or "https" ? r : original;
        lock (_gate)
        {
            _pending = new Pending(downloadId, download.FileName, original?.Host, DateTime.UtcNow + WaitLimit);
        }

        logger.LogInformation("Waiting for a new address for download {Id} from {Page}", downloadId, page);
        if (Application.Current is not null)
        {
            _window?.Close();
            _window = new RefreshWaitDialog(download.FileName, Cancel);
            _window.Show();
        }

        if (openBrowser && page is not null)
        {
            ShellService.OpenUrl(page.AbsoluteUri);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _pending = null;
        }

        CloseWindow();
    }

    /// <summary>
    /// A download the browser handed over: when it is the file we're waiting for, it refreshes that download instead of
    /// becoming a new one. Returns true when it was taken.
    /// </summary>
    public bool TryTake(BrowserDownload candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Pending pending;
        lock (_gate)
        {
            if (_pending is not { } p || p.Expires < DateTime.UtcNow)
            {
                _pending = null;
                return false;
            }

            pending = p;
        }

        var url = candidate.FinalUrl ?? candidate.Url;
        var name = FileNameSanitizer.Sanitize(string.IsNullOrWhiteSpace(candidate.FileName)
            ? FileNameResolver.NameFromUrl(url) ?? string.Empty
            : candidate.FileName);
        var sameName = !string.IsNullOrEmpty(pending.FileName) && string.Equals(name, pending.FileName, StringComparison.OrdinalIgnoreCase);
        var sameSite = pending.Host is not null && string.Equals(url.Host, pending.Host, StringComparison.OrdinalIgnoreCase);
        if (!sameName && !sameSite)
        {
            return false;
        }

        lock (_gate)
        {
            _pending = null;
        }

        Apply(pending.DownloadId, url, candidate.Request);
        return true;
    }

    private void Apply(long downloadId, Uri url, BrowserRequestInfo request)
    {
        void Run()
        {
            CloseWindow();
            if (downloads.Find(downloadId) is not { } download)
            {
                return;
            }

            logger.LogInformation("Download {Id} gets a new address", downloadId);
            download.Url = url.AbsoluteUri;
            download.Referrer = request.Referrer ?? download.Referrer;
            download.Cookies = request.Cookies ?? download.Cookies;
            download.UserAgent = request.UserAgent ?? download.UserAgent;
            downloads.Save(download);
            downloadUi.Value.StartDownload(downloadId);
        }

        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(Run);
        }
        else
        {
            Run();
        }
    }

    private void CloseWindow()
    {
        if (_window is { } window)
        {
            _window = null;
            window.Dispatcher.BeginInvoke(window.Close);
        }
    }

    private sealed record Pending(long DownloadId, string FileName, string? Host, DateTime Expires);
}
