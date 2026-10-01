using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NovaGet.App.Localization;
using NovaGet.App.ViewModels;

namespace NovaGet.App.Views.Dialogs;

/// <summary>"Download all links": pick links, a category and a folder, then download now or later.</summary>
public partial class LinksDialog : DialogWindow
{
    private readonly LinksViewModel _vm;

    public LinksDialog(LinksRequest request)
    {
        InitializeComponent();
        _vm = new LinksViewModel(request);
        DataContext = _vm;
        FlowDirection = Localizer.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    internal LinksViewModel ViewModel => _vm;

    /// <summary>Null for "Download" (start now); a queue id for "Download Later".</summary>
    public long? QueueId { get; private set; }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        if (Validate())
        {
            QueueId = null;
            Accept();
        }
    }

    private void OnDownloadLater(object sender, RoutedEventArgs e)
    {
        if (!Validate())
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = LaterButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var queue in _vm.Request.Queues)
        {
            var id = queue.Id;
            var item = new MenuItem { Header = queue.Title };
            item.Click += (_, _) =>
            {
                QueueId = id;
                Accept();
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private bool Validate()
    {
        if (_vm.SelectedLinks.Count == 0)
        {
            MessageBox.Show(this, Localizer.Get("Links_NothingSelected"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (!_vm.IsAutomaticCategory && !Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(_vm.SaveFolder.Trim())))
        {
            MessageBox.Show(this, Localizer.Get("Error_InvalidPath"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            FolderBox.Focus();
            return false;
        }

        return true;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = _vm.SaveFolder };
        if (dialog.ShowDialog(this) == true)
        {
            _vm.SaveFolder = dialog.FolderName;
        }
    }

    /// <summary>Space toggles the selected rows.</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || LinksList.SelectedItems.Count == 0)
        {
            return;
        }

        var items = LinksList.SelectedItems.Cast<LinkItemViewModel>().ToList();
        var value = !items[0].IsChecked;
        foreach (var item in items)
        {
            item.IsChecked = value;
        }

        e.Handled = true;
    }
}
