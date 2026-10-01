using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using NovaGet.App.Localization;
using NovaGet.App.Services;
using NovaGet.App.ViewModels;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.CommandLine;
using NovaGet.Core.Paths;
using NovaGet.Core.Settings;

namespace NovaGet.App.Views;

public partial class MainWindow : Window
{
    private const string DragFormat = "NovaGet.DownloadIds";

    private readonly MainViewModel _vm;
    private readonly ISettingsService _settings;
    private readonly IAppController _controller;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly List<(string Id, DataGridColumn Column)> _columns;
    private readonly List<(string Id, bool Visible, double Width)> _defaultColumns;
    private Point _dragStart;
    private bool _dragArmed;
    private string _typeAhead = string.Empty;
    private DateTime _typeAheadAt;

    public MainWindow(MainViewModel viewModel, ISettingsService settings, IAppController controller, IDialogService dialogs, AppPaths paths)
    {
        _vm = viewModel;
        _settings = settings;
        _controller = controller;
        _dialogs = dialogs;
        _paths = paths;
        InitializeComponent();
        SourceInitialized += (_, _) => Services.ThemeService.ApplyTitleBar(this);
        DataContext = viewModel;
        FlowDirection = Localizer.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        _columns =
        [
            ("FileName", ColFileName), ("Queue", ColQueue), ("Size", ColSize), ("Status", ColStatus), ("TimeLeft", ColTimeLeft),
            ("Rate", ColRate), ("LastTry", ColLastTry), ("Description", ColDescription), ("SaveTo", ColSaveTo),
            ("Referrer", ColReferrer), ("Added", ColAdded), ("Connections", ColConnections),
        ];
        _defaultColumns = [.. _columns.Select(c => (c.Id, c.Column.Visibility == Visibility.Visible, c.Column.Width.DisplayValue))];

        WindowPlacementHelper.Apply(this, settings.Current.Ui.MainWindow);
        ApplyColumnSettings(settings.Current.Ui.Columns);
        ApplyCategoriesPane();
        BuildArrangeMenu();
        BuildLanguageMenu();
        UpdateSortArrows();

        viewModel.SortChanged += (_, _) => UpdateSortArrows();
        viewModel.RevealRequested += (_, item) => Reveal(item);
        viewModel.ColumnsDialogRequested += (_, _) => ShowColumnsDialog();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveLayout();
        if (!_controller.IsExiting && _settings.Current.General.CloseButtonHidesToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
        if (!_controller.IsExiting)
        {
            _controller.RequestExit();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowCategories))
        {
            ApplyCategoriesPane();
        }
    }

    // ----------------------------------------------------------------- layout persistence

    private void SaveLayout()
    {
        var paneWidth = _vm.ShowCategories ? CategoriesColumn.ActualWidth : _settings.Current.Ui.CategoriesPaneWidth;
        _settings.Update(s =>
        {
            WindowPlacementHelper.Capture(this, s.Ui.MainWindow);
            if (paneWidth >= 60)
            {
                s.Ui.CategoriesPaneWidth = paneWidth;
            }

            s.Ui.Columns = [.. _columns.Select(c => new ColumnSetting
            {
                Id = c.Id,
                Width = c.Column.ActualWidth,
                DisplayIndex = c.Column.DisplayIndex,
                Visible = c.Column.Visibility == Visibility.Visible,
            })];
        });
    }

    private void ApplyColumnSettings(IReadOnlyList<ColumnSetting> saved)
    {
        if (saved.Count == 0)
        {
            return;
        }

        foreach (var setting in saved)
        {
            var column = _columns.Find(c => c.Id == setting.Id).Column;
            if (column is null)
            {
                continue;
            }

            if (setting.Width >= 16)
            {
                column.Width = new DataGridLength(setting.Width);
            }

            column.Visibility = setting.Visible || setting.Id == "FileName" ? Visibility.Visible : Visibility.Collapsed;
        }

        var order = saved.Where(s => _columns.Exists(c => c.Id == s.Id)).OrderBy(s => s.DisplayIndex).Select(s => s.Id).ToList();
        order.AddRange(_columns.Select(c => c.Id).Where(id => !order.Contains(id)));
        for (var i = 0; i < order.Count; i++)
        {
            _columns.Find(c => c.Id == order[i]).Column.DisplayIndex = i;
        }
    }

    private void ApplyCategoriesPane()
    {
        if (_vm.ShowCategories)
        {
            CategoriesColumn.Width = new GridLength(Math.Max(100, _settings.Current.Ui.CategoriesPaneWidth));
            CategoriesPane.Visibility = Visibility.Visible;
            PaneSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (CategoriesColumn.ActualWidth >= 60)
            {
                var width = CategoriesColumn.ActualWidth;
                _settings.Update(s => s.Ui.CategoriesPaneWidth = width);
            }

            CategoriesColumn.Width = new GridLength(0);
            CategoriesPane.Visibility = Visibility.Collapsed;
            PaneSplitter.Visibility = Visibility.Collapsed;
        }
    }

    // ----------------------------------------------------------------- menus built in code

    private void BuildArrangeMenu()
    {
        foreach (var entry in _vm.ArrangeMenu)
        {
            ArrangeMenu.Items.Add(EntryItem(entry));
        }

        ArrangeMenu.Items.Add(new Separator());
        foreach (var entry in _vm.SortDirectionMenu)
        {
            ArrangeMenu.Items.Add(EntryItem(entry));
        }
    }

    private static MenuItem EntryItem(MenuEntryViewModel entry)
    {
        var item = new MenuItem { Header = entry.Header, Command = entry.Command, CommandParameter = entry.Parameter };
        item.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(MenuEntryViewModel.IsChecked)) { Source = entry, Mode = BindingMode.OneWay });
        return item;
    }

    private void BuildLanguageMenu()
    {
        var folder = Path.Combine(_paths.ExecutableDir, "lang");
        foreach (var culture in Localizer.AvailableLanguages(folder))
        {
            var item = new MenuItem
            {
                Header = culture.NativeName,
                IsChecked = string.Equals(culture.TwoLetterISOLanguageName, Localizer.Culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase),
            };
            var name = culture.Name;
            item.Click += (_, _) =>
            {
                _settings.Update(s => s.General.Language = name);
                _dialogs.Info(Localizer.Get("Msg_LanguageRestart"));
            };
            LanguageMenu.Items.Add(item);
        }
    }

    private void OnToolbarDropDown(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ToolbarItemViewModel { DropDown: { } entries })
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, FlowDirection = FlowDirection };
        foreach (var entry in entries)
        {
            menu.Items.Add(new MenuItem { Header = entry.Header, Command = entry.Command, CommandParameter = entry.Parameter });
        }

        menu.IsOpen = true;
    }

    // ----------------------------------------------------------------- list: selection, sorting, keyboard

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SetSelection(DownloadList.SelectedItems.OfType<DownloadItemViewModel>());

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrEmpty(e.Column.SortMemberPath))
        {
            _vm.ToggleSort(e.Column.SortMemberPath);
        }
    }

    private void UpdateSortArrows()
    {
        foreach (var (_, column) in _columns)
        {
            column.SortDirection = column.SortMemberPath == _vm.SortKey
                ? (_vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowItem(e.OriginalSource) is { } item)
        {
            _vm.Activate(item);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Delete:
                Execute(modifiers == ModifierKeys.Shift ? _vm.DeleteWithFileCommand : _vm.DeleteCommand);
                e.Handled = true;
                break;
            case Key.Enter when modifiers == ModifierKeys.None:
                if (_vm.Selection.Count == 1)
                {
                    if (_vm.Selection[0].IsCompleted)
                    {
                        Execute(_vm.OpenCommand);
                    }
                    else
                    {
                        _vm.Activate(_vm.Selection[0]);
                    }
                }

                e.Handled = true;
                break;
            case Key.Space when modifiers == ModifierKeys.None:
                Execute(_vm.TogglePauseCommand);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Type-ahead: typing selects the first file whose name starts with the typed text.</summary>
    private void OnListTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
        {
            return;
        }

        if (DateTime.UtcNow - _typeAheadAt > TimeSpan.FromSeconds(1))
        {
            _typeAhead = string.Empty;
        }

        _typeAheadAt = DateTime.UtcNow;
        _typeAhead += e.Text;
        var match = _vm.ItemsView.Cast<DownloadItemViewModel>()
            .FirstOrDefault(i => i.FileName.StartsWith(_typeAhead, StringComparison.CurrentCultureIgnoreCase));
        if (match is not null)
        {
            Reveal(match);
        }

        e.Handled = true;
    }

    private void Reveal(DownloadItemViewModel item)
    {
        DownloadList.SelectedItems.Clear();
        DownloadList.SelectedItem = item;
        DownloadList.ScrollIntoView(item);
        if (DownloadList.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
        {
            row.Focus();
        }
    }

    private static void Execute(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    // ----------------------------------------------------------------- list: column chooser

    private void OnListRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is null)
        {
            return;
        }

        var menu = new ContextMenu { FlowDirection = FlowDirection };
        foreach (var (id, column) in _columns.OrderBy(c => c.Column.DisplayIndex))
        {
            var item = new MenuItem
            {
                Header = column.Header,
                IsCheckable = id != "FileName",
                IsChecked = column.Visibility == Visibility.Visible,
                IsEnabled = id != "FileName",
            };
            item.Click += (_, _) =>
            {
                column.Visibility = column.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                SaveLayout();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Localizer.Get("Cmd_ShowColumns"), Command = _vm.ShowColumnsCommand });
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ShowColumnsDialog()
    {
        var current = _columns.OrderBy(c => c.Column.DisplayIndex)
            .Select(c => (c.Id, Label: c.Column.Header?.ToString() ?? c.Id, Visible: c.Column.Visibility == Visibility.Visible))
            .ToList();
        var defaults = _defaultColumns
            .Select(d => (d.Id, Label: _columns.Find(c => c.Id == d.Id).Column.Header?.ToString() ?? d.Id, d.Visible))
            .ToList();
        var dialog = ReorderDialogs.ForColumns(current, defaults, "FileName");
        if (_dialogs.ShowModal(dialog) != true)
        {
            return;
        }

        var index = 0;
        foreach (var (id, visible) in dialog.Result)
        {
            var column = _columns.Find(c => c.Id == id).Column;
            column.DisplayIndex = index++;
            column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        SaveLayout();
    }

    // ----------------------------------------------------------------- drag and drop

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragArmed = RowItem(e.OriginalSource) is not null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragArmed = false;
        var ids = _vm.Selection.Select(i => i.Id).ToArray();
        if (ids.Length > 0)
        {
            DragDrop.DoDragDrop(DownloadList, new DataObject(DragFormat, ids), DragDropEffects.Move);
        }
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) && NodeAt(e.OriginalSource) is { } node && MainViewModel.CanDropOn(node)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is long[] ids && NodeAt(e.OriginalSource) is { } node && MainViewModel.CanDropOn(node))
        {
            _vm.MoveToNode(ids, node);
        }
    }

    /// <summary>Links, address text or .url files dropped on the list: one opens Add URL, several the links dialog.</summary>
    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !e.Data.GetDataPresent(DragFormat) && DroppedLinks.HasLinks(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat) && DroppedLinks.From(e.Data) is { Count: > 0 } links)
        {
            _controller.AddDropped(links);
        }
    }

    // ----------------------------------------------------------------- categories tree

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedNode = e.NewValue as TreeNodeViewModel;

    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = CategoryTree.ContextMenu!;
        menu.Items.Clear();
        menu.FlowDirection = FlowDirection;
        if (CategoryTree.SelectedItem is not TreeNodeViewModel node)
        {
            e.Handled = true;
            return;
        }

        void Add(string key, System.Windows.Input.ICommand command) =>
            menu.Items.Add(new MenuItem { Header = Localizer.Get(key), Command = command, CommandParameter = node });

        switch (node.Kind)
        {
            case TreeNodeKind.AllDownloads or TreeNodeKind.Unfinished or TreeNodeKind.Finished or TreeNodeKind.Category:
                Add("Cmd_AddCategory", _vm.AddCategoryCommand);
                if (node.Kind == TreeNodeKind.Category)
                {
                    Add("Cmd_EditCategory", _vm.EditCategoryCommand);
                    Add("Cmd_DeleteCategory", _vm.DeleteCategoryCommand);
                }

                menu.Items.Add(new Separator());
                Add("Cmd_OpenFolder", _vm.OpenCategoryFolderCommand);
                break;
            case TreeNodeKind.Queues:
                Add("Cmd_CreateQueue", _vm.CreateQueueCommand);
                break;
            case TreeNodeKind.GrabberProjects:
                Add("Cmd_RunGrabber", _vm.GrabberCommand);
                break;
            case TreeNodeKind.GrabberProject:
                Add("Cmd_GrabberRunAgain", _vm.RunGrabberProjectCommand);
                Add("Cmd_GrabberEdit", _vm.EditGrabberProjectCommand);
                menu.Items.Add(new Separator());
                Add("Cmd_GrabberDelete", _vm.DeleteGrabberProjectCommand);
                break;
            case TreeNodeKind.Queue:
                Add("Cmd_QueueStart", _vm.QueueStartCommand);
                Add("Cmd_QueueStop", _vm.QueueStopCommand);
                Add("Cmd_QueueEdit", _vm.QueueEditCommand);
                menu.Items.Add(new Separator());
                Add("Cmd_CreateQueue", _vm.CreateQueueCommand);
                Add("Cmd_DeleteQueue", _vm.DeleteQueueCommand);
                break;
            default:
                e.Handled = true;
                break;
        }
    }

    // ----------------------------------------------------------------- helpers

    private DownloadItemViewModel? RowItem(object source) =>
        source is DependencyObject element && FindAncestor<DataGridColumnHeader>(element) is null
            ? (ItemsControl.ContainerFromElement(DownloadList, element) as DataGridRow)?.Item as DownloadItemViewModel
            : null;

    private static TreeNodeViewModel? NodeAt(object source) =>
        FindAncestor<TreeViewItem>(source as DependencyObject)?.DataContext as TreeNodeViewModel;

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }
}
