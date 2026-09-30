using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaGet.App.Services;

namespace NovaGet.App.ViewModels;

/// <summary>A toolbar button. Buttons with <see cref="DropDown"/> items get a ▼ part (queues, delete variants).</summary>
public sealed partial class ToolbarItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isVisible = true;

    public ToolbarItemViewModel(string id, string label, string iconName, ICommand command)
    {
        Id = id;
        Label = label;
        IconName = iconName;
        Command = command;
    }

    public string Id { get; }

    public string Label { get; }

    public string IconName { get; }

    public ICommand Command { get; }

    public ImageSource LargeIcon => AppImages.Get(IconName, 96);

    public ImageSource SmallIcon => AppImages.Get(IconName, 48);

    public ObservableCollection<MenuEntryViewModel>? DropDown { get; init; }

    public bool HasDropDown => DropDown is not null;
}

/// <summary>A dynamic menu entry (queue lists, presets).</summary>
public sealed partial class MenuEntryViewModel(string header, ICommand? command, object? parameter = null) : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public string Header { get; } = header;

    public ICommand? Command { get; } = command;

    public object? Parameter { get; } = parameter;

    public bool IsCheckable { get; init; }
}
