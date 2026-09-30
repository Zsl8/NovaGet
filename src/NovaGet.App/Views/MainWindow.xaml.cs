using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using NovaGet.App.Services;
using NovaGet.App.ViewModels;
using NovaGet.Core.Settings;

namespace NovaGet.App.Views;

public partial class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly IAppController _controller;

    public MainWindow(MainViewModel viewModel, ISettingsService settings, IAppController controller)
    {
        _settings = settings;
        _controller = controller;
        InitializeComponent();
        DataContext = viewModel;
        WindowPlacementHelper.Apply(this, settings.Current.Ui.MainWindow);

        // Alt+F4 exits the app entirely; the caption X only hides to the tray (configurable).
        InputBindings.Add(new KeyBinding(viewModel.ExitCommand, Key.F4, ModifierKeys.Alt));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
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

    private void SavePlacement() =>
        _settings.Update(s => WindowPlacementHelper.Capture(this, s.Ui.MainWindow));
}
