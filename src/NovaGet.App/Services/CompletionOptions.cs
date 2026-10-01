using CommunityToolkit.Mvvm.ComponentModel;
using NovaGet.Core.Models;

namespace NovaGet.App.Services;

/// <summary>A download's "Options on completion" (progress dialog, third tab). Kept for the session.</summary>
public sealed partial class CompletionOptions : ObservableObject
{
    [ObservableProperty]
    private bool _showCompleteDialog = true;

    [ObservableProperty]
    private bool _hangUp;

    [ObservableProperty]
    private bool _exitWhenDone;

    [ObservableProperty]
    private bool _turnOff;

    [ObservableProperty]
    private PowerAction _powerAction = PowerAction.ShutDown;

    [ObservableProperty]
    private bool _forceProcesses;

    public static IReadOnlyList<PowerAction> PowerActions { get; } = [PowerAction.ShutDown, PowerAction.Sleep, PowerAction.Hibernate, PowerAction.LogOff];
}
