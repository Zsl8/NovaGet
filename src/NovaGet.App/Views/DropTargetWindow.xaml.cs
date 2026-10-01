using System.Windows;
using System.Windows.Input;
using NovaGet.App.Services;
using NovaGet.Core.Settings;

namespace NovaGet.App.Views;

/// <summary>
/// The drop target (section 12.3): a small always-on-top window that takes dragged links, text addresses and .url
/// files. Drag it anywhere; right-click for Hide, Add URL, Speed limiter and Exit. Its position is remembered.
/// </summary>
public partial class DropTargetWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly IAppController _controller;
    private readonly Func<IReadOnlyList<Uri>, Task> _add;

    internal DropTargetWindow(ISettingsService settings, IAppController controller, Func<IReadOnlyList<Uri>, Task> add)
    {
        _settings = settings;
        _controller = controller;
        _add = add;
        InitializeComponent();
        Badge.Source = AppImages.Get("novaget", 48);
        var ui = settings.Current.Ui;
        if (ui.DropTargetLeft is { } left && ui.DropTargetTop is { } top && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Bottom - Height - 24;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        DragMove();
        _settings.Update(s =>
        {
            s.Ui.DropTargetLeft = Left;
            s.Ui.DropTargetTop = Top;
        });
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        _controller.ShowMainWindow();
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        Highlight(DroppedLinks.HasLinks(e.Data));
        e.Effects = DroppedLinks.HasLinks(e.Data) ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = DroppedLinks.HasLinks(e.Data) ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        Highlight(false);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        Highlight(false);
        var links = DroppedLinks.From(e.Data);
        if (links.Count > 0)
        {
            _ = _add(links);
        }
    }

    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 32 && top >= SystemParameters.VirtualScreenTop - 32
        && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 32
        && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 32;

    private void Highlight(bool on) => Frame.Opacity = on ? 1.0 : 0.85;

    private void OnHide(object sender, RoutedEventArgs e) => _controller.ToggleDropTarget();

    private void OnAddUrl(object sender, RoutedEventArgs e) => _controller.ShowAddUrl();

    private void OnSpeedLimiter(object sender, RoutedEventArgs e)
    {
        _controller.ShowMainWindow();
        (Application.Current.MainWindow?.DataContext as ViewModels.MainViewModel)?.LimiterSettingsCommand.Execute(null);
    }

    private void OnExit(object sender, RoutedEventArgs e) => _controller.RequestExit();
}
