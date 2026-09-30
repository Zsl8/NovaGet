using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using NovaGet.App.Localization;
using NovaGet.App.ViewModels;
using NovaGet.Core;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Formatting;
using NovaGet.Core.Models;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;
using Icon = System.Drawing.Icon;
using MenuItem = System.Windows.Controls.MenuItem;

namespace NovaGet.App.Services;

/// <summary>
/// The notification-area icon: always visible while NovaGet runs. Shows an "active" icon (or a live
/// 16 × 16 speed graph) while downloading, a tooltip with the totals, the tray menu, and balloons for
/// finished and failed downloads.
/// </summary>
internal sealed partial class TrayIconService(
    IDownloadService downloads,
    IQueueRepository queues,
    ISettingsService settings) : IDisposable
{
    private const int GraphSamples = 16;

    private readonly Queue<double> _samples = new();
    private TaskbarIcon? _icon;
    private IAppController? _controller;
    private DispatcherTimer? _timer;
    private Icon? _idleIcon;
    private Icon? _activeIcon;
    private Icon? _graphIcon;
    private string? _lastCompletedPath;
    private bool _wasActive;

    public void Initialize(IAppController controller)
    {
        if (_icon is not null)
        {
            return;
        }

        _controller = controller;
        var size = SmallIconSize();
        _idleIcon = LoadIcon("novaget.ico", size);
        _activeIcon = LoadIcon("tray-active.ico", size);

        _icon = new TaskbarIcon
        {
            ToolTipText = AppInfo.ProductName,
            Icon = _idleIcon,
            ContextMenu = new ContextMenu(),
            NoLeftClickDelay = true,
        };
        _icon.ContextMenu.Opened += (_, _) => BuildMenu(_icon.ContextMenu);
        _icon.TrayMouseDoubleClick += (_, _) => controller.ShowMainWindow();
        _icon.TrayBalloonTipClicked += (_, _) =>
        {
            if (_lastCompletedPath is not null)
            {
                ShellService.OpenFolder(_lastCompletedPath);
            }
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
        BuildMenu(_icon.ContextMenu);

        downloads.StateChanged += OnStateChanged;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), Application.Current.Dispatcher);
        _timer.Start();
    }

    public void ShowBalloon(string title, string message, bool error = false) =>
        _icon?.ShowNotification(title, message, error ? NotificationIcon.Error : NotificationIcon.Info);

    public void Dispose()
    {
        downloads.StateChanged -= OnStateChanged;
        _timer?.Stop();
        _icon?.Dispose();
        _icon = null;
        _idleIcon?.Dispose();
        _activeIcon?.Dispose();
        _graphIcon?.Dispose();
    }

    private void OnStateChanged(object? sender, DownloadStateChangedEventArgs e)
    {
        if (e.Status is not (DownloadStatus.Completed or DownloadStatus.Error))
        {
            return;
        }

        var download = downloads.Find(e.Id);
        if (download is null)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (e.Status == DownloadStatus.Completed)
            {
                _lastCompletedPath = download.FullPath;
                ShowBalloon(Localizer.Get("Notify_Complete"), download.FileName);
            }
            else
            {
                _lastCompletedPath = null;
                ShowBalloon(Localizer.Get("Notify_Failed"), $"{download.FileName}\n{e.Message}", error: true);
            }
        });
    }

    /// <summary>Tooltip and icon state, once a second.</summary>
    private void Refresh()
    {
        if (_icon is null)
        {
            return;
        }

        var stats = downloads.GetStatistics();
        var active = stats.Active > 0;
        _icon.ToolTipText = active
            ? Localizer.Format("Tray_Tooltip", AppInfo.ProductName, stats.Active, DisplayFormat.Rate(stats.BytesPerSecond, 1, Localizer.Culture))
            : AppInfo.ProductName;

        _samples.Enqueue(stats.BytesPerSecond);
        while (_samples.Count > GraphSamples)
        {
            _samples.Dequeue();
        }

        if (active && settings.Current.General.ShowTraySpeedGraph)
        {
            var graph = DrawSpeedGraph(SmallIconSize(), [.. _samples]);
            _icon.Icon = graph;
            _graphIcon?.Dispose();
            _graphIcon = graph;
        }
        else if (active != _wasActive || _icon.Icon == _graphIcon)
        {
            _icon.Icon = active ? _activeIcon : _idleIcon;
        }

        _wasActive = active;
    }

    private void BuildMenu(ContextMenu menu)
    {
        var controller = _controller!;
        menu.Items.Clear();
        menu.FlowDirection = Localizer.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        menu.Items.Add(Item("Tray_Open", controller.ShowMainWindow, bold: true));
        menu.Items.Add(Item("Tray_AddDownload", () => controller.ShowAddUrl()));
        menu.Items.Add(new Separator());

        var limiter = settings.Current.SpeedLimiter;
        var speed = new MenuItem { Header = Localizer.Get("Menu_SpeedLimiter") };
        speed.Items.Add(Item("Cmd_TurnOn", () => settings.Update(s => s.SpeedLimiter.Enabled = true), check: limiter.Enabled));
        speed.Items.Add(Item("Cmd_TurnOff", () => settings.Update(s => s.SpeedLimiter.Enabled = false), check: !limiter.Enabled));
        speed.Items.Add(new Separator());
        foreach (var preset in SpeedLimiterSettings.TrayPresetsKBps)
        {
            var value = preset;
            speed.Items.Add(new MenuItem
            {
                Header = Localizer.Format("Tray_Preset", preset),
                IsChecked = limiter.Enabled && limiter.MaxKBps == preset,
                Command = new Command(() => settings.Update(s =>
                {
                    s.SpeedLimiter.Enabled = true;
                    s.SpeedLimiter.MaxKBps = value;
                })),
            });
        }

        speed.Items.Add(new Separator());
        speed.Items.Add(Item("Cmd_LimiterSettings", () =>
        {
            controller.ShowMainWindow();
            (Application.Current.MainWindow?.DataContext as MainViewModel)?.LimiterSettingsCommand.Execute(null);
        }));
        menu.Items.Add(speed);
        menu.Items.Add(Item("Cmd_PauseAll", () => _ = downloads.StopAllAsync()));
        menu.Items.Add(Item("Tray_ResumeAll", ResumeAll));
        menu.Items.Add(new Separator());

        var start = new MenuItem { Header = Localizer.Get("Menu_StartQueue") };
        var stop = new MenuItem { Header = Localizer.Get("Menu_StopQueue") };
        foreach (var queue in queues.GetAll())
        {
            var id = queue.Id;
            start.Items.Add(new MenuItem { Header = MainViewModel.QueueTitle(queue), Command = new Command(() => controller.StartQueue(id)) });
            stop.Items.Add(new MenuItem { Header = MainViewModel.QueueTitle(queue), Command = new Command(() => controller.StopQueue(id)) });
        }

        menu.Items.Add(start);
        menu.Items.Add(stop);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Cmd_ShowDropTarget", controller.ToggleDropTarget, check: settings.Current.General.ShowDropTarget));
        menu.Items.Add(Item("Tray_MonitorClipboard", () => settings.Update(s => s.General.MonitorClipboard = !s.General.MonitorClipboard),
            check: settings.Current.General.MonitorClipboard));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Cmd_Exit", controller.RequestExit));
    }

    private void ResumeAll()
    {
        foreach (var download in downloads.GetAll().Where(d => d.Status is DownloadStatus.Paused or DownloadStatus.Error))
        {
            downloads.Start(download.Id);
        }
    }

    private static MenuItem Item(string key, Action action, bool bold = false, bool? check = null)
    {
        var item = new MenuItem { Header = Localizer.Get(key), Command = new Command(action) };
        if (bold)
        {
            item.FontWeight = FontWeights.Bold;
        }

        if (check is { } isChecked)
        {
            item.IsChecked = isChecked;
        }

        return item;
    }

    /// <summary>A tiny bar chart of the last 16 seconds of total speed.</summary>
    internal static Icon DrawSpeedGraph(int size, IReadOnlyList<double> samples)
    {
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(255, 30, 41, 59));
            g.FillRectangle(background, 0, 0, size, size);
            using var bar = new SolidBrush(Color.FromArgb(255, 34, 197, 94));
            var max = Math.Max(1, samples.DefaultIfEmpty(0).Max());
            var barWidth = Math.Max(1f, (float)size / GraphSamples);
            var offset = GraphSamples - samples.Count;
            for (var i = 0; i < samples.Count; i++)
            {
                var height = (float)(samples[i] / max * (size - 2));
                if (height >= 0.5f)
                {
                    g.FillRectangle(bar, (offset + i) * barWidth, size - 1 - height, barWidth, height);
                }
            }

            using var border = new Pen(Color.FromArgb(255, 20, 196, 176));
            g.DrawRectangle(border, 0, 0, size - 1, size - 1);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Icon LoadIcon(string name, int size)
    {
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute))
            ?? throw new InvalidOperationException($"Missing icon resource {name}.");
        using var stream = resource.Stream;
        return new Icon(stream, size, size);
    }

    private static int SmallIconSize()
    {
        const int SmCxSmIcon = 49;
        var size = GetSystemMetrics(SmCxSmIcon);
        return size > 0 ? size : 16;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    private sealed class Command(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
