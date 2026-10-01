using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaGet.App.Services;

namespace NovaGet.App.ViewModels;

public enum TreeNodeKind
{
    AllDownloads,
    Category,
    Unfinished,
    Finished,
    GrabberProjects,
    GrabberProject,
    Queues,
    Queue,
}

/// <summary>Which downloads a category node shows: everything under All Downloads, Unfinished or Finished.</summary>
public enum ListScope
{
    All,
    Unfinished,
    Finished,
}

/// <summary>A node of the Categories pane. Selecting it filters the download list.</summary>
public sealed partial class TreeNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public TreeNodeViewModel(TreeNodeKind kind, string title, string iconName, ListScope scope = ListScope.All)
    {
        Kind = kind;
        _title = title;
        IconName = iconName;
        Scope = scope;
    }

    public TreeNodeKind Kind { get; }

    public string IconName { get; }

    public ImageSource Icon => AppImages.Get(IconName, 32);

    public ListScope Scope { get; }

    /// <summary>Set for category nodes (and the root nodes' General category).</summary>
    public long? CategoryId { get; init; }

    public long? QueueId { get; init; }

    public long? GrabberProjectId { get; init; }

    public bool IsBuiltIn { get; init; }

    /// <summary>This category and every sub-category below it.</summary>
    public HashSet<long> CategoryIds { get; } = [];

    /// <summary>Grabber nodes: the downloads the project(s) made.</summary>
    public HashSet<long> DownloadIds { get; } = [];

    public TreeNodeViewModel? Parent { get; set; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public bool IsCategoryNode => Kind is TreeNodeKind.AllDownloads or TreeNodeKind.Category or TreeNodeKind.Unfinished or TreeNodeKind.Finished;

    public bool IsQueueNode => Kind == TreeNodeKind.Queue;

    /// <summary>Whether a download belongs in the list while this node is selected.</summary>
    public bool Matches(DownloadItemViewModel item)
    {
        switch (Kind)
        {
            case TreeNodeKind.Queues:
                return item.QueueId is not null;
            case TreeNodeKind.Queue:
                return item.QueueId == QueueId;
            case TreeNodeKind.GrabberProjects:
            case TreeNodeKind.GrabberProject:
                return DownloadIds.Contains(item.Id);
        }

        if (Scope == ListScope.Unfinished && item.IsCompleted)
        {
            return false;
        }

        if (Scope == ListScope.Finished && !item.IsCompleted)
        {
            return false;
        }

        // Root nodes (All Downloads / Unfinished / Finished) show every category.
        return Kind != TreeNodeKind.Category || CategoryIds.Contains(item.CategoryId);
    }
}
