using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NovaGet.App.Views.Dialogs;

/// <summary>An entry in a reorderable checklist (toolbar buttons, list columns).</summary>
public sealed partial class ReorderEntry(string id, string label, ImageSource? icon, bool isChecked) : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked = isChecked;

    public string Id { get; } = id;

    public string Label { get; } = label;

    public ImageSource? Icon { get; } = icon;
}

/// <summary>Drag-and-drop and up/down reordering for a ListBox bound to a collection of <see cref="ReorderEntry"/>.</summary>
internal static class ReorderList
{
    public static void Attach(ListBox list, ObservableCollection<ReorderEntry> items)
    {
        Point start = default;
        list.AllowDrop = true;
        list.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(list);
        list.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || list.SelectedItem is not ReorderEntry entry)
            {
                return;
            }

            var delta = e.GetPosition(list) - start;
            if (Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance && e.OriginalSource is not CheckBox)
            {
                DragDrop.DoDragDrop(list, entry, DragDropEffects.Move);
            }
        };
        list.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(ReorderEntry)) is not ReorderEntry dragged)
            {
                return;
            }

            var target = (e.OriginalSource as FrameworkElement)?.DataContext as ReorderEntry;
            var from = items.IndexOf(dragged);
            var to = target is null ? items.Count - 1 : items.IndexOf(target);
            if (from >= 0 && to >= 0 && from != to)
            {
                items.Move(from, to);
                list.SelectedItem = dragged;
            }
        };
    }

    public static void Move(ListBox list, ObservableCollection<ReorderEntry> items, int delta)
    {
        if (list.SelectedItem is not ReorderEntry entry)
        {
            return;
        }

        var index = items.IndexOf(entry);
        var target = Math.Clamp(index + delta, 0, items.Count - 1);
        if (target != index)
        {
            items.Move(index, target);
            list.SelectedItem = entry;
            list.ScrollIntoView(entry);
        }
    }
}
