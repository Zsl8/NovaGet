using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NovaGet.App.Localization;
using NovaGet.App.Services;
using NovaGet.App.ViewModels;
using NovaGet.App.ViewModels.Scheduler;
using NovaGet.Core.Settings;

namespace NovaGet.App.Views;

/// <summary>Downloads → Scheduler (section 10). One instance; reopening it brings it to the front.</summary>
public partial class SchedulerWindow : Window
{
    private readonly SchedulerViewModel _vm;
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _timer;
    private Point _dragStart;
    private DownloadItemViewModel? _dragItem;

    public SchedulerWindow(SchedulerViewModel vm, ISettingsService settings, long? queueId = null)
    {
        _vm = vm;
        _settings = settings;
        InitializeComponent();
        FlowDirection = Localizer.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        SetResourceReference(BackgroundProperty, SystemColors.ControlBrushKey);
        DataContext = vm;
        if (queueId is { } id)
        {
            vm.Select(id);
        }

        WindowPlacementHelper.Apply(this, settings.Current.Ui.Scheduler);
        vm.PropertyChanged += OnViewModelChanged;
        SelectVisibleTab();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => vm.RefreshProgress(), Dispatcher);
        _timer.Start();
    }

    internal SchedulerViewModel ViewModel => _vm;

    public void Select(long queueId) => _vm.Select(queueId);

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !_vm.HasChanges)
        {
            return;
        }

        var answer = MessageBox.Show(this, Localizer.Get("Scheduler_ApplyChanges"), Localizer.Get("Scheduler_Title"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !_vm.ApplyAll()))
        {
            e.Cancel = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _vm.PropertyChanged -= OnViewModelChanged;
        _vm.Dispose();
        _settings.Update(s => WindowPlacementHelper.Capture(this, s.Ui.Scheduler));
        base.OnClosed(e);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SchedulerViewModel.SelectedQueue))
        {
            SelectVisibleTab();
        }
    }

    /// <summary>The synchronization queue shows "Synchronization" instead of "Schedule".</summary>
    private void SelectVisibleTab()
    {
        var sync = _vm.SelectedQueue?.IsSyncQueue == true;
        if (Tabs.SelectedItem != FilesTab)
        {
            Tabs.SelectedItem = sync ? SyncTab : ScheduleTab;
        }
    }

    private void OnBrowseOpenFile(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedQueue is not { } queue)
        {
            return;
        }

        var dialog = new OpenFileDialog { Filter = Localizer.Get("Filter_AllFiles"), FileName = queue.OpenFilePath };
        if (dialog.ShowDialog(this) == true)
        {
            queue.OpenFilePath = dialog.FileName;
            queue.OpenFileWhenDone = true;
        }
    }

    // ---------------------------------------------------------- drag and drop reordering

    private void OnFilesMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(FilesList);
        _dragItem = ItemAt(e.OriginalSource as DependencyObject);
    }

    private void OnFilesMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null)
        {
            return;
        }

        var delta = e.GetPosition(FilesList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(FilesList, new DataObject(typeof(DownloadItemViewModel), item), DragDropEffects.Move);
    }

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(DownloadItemViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DownloadItemViewModel)) is not DownloadItemViewModel dragged)
        {
            return;
        }

        var target = ItemAt(e.OriginalSource as DependencyObject);
        var index = target is null ? _vm.Files.Count - 1 : _vm.Files.IndexOf(target);
        _vm.MoveFile(dragged, index);
        e.Handled = true;
    }

    private static DownloadItemViewModel? ItemAt(DependencyObject? element)
    {
        while (element is not null and not ListViewItem)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return (element as ListViewItem)?.DataContext as DownloadItemViewModel;
    }
}
