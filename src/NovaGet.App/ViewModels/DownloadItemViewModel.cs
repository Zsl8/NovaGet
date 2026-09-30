using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using NovaGet.App.Localization;
using NovaGet.App.Services;
using NovaGet.Core.Engine;
using NovaGet.Core.Formatting;
using NovaGet.Core.Models;

namespace NovaGet.App.ViewModels;

/// <summary>
/// One row of the download list. Updated from the list service (state changes) and from the refresh timer
/// (live progress, at most four times a second). Raises PropertyChanged only for values that really changed.
/// </summary>
public sealed class DownloadItemViewModel : INotifyPropertyChanged
{
    private string _fileName = string.Empty;
    private ImageSource? _icon;
    private string _iconExtension = "\0";
    private int? _queuePosition;
    private long _size = -1;
    private long _downloaded;
    private DownloadStatus _status;
    private string _statusText = string.Empty;
    private double _rate;
    private TimeSpan? _timeLeft;
    private DateTime? _lastTry;
    private string? _description;
    private string _saveTo = string.Empty;
    private string? _referrer;
    private int _connections;
    private string? _lastError;

    public DownloadItemViewModel(Download download)
    {
        Id = download.Id;
        AddedAt = download.AddedAt;
        Update(download);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; }

    public DateTime AddedAt { get; }

    public Download Model { get; private set; } = null!;

    public string FileName { get => _fileName; private set => Set(ref _fileName, value); }

    public ImageSource? Icon { get => _icon; private set => Set(ref _icon, value); }

    private long _categoryId;
    private long? _queueId;

    public long CategoryId { get => _categoryId; private set => Set(ref _categoryId, value); }

    public long? QueueId { get => _queueId; private set => Set(ref _queueId, value); }

    /// <summary>1-based position in its queue (the "Q" column), or null.</summary>
    public int? QueuePosition { get => _queuePosition; private set => Set(ref _queuePosition, value); }

    public long Size
    {
        get => _size;
        private set
        {
            if (Set(ref _size, value))
            {
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public string SizeText => DisplayFormat.Size(_size);

    public long Downloaded { get => _downloaded; private set => Set(ref _downloaded, value); }

    public DownloadStatus Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(StatusRank));
            }
        }
    }

    public bool IsCompleted => _status == DownloadStatus.Completed;

    public bool IsError => _status == DownloadStatus.Error;

    public bool IsActive => _status.IsActive();

    /// <summary>Sort key for the Status column: active first, then by progress.</summary>
    public double StatusRank => _status switch
    {
        DownloadStatus.Completed => 300,
        DownloadStatus.Error => 200,
        _ when _status.IsActive() => _size > 0 ? _downloaded * 100.0 / _size : 0,
        _ => 100 + (_size > 0 ? _downloaded * 99.0 / _size / 100 : 0),
    };

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public double Rate
    {
        get => _rate;
        private set
        {
            if (Set(ref _rate, value))
            {
                OnPropertyChanged(nameof(RateText));
            }
        }
    }

    public string RateText => DisplayFormat.Rate(_rate);

    public double TimeLeftSeconds => _timeLeft?.TotalSeconds ?? double.MaxValue;

    public TimeSpan? TimeLeft
    {
        get => _timeLeft;
        private set
        {
            if (Set(ref _timeLeft, value))
            {
                OnPropertyChanged(nameof(TimeLeftText));
                OnPropertyChanged(nameof(TimeLeftSeconds));
            }
        }
    }

    public string TimeLeftText => DisplayFormat.Duration(_timeLeft, compact: true);

    public DateTime? LastTry
    {
        get => _lastTry;
        private set
        {
            if (Set(ref _lastTry, value))
            {
                OnPropertyChanged(nameof(LastTryText));
            }
        }
    }

    public string LastTryText => DisplayFormat.ShortDate(_lastTry);

    public string AddedText => DisplayFormat.ShortDate(AddedAt);

    public string? Description { get => _description; private set => Set(ref _description, value); }

    public string SaveTo { get => _saveTo; private set => Set(ref _saveTo, value); }

    public string? Referrer { get => _referrer; private set => Set(ref _referrer, value); }

    public int Connections
    {
        get => _connections;
        private set
        {
            if (Set(ref _connections, value))
            {
                OnPropertyChanged(nameof(ConnectionsText));
            }
        }
    }

    public string ConnectionsText => _connections > 0 ? _connections.ToString(Localizer.Culture) : string.Empty;

    public string? LastError { get => _lastError; private set => Set(ref _lastError, value); }

    /// <summary>Tooltip for the row: the error when there is one.</summary>
    public string? ToolTip => _lastError;

    /// <summary>Applies the stored state (after the engine or the user changed something).</summary>
    public void Update(Download download)
    {
        Model = download;
        FileName = string.IsNullOrEmpty(download.FileName) ? download.Url : download.FileName;
        var extension = System.IO.Path.GetExtension(download.FileName);
        if (extension != _iconExtension)
        {
            _iconExtension = extension;
            Icon = ShellService.IconFor(string.IsNullOrEmpty(download.FileName) ? "file" : download.FileName);
        }

        CategoryId = download.CategoryId;
        QueueId = download.QueueId;
        QueuePosition = download.QueueId is null ? null : download.QueuePosition;
        Size = download.Size;
        Downloaded = download.Downloaded;
        Status = download.Status;
        LastTry = download.LastTryAt;
        Description = download.Description;
        SaveTo = download.SavePath;
        Referrer = download.Referrer;
        LastError = download.LastError;
        OnPropertyChanged(nameof(ToolTip));
        if (!download.Status.IsActive())
        {
            Rate = 0;
            TimeLeft = null;
            Connections = 0;
        }

        StatusText = TextForStatus(download.Status, download.Downloaded, download.Size);
    }

    /// <summary>Applies live progress from the engine.</summary>
    public void UpdateProgress(DownloadProgress progress)
    {
        Size = progress.Size;
        Downloaded = progress.Downloaded;
        Status = progress.Status;
        Rate = progress.BytesPerSecond;
        TimeLeft = progress.TimeLeft;
        Connections = progress.ActiveConnections;
        StatusText = TextForStatus(progress.Status, progress.Downloaded, progress.Size);
    }

    private static string TextForStatus(DownloadStatus status, long downloaded, long size) => status switch
    {
        DownloadStatus.Completed => Localizer.Get("Status_Complete"),
        DownloadStatus.Error => Localizer.Get("Status_Error"),
        DownloadStatus.Paused => Localizer.Get("Status_Paused"),
        DownloadStatus.Queued => Localizer.Get("Status_Queued"),
        DownloadStatus.Connecting => Localizer.Get("Status_Connecting"),
        DownloadStatus.Receiving when size > 0 => DisplayFormat.Percent(downloaded, size, Localizer.Culture),
        DownloadStatus.Receiving => DisplayFormat.Size(downloaded, 2, Localizer.Culture),
        DownloadStatus.Assembling => Localizer.Get("Status_Assembling"),
        DownloadStatus.Merging => Localizer.Get("Status_Merging"),
        DownloadStatus.Scanning => Localizer.Get("Status_Scanning"),
        DownloadStatus.WaitingForRetry => Localizer.Get("Status_Retrying"),
        DownloadStatus.Refreshing => Localizer.Get("Status_Refreshing"),
        _ => string.Empty,
    };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
