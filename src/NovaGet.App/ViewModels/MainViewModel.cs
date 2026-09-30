using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaGet.App.Services;
using NovaGet.Core;

namespace NovaGet.App.ViewModels;

public sealed partial class MainViewModel(IAppController controller) : ObservableObject
{
    public string Title => AppInfo.MainWindowTitle;

    [ObservableProperty]
    private string _statusText = "Ready";

    [RelayCommand]
    private void Exit() => controller.RequestExit();

    [RelayCommand]
    private static void About() => System.Windows.MessageBox.Show(
        $"{AppInfo.ProductName} {AppInfo.InformationalVersion}\n\nA free download manager.",
        $"About {AppInfo.ProductName}");
}
