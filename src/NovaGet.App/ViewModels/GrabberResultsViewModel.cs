using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaGet.App.Localization;
using NovaGet.Core.Engine;
using NovaGet.Core.Formatting;
using NovaGet.Core.Grabber;
using NovaGet.Core.Models;

namespace NovaGet.App.ViewModels;

/// <summary>A row of the grabber results grid.</summary>
public sealed partial class GrabberItemViewModel(GrabberItem item) : ObservableObject
{
    public GrabberItem Item { get; } = item;

    public string Url => Item.Url.AbsoluteUri;

    public string Type => Item.Extension.Length == 0 ? "—" : Item.Extension.ToUpperInvariant();

    public string Size => Item.Size >= 0 ? DisplayFormat.Size(Item.Size) : string.Empty;

    public string Status => Localizer.Get($"Grabber_State_{Item.State}");

    public string PageUrl => Item.PageUrl?.AbsoluteUri ?? string.Empty;

    [ObservableProperty]
    private bool _isChecked = item.CheckedByDefault;

    /// <summary>The item changed (state or size) outside the grid.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Size));
    }
}

/// <summary>
/// The grabber results window (section 14): a live grid of found files with counters, Stop exploring, Pause, Download
/// selected and Add selected to queue. Rows arrive on background threads and are added in batches by <see cref="Flush"/>.
/// </summary>
public sealed partial class GrabberResultsViewModel : ObservableObject, IDisposable
{
    private readonly GrabberSession _session;
    private readonly ConcurrentQueue<GrabberItem> _arrived = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<IReadOnlyList<GrabberItem>, long?, IReadOnlyList<long>> _download;
    private bool _disposed;

    /// <param name="download">Adds the files to a queue (null = the main queue, started at once); returns download ids.</param>
    public GrabberResultsViewModel(GrabberSession session, string projectName, Func<IReadOnlyList<GrabberItem>, long?, IReadOnlyList<long>> download)
    {
        _session = session;
        _download = download;
        ProjectName = projectName;
        session.ItemFound += (_, item) => _arrived.Enqueue(item);
    }

    public string ProjectName { get; }

    public string StartUrl => _session.Settings.StartUrl;

    public ObservableCollection<GrabberItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(PauseCommand))]
    private bool _isExploring;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _counters = string.Empty;

    /// <summary>Runs the exploration; returns when it is done or stopped.</summary>
    public async Task RunAsync()
    {
        IsExploring = true;
        StatusText = Localizer.Get("Grabber_Exploring");
        string done;
        try
        {
            await Task.Run(() => _session.RunAsync(_stop.Token));
            done = "Grabber_Done";
        }
        catch (OperationCanceledException)
        {
            done = "Grabber_Stopped";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            IsExploring = false;
            Flush();
            StatusText = Localizer.Format("Grabber_Failed", ex.Message);
            return;
        }

        IsExploring = false;
        IsPaused = false;
        Flush();
        StatusText = Localizer.Get(done);
    }

    /// <summary>Moves newly found files into the grid and refreshes the counters (called by a timer on the UI thread).</summary>
    public void Flush()
    {
        while (_arrived.TryDequeue(out var item))
        {
            Items.Add(new GrabberItemViewModel(item));
        }

        Counters = Localizer.Format("Grabber_Counters", _session.Crawler.PagesExplored, _session.Crawler.FilesFound,
            Items.Count(i => i.Item.State == GrabberItemState.Downloaded));
    }

    /// <summary>A download finished or changed: update the rows made from it.</summary>
    public void OnDownloadStateChanged(DownloadStateChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        foreach (var row in Items.Where(i => i.Item.DownloadId == e.Id))
        {
            if (e.Status == DownloadStatus.Completed)
            {
                row.Item.State = GrabberItemState.Downloaded;
            }

            row.Refresh();
        }
    }

    public IReadOnlyList<GrabberItemViewModel> Selected => [.. Items.Where(i => i.IsChecked)];

    [RelayCommand(CanExecute = nameof(IsExploring))]
    private void Stop() => _stop.Cancel();

    [RelayCommand(CanExecute = nameof(IsExploring))]
    private void Pause()
    {
        if (_session.Crawler.IsPaused)
        {
            _session.Crawler.Resume();
            IsPaused = false;
            StatusText = Localizer.Get("Grabber_Exploring");
        }
        else
        {
            _session.Crawler.Pause();
            IsPaused = true;
            StatusText = Localizer.Get("Grabber_Paused");
        }
    }

    [RelayCommand]
    private void SelectAll() => SetChecked(true);

    [RelayCommand]
    private void SelectNone() => SetChecked(false);

    /// <summary>"Download selected" (queueId null) or "Add selected to queue". Returns how many were added.</summary>
    public int AddSelected(long? queueId)
    {
        var rows = Selected;
        var ids = _download([.. rows.Select(r => r.Item)], queueId);
        foreach (var row in rows)
        {
            row.IsChecked = false;
            row.Refresh();
        }

        return ids.Count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _stop.Dispose();
    }

    private void SetChecked(bool value)
    {
        foreach (var row in Items)
        {
            row.IsChecked = value;
        }
    }
}
