using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using NovaGet.App.Localization;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Choose and order items with check boxes (Customize toolbar, Show/Hide columns).</summary>
public partial class ReorderDialog : DialogWindow
{
    private readonly IReadOnlyList<(string Id, string Label, ImageSource? Icon, bool Visible)> _defaults;
    private readonly ObservableCollection<ReorderEntry> _entries = [];

    public ReorderDialog(
        string title,
        string intro,
        IReadOnlyList<(string Id, string Label, ImageSource? Icon, bool Visible)> current,
        IReadOnlyList<(string Id, string Label, ImageSource? Icon, bool Visible)> defaults,
        IReadOnlySet<string>? alwaysVisible = null)
    {
        InitializeComponent();
        Title = title;
        IntroText.Text = intro;
        _defaults = defaults;
        AlwaysVisible = alwaysVisible ?? new HashSet<string>();
        Load(current);
        EntryList.ItemsSource = _entries;
        ReorderList.Attach(EntryList, _entries);
    }

    /// <summary>Ids that can't be hidden (e.g. the File Name column).</summary>
    public IReadOnlySet<string> AlwaysVisible { get; }

    public IReadOnlyList<(string Id, bool Visible)> Result => [.. _entries.Select(e => (e.Id, e.IsChecked || AlwaysVisible.Contains(e.Id)))];

    private void Load(IEnumerable<(string Id, string Label, ImageSource? Icon, bool Visible)> items)
    {
        _entries.Clear();
        foreach (var (id, label, icon, visible) in items)
        {
            _entries.Add(new ReorderEntry(id, label, icon, visible || AlwaysVisible.Contains(id)));
        }
    }

    private void OnUp(object sender, RoutedEventArgs e) => ReorderList.Move(EntryList, _entries, -1);

    private void OnDown(object sender, RoutedEventArgs e) => ReorderList.Move(EntryList, _entries, +1);

    private void OnReset(object sender, RoutedEventArgs e) => Load(_defaults);

    private void OnOk(object sender, RoutedEventArgs e) => Accept();
}

/// <summary>Factories for the two uses of <see cref="ReorderDialog"/>.</summary>
public static class ReorderDialogs
{
    /// <summary>View → Toolbar → Customize.</summary>
    public static ReorderDialog ForToolbar(IReadOnlyList<(string Id, string Label, ImageSource Icon, bool Visible)> current, IReadOnlyList<string> defaultOrder) =>
        new(
            Localizer.Get("ToolbarCustomize_Title"),
            Localizer.Get("ToolbarCustomize_Intro"),
            [.. current.Select(c => (c.Id, c.Label, (ImageSource?)c.Icon, c.Visible))],
            [.. defaultOrder.Select(id => current.First(c => c.Id == id)).Select(c => (c.Id, c.Label, (ImageSource?)c.Icon, true))]);

    /// <summary>View → Show/Hide columns. The File Name column can't be hidden.</summary>
    public static ReorderDialog ForColumns(
        IReadOnlyList<(string Id, string Label, bool Visible)> current,
        IReadOnlyList<(string Id, string Label, bool Visible)> defaults,
        string alwaysVisible) =>
        new(
            Localizer.Get("Columns_Title"),
            Localizer.Get("Columns_Intro"),
            [.. current.Select(c => (c.Id, c.Label, (ImageSource?)null, c.Visible))],
            [.. defaults.Select(c => (c.Id, c.Label, (ImageSource?)null, c.Visible))],
            new HashSet<string> { alwaysVisible });
}
