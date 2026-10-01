using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaGet.App.Localization;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Engine.Naming;
using NovaGet.Core.Services;

namespace NovaGet.App.ViewModels;

/// <summary>A link offered in the "Download all links" dialog.</summary>
public sealed partial class LinkItemViewModel : ObservableObject
{
    public LinkItemViewModel(Uri url, string? description)
    {
        Url = url;
        var name = Uri.UnescapeDataString(url.AbsolutePath.Split('/')[^1]);
        if (string.IsNullOrWhiteSpace(name))
        {
            // A folder or site address: show the host, which has no file type.
            FileName = url.Host;
            Extension = string.Empty;
        }
        else
        {
            FileName = FileNameSanitizer.Sanitize(name);
            var dot = FileName.LastIndexOf('.');
            Extension = dot > 0 && dot < FileName.Length - 1 && FileName.Length - dot <= 10 ? FileName[(dot + 1)..].ToLowerInvariant() : string.Empty;
        }
        Description = description ?? string.Empty;
    }

    public Uri Url { get; }

    public string Address => Url.AbsoluteUri;

    public string FileName { get; }

    /// <summary>Lower-case extension without the dot; empty when there is none.</summary>
    public string Extension { get; }

    public string Type => Extension.Length == 0 ? "—" : Extension.ToUpperInvariant();

    public string Description { get; }

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>A per-extension check box: ticking it selects every link of that type.</summary>
public sealed partial class ExtensionFilterViewModel(string extension, int count, bool isChecked) : ObservableObject
{
    public string Extension { get; } = extension;

    public string Label { get; } = $"{(extension.Length == 0 ? Localizer.Get("Links_NoExtension") : extension.ToUpperInvariant())} ({count})";

    [ObservableProperty]
    private bool _isChecked = isChecked;
}

/// <summary>What the dialog shows: the links and where the user may put them.</summary>
public sealed record LinksRequest
{
    public required string Title { get; init; }

    public required IReadOnlyList<(Uri Url, string? Description)> Links { get; init; }

    /// <summary>Extensions selected at first (Options → File Types).</summary>
    public IReadOnlyList<string> PreferredExtensions { get; init; } = [];

    public required IReadOnlyList<ChoiceItem> Categories { get; init; }

    public required Func<long, string> FolderForCategory { get; init; }

    public required IReadOnlyList<ChoiceItem> Queues { get; init; }
}

/// <summary>The "Download all links" selection dialog (sections 12.1 and 13).</summary>
public sealed partial class LinksViewModel : ObservableObject
{
    /// <summary>"Automatic": each file goes to the category of its type.</summary>
    public const long AutomaticCategory = 0;

    private bool _syncing;

    public LinksViewModel(LinksRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        foreach (var (url, description) in request.Links)
        {
            var item = new LinkItemViewModel(url, description);
            item.PropertyChanged += OnItemChanged;
            Items.Add(item);
        }

        var preferred = request.PreferredExtensions;
        foreach (var group in Items.GroupBy(i => i.Extension).OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var selected = group.Key.Length > 0 && preferred.Any(p => CategoryMatcher.ExtensionMatches(p, group.Key));
            var filter = new ExtensionFilterViewModel(group.Key, group.Count(), selected);
            filter.PropertyChanged += OnFilterChanged;
            Extensions.Add(filter);
            foreach (var item in group)
            {
                item.IsChecked = selected;
            }
        }

        Categories = [new ChoiceItem(AutomaticCategory, Localizer.Get("Links_AutomaticCategory")), .. request.Categories];
        _categoryId = AutomaticCategory;
        _saveFolder = string.Empty;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Matches;
        UpdateCount();
    }

    public LinksRequest Request { get; }

    public ObservableCollection<LinkItemViewModel> Items { get; } = [];

    public ObservableCollection<ExtensionFilterViewModel> Extensions { get; } = [];

    public ICollectionView View { get; }

    public IReadOnlyList<ChoiceItem> Categories { get; }

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutomaticCategory))]
    private long _categoryId;

    [ObservableProperty]
    private string _saveFolder;

    [ObservableProperty]
    private string _selectionText = string.Empty;

    public bool IsAutomaticCategory => CategoryId == AutomaticCategory;

    public IReadOnlyList<LinkItemViewModel> SelectedLinks => [.. Items.Where(i => i.IsChecked)];

    [RelayCommand]
    private void SelectAll() => SetVisible(true);

    [RelayCommand]
    private void SelectNone() => SetVisible(false);

    partial void OnFilterTextChanged(string value) => View.Refresh();

    partial void OnCategoryIdChanged(long value) =>
        SaveFolder = value == AutomaticCategory ? string.Empty : Request.FolderForCategory(value);

    private bool Matches(object item)
    {
        var link = (LinkItemViewModel)item;
        var text = FilterText.Trim();
        return text.Length == 0
            || link.FileName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || link.Address.Contains(text, StringComparison.OrdinalIgnoreCase)
            || link.Description.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void SetVisible(bool isChecked)
    {
        foreach (LinkItemViewModel item in View)
        {
            item.IsChecked = isChecked;
        }
    }

    private void OnFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || sender is not ExtensionFilterViewModel filter || e.PropertyName != nameof(ExtensionFilterViewModel.IsChecked))
        {
            return;
        }

        _syncing = true;
        foreach (var item in Items.Where(i => i.Extension == filter.Extension))
        {
            item.IsChecked = filter.IsChecked;
        }

        _syncing = false;
        UpdateCount();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LinkItemViewModel.IsChecked) || _syncing)
        {
            return;
        }

        // Keep the type check box in step: ticked when every link of that type is.
        _syncing = true;
        foreach (var filter in Extensions)
        {
            filter.IsChecked = Items.Where(i => i.Extension == filter.Extension).All(i => i.IsChecked);
        }

        _syncing = false;
        UpdateCount();
    }

    private void UpdateCount() =>
        SelectionText = Localizer.Format("Links_Selected", Items.Count(i => i.IsChecked), Items.Count);
}
