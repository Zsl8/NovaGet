using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using NovaGet.App.Localization;
using NovaGet.App.Services;
using NovaGet.Core.Engine.Naming;
using NovaGet.Core.Formatting;

namespace NovaGet.App.Views.Dialogs;

public enum FileInfoChoice
{
    Cancel,
    Start,
    Later,
}

/// <summary>A category or queue shown in a combo box or menu.</summary>
public sealed record ChoiceItem(long Id, string Title);

/// <summary>Everything the File Info dialog needs from the caller.</summary>
public sealed class FileInfoRequest
{
    public required string Url { get; init; }

    public required string FileName { get; init; }

    public long Size { get; init; } = -1;

    public required IReadOnlyList<ChoiceItem> Categories { get; init; }

    public long CategoryId { get; init; }

    /// <summary>Folder for a category id (custom folder or default).</summary>
    public required Func<long, string> FolderForCategory { get; init; }

    public IReadOnlyList<string> RecentFolders { get; init; } = [];

    public string? Description { get; init; }

    public string? InitialFolder { get; init; }

    public required IReadOnlyList<ChoiceItem> Queues { get; init; }

    public bool ShowQueuePicker { get; init; } = true;

    /// <summary>Adds a category (with its own dialog) and returns it, or null.</summary>
    public Func<ChoiceItem?>? AddCategory { get; init; }

    /// <summary>Creates a queue (with its own dialog) and returns it, or null.</summary>
    public Func<ChoiceItem?>? CreateQueue { get; init; }
}

/// <summary>"Download File Info": category, Save As, description; Start Download / Download Later / Cancel.</summary>
public partial class FileInfoDialog : DialogWindow
{
    private readonly FileInfoRequest _request;
    private readonly List<ChoiceItem> _categories;
    private readonly List<ChoiceItem> _queues;
    private string _folderForCategory;
    private bool _loading = true;

    public FileInfoDialog(FileInfoRequest request)
    {
        _request = request;
        _categories = [.. request.Categories];
        _queues = [.. request.Queues];
        InitializeComponent();

        UrlBox.Text = request.Url;
        DescriptionBox.Text = request.Description ?? string.Empty;
        CategoryBox.ItemsSource = _categories;
        CategoryBox.SelectedItem = _categories.Find(c => c.Id == request.CategoryId) ?? _categories.FirstOrDefault();
        _folderForCategory = request.FolderForCategory(SelectedCategoryId);
        var folder = string.IsNullOrWhiteSpace(request.InitialFolder) ? _folderForCategory : request.InitialFolder;
        var name = FileNameSanitizer.Sanitize(request.FileName);
        SaveAsBox.ItemsSource = request.RecentFolders.Where(Directory.Exists).Select(f => Path.Combine(f, name)).ToList();
        SaveAsBox.Text = Path.Combine(folder, FileNameSanitizer.MakeUnique(folder, name));
        NameText.Text = name;
        FileIcon.Source = ShellService.IconFor(name, large: true);
        SizeText.Text = Localizer.Format("FileInfo_Size", request.Size >= 0 ? DisplayFormat.Size(request.Size, 2, Localizer.Culture) : Localizer.Get("FileInfo_SizeUnknown"));
        UpdateRememberLabel();
        _loading = false;
    }

    public FileInfoChoice Choice { get; private set; }

    public long? QueueId { get; private set; }

    public long CategoryId => SelectedCategoryId;

    public string Folder => Path.GetDirectoryName(FullPath) ?? _folderForCategory;

    public string FileName => FileNameSanitizer.Sanitize(Path.GetFileName(FullPath), _request.FileName);

    public string Description => DescriptionBox.Text.Trim();

    /// <summary>"Remember this path for the category" was ticked and the folder differs from the category's.</summary>
    public bool RememberFolder => RememberBox.IsChecked == true;

    private string FullPath => Environment.ExpandEnvironmentVariables(SaveAsBox.Text.Trim().Trim('"'));

    private long SelectedCategoryId => (CategoryBox.SelectedItem as ChoiceItem)?.Id ?? _request.CategoryId;

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // Follow the category's folder unless the user already typed another folder.
        var newFolder = _request.FolderForCategory(SelectedCategoryId);
        if (string.Equals(Folder, _folderForCategory, StringComparison.OrdinalIgnoreCase))
        {
            SaveAsBox.Text = Path.Combine(newFolder, FileNameSanitizer.MakeUnique(newFolder, FileName));
        }

        _folderForCategory = newFolder;
        UpdateRememberLabel();
    }

    private void UpdateRememberLabel() =>
        RememberBox.Content = Localizer.Format("FileInfo_Remember", (CategoryBox.SelectedItem as ChoiceItem)?.Title ?? string.Empty);

    private void OnRecentFolderChosen(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SaveAsBox.SelectedItem is string path)
        {
            // Keep the current file name, take the folder of the recent entry.
            var folder = Path.GetDirectoryName(path) ?? _folderForCategory;
            Dispatcher.BeginInvoke(() => SaveAsBox.Text = Path.Combine(folder, FileName));
        }
    }

    private void OnAddCategory(object sender, RoutedEventArgs e)
    {
        if (_request.AddCategory?.Invoke() is { } added)
        {
            _categories.Add(added);
            CategoryBox.ItemsSource = null;
            CategoryBox.ItemsSource = _categories;
            CategoryBox.SelectedItem = added;
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = FileName,
            InitialDirectory = Directory.Exists(Folder) ? Folder : _folderForCategory,
            OverwritePrompt = false,
            Filter = "*.*|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            SaveAsBox.Text = dialog.FileName;
        }
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (!Validate())
        {
            return;
        }

        Choice = FileInfoChoice.Start;
        Accept();
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        if (!Validate())
        {
            return;
        }

        if (_request.ShowQueuePicker && _queues.Count > 1)
        {
            var picker = new QueuePickDialog(_queues) { Owner = this };
            if (picker.ShowDialog() != true)
            {
                return;
            }

            QueueId = picker.SelectedQueueId;
        }
        else
        {
            QueueId = _queues.FirstOrDefault()?.Id;
        }

        Choice = FileInfoChoice.Later;
        Accept();
    }

    private void OnLaterMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = LaterButton, Placement = PlacementMode.Bottom, FlowDirection = FlowDirection };
        foreach (var queue in _queues)
        {
            var item = new MenuItem { Header = queue.Title };
            item.Click += (_, _) => LaterTo(queue.Id);
            menu.Items.Add(item);
        }

        if (_request.CreateQueue is not null)
        {
            menu.Items.Add(new Separator());
            var create = new MenuItem { Header = Localizer.Get("FileInfo_CreateQueue") };
            create.Click += (_, _) =>
            {
                if (_request.CreateQueue() is { } queue)
                {
                    _queues.Add(queue);
                    LaterTo(queue.Id);
                }
            };
            menu.Items.Add(create);
        }

        menu.IsOpen = true;
    }

    private void LaterTo(long queueId)
    {
        if (!Validate())
        {
            return;
        }

        QueueId = queueId;
        Choice = FileInfoChoice.Later;
        Accept();
    }

    private bool Validate()
    {
        try
        {
            if (Path.IsPathFullyQualified(FullPath) && !string.IsNullOrWhiteSpace(Path.GetFileName(FullPath)))
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
        }

        MessageBox.Show(this, Localizer.Get("Error_InvalidPath"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        SaveAsBox.Focus();
        return false;
    }
}
