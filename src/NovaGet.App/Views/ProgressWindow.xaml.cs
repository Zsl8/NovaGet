using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using NovaGet.App.Localization;
using NovaGet.App.Services;
using NovaGet.Core.Engine;
using NovaGet.Core.Formatting;
using NovaGet.Core.Models;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.App.Views;

/// <summary>
/// The per-download progress dialog. Polls the engine four times a second; minimizing hides it to the tray.
/// </summary>
public partial class ProgressWindow : Window
{
    private readonly long _id;
    private readonly IDownloadService _downloads;
    private readonly IDownloadEngine _engine;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IAppController _controller;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<ConnectionRow> _connections = [];
    private bool _detailsVisible = true;
    private bool _loadingLimiter;

    public ProgressWindow(
        long id,
        IDownloadService downloads,
        IDownloadEngine engine,
        ISettingsService settings,
        IDialogService dialogs,
        IAppController controller,
        CompletionOptions completion)
    {
        _id = id;
        _downloads = downloads;
        _engine = engine;
        _settings = settings;
        _dialogs = dialogs;
        _controller = controller;
        InitializeComponent();
        FlowDirection = Localizer.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ConnectionList.ItemsSource = _connections;
        CompletionPanel.DataContext = completion;
        PowerCombo.ItemsSource = CompletionOptions.PowerActions;
        LoadLimiter();
        UpdateDetailsButton();
        Refresh();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _timer.Start();
    }

    public long DownloadId => _id;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            // Minimize sends the dialog to the tray; double-clicking the download shows it again.
            Hide();
            WindowState = WindowState.Normal;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    /// <summary>Re-reads the state now (e.g. right after it changed).</summary>
    public void Refresh()
    {
        var download = _downloads.Find(_id);
        if (download is null)
        {
            Close();
            return;
        }

        var progress = _engine.GetProgress(_id);
        var size = progress?.Size ?? download.Size;
        var downloaded = progress?.Downloaded ?? download.Downloaded;
        var status = progress?.Status ?? download.Status;
        var percent = size > 0 ? Math.Min(100, downloaded * 100.0 / size) : 0;
        var running = progress is not null;

        Title = size > 0
            ? Localizer.Format("Progress_TitleKnown", Math.Floor(percent).ToString("0", CultureInfo.CurrentCulture), download.FileName)
            : download.FileName;
        UrlText.Text = download.Url;
        StatusText.Text = StatusFor(status, progress?.Message ?? download.LastError);
        SizeText.Text = size >= 0 ? DisplayFormat.Size(size, 2, Localizer.Culture) : Localizer.Get("Progress_Unknown");
        DownloadedText.Text = size > 0
            ? Localizer.Format("Progress_DownloadedValue", DisplayFormat.Size(downloaded, 2, Localizer.Culture), percent.ToString("0.00", CultureInfo.CurrentCulture))
            : DisplayFormat.Size(downloaded, 2, Localizer.Culture);
        RateText.Text = running ? DisplayFormat.Rate(progress!.BytesPerSecond, 3, Localizer.Culture) : string.Empty;
        TimeLeftText.Text = running ? DisplayFormat.Duration(progress!.TimeLeft) : string.Empty;
        ResumeText.Text = (progress?.ResumeCapable ?? download.ResumeCapable) switch
        {
            true => Localizer.Get("Progress_Yes"),
            false => Localizer.Get("Progress_No"),
            null => Localizer.Get("Progress_Unknown"),
        };

        OverallBar.IsIndeterminate = running && size <= 0;
        OverallBar.Value = size > 0 ? percent * 10 : 0;
        Taskbar.ProgressState = !running ? TaskbarItemProgressState.Paused
            : size <= 0 ? TaskbarItemProgressState.Indeterminate
            : status == DownloadStatus.Error ? TaskbarItemProgressState.Error
            : TaskbarItemProgressState.Normal;
        Taskbar.ProgressValue = percent / 100;

        SegmentMap.TotalSize = size;
        SegmentMap.Segments = progress?.Segments;
        UpdateConnections(progress?.Connections ?? []);

        PauseButton.Content = Localizer.Get(running ? "Progress_Pause" : "Progress_Resume");
        PauseButton.IsEnabled = running || status.IsResumable();
    }

    private static string StatusFor(DownloadStatus status, string? message) => status switch
    {
        DownloadStatus.Receiving => Localizer.Get("Status_Receiving"),
        DownloadStatus.Connecting => Localizer.Get("Status_Connecting"),
        DownloadStatus.Error => string.IsNullOrEmpty(message) ? Localizer.Get("Status_Error") : $"{Localizer.Get("Status_Error")}: {message}",
        DownloadStatus.Paused when !string.IsNullOrEmpty(message) => $"{Localizer.Get("Status_Paused")}: {message}",
        DownloadStatus.Paused => Localizer.Get("Status_Paused"),
        DownloadStatus.Queued => Localizer.Get("Status_Queued"),
        DownloadStatus.Assembling => Localizer.Get("Status_Assembling"),
        DownloadStatus.Merging => Localizer.Get("Status_Merging"),
        DownloadStatus.Scanning => Localizer.Get("Status_Scanning"),
        DownloadStatus.Completed => Localizer.Get("Status_Complete"),
        DownloadStatus.WaitingForRetry => Localizer.Get("Status_Retrying"),
        DownloadStatus.Refreshing => Localizer.Get("Status_Refreshing"),
        _ => string.Empty,
    };

    private void UpdateConnections(IReadOnlyList<ConnectionProgress> connections)
    {
        while (_connections.Count > connections.Count)
        {
            _connections.RemoveAt(_connections.Count - 1);
        }

        for (var i = 0; i < connections.Count; i++)
        {
            if (i < _connections.Count)
            {
                _connections[i].Update(connections[i]);
            }
            else
            {
                _connections.Add(new ConnectionRow(connections[i]));
            }
        }
    }

    // ----------------------------------------------------------------- buttons

    private async void OnPauseResume(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning(_id))
        {
            PauseButton.IsEnabled = false;
            await _downloads.StopAsync(_id);
        }
        else
        {
            _controller.StartDownload(_id);
        }

        Refresh();
    }

    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        var download = _downloads.Find(_id);
        var hasData = (_engine.GetProgress(_id)?.Downloaded ?? download?.Downloaded ?? 0) > 0;
        if (hasData && !_dialogs.Confirm(Localizer.Get("Confirm_CancelDownload")))
        {
            return;
        }

        _timer.Stop();
        Close();
        await _downloads.RemoveAsync([_id], deleteFiles: true);
    }

    private void OnToggleDetails(object sender, RoutedEventArgs e)
    {
        _detailsVisible = !_detailsVisible;
        UpdateDetailsButton();
    }

    private void UpdateDetailsButton()
    {
        DetailsButton.Content = Localizer.Get(_detailsVisible ? "Progress_HideDetails" : "Progress_ShowDetails");
        DetailsPanel.Visibility = _detailsVisible ? Visibility.Visible : Visibility.Collapsed;
        SegmentMap.Visibility = _detailsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----------------------------------------------------------------- speed limiter tab

    private void LoadLimiter()
    {
        _loadingLimiter = true;
        var download = _downloads.Find(_id);
        var host = HostOf(download?.Url);
        var remembered = host is not null && _settings.Current.Connection.HostSpeedLimits.TryGetValue(host, out var hostLimit) ? hostLimit : (int?)null;
        var limit = download?.SpeedLimitKBps ?? remembered;
        UseLimiterBox.IsChecked = limit is > 0;
        LimitBox.Text = (limit is > 0 ? limit.Value : 500).ToString(CultureInfo.CurrentCulture);
        RememberBox.IsChecked = remembered is not null;
        _loadingLimiter = false;
    }

    private void OnLimitKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnLimiterChanged(sender, e);
            e.Handled = true;
        }
    }

    private void OnLimiterChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingLimiter || !IsInitialized)
        {
            return;
        }

        int? kbps = null;
        if (UseLimiterBox.IsChecked == true)
        {
            if (!int.TryParse(LimitBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value < 1)
            {
                return;
            }

            kbps = value;
        }

        _engine.SetSpeedLimit(_id, kbps);
        var download = _downloads.Find(_id);
        if (download is null)
        {
            return;
        }

        download.SpeedLimitKBps = kbps;
        _downloads.Save(download);
        if (HostOf(download.Url) is { } host)
        {
            _settings.Update(s =>
            {
                if (RememberBox.IsChecked == true && kbps is { } limit)
                {
                    s.Connection.HostSpeedLimits[host] = limit;
                }
                else
                {
                    s.Connection.HostSpeedLimits.Remove(host);
                }
            });
        }
    }

    private static string? HostOf(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>A row of the connection table.</summary>
    private sealed class ConnectionRow : INotifyPropertyChanged
    {
        private ConnectionProgress _value;

        public ConnectionRow(ConnectionProgress value) => _value = value;

        public event PropertyChangedEventHandler? PropertyChanged;

        public int Number => _value.Number;

        public string DownloadedText => DisplayFormat.Size(_value.Downloaded, 2, Localizer.Culture);

        public string Info => _value.Info;

        public void Update(ConnectionProgress value)
        {
            if (value == _value)
            {
                return;
            }

            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
