using System.Windows;
using NovaGet.App.Localization;
using NovaGet.App.ViewModels.Options;
using NovaGet.Core.Settings;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Options → General → Edit panel for web players: the floating "Download this video" panel (section 12).</summary>
public partial class WebPlayerPanelDialog : DialogWindow
{
    public WebPlayerPanelDialog(WebPlayerPanelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        ShowBox.IsChecked = settings.Show;
        PopupBox.IsChecked = settings.ShowInPopupWindows;
        HoverBox.IsChecked = settings.ShowOnlyOnHover;
        PositionBox.ItemsSource = Enum.GetValues<WebPanelPosition>()
            .Select(p => new Choice<WebPanelPosition>(p, Localizer.Get("WebPanel_" + p)))
            .ToList();
        PositionBox.SelectedValue = settings.Position;
        ExceptionsBox.Text = string.Join(Environment.NewLine, settings.ExcludedSites);
    }

    public WebPlayerPanelSettings Result { get; private set; } = new();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = new WebPlayerPanelSettings
        {
            Show = ShowBox.IsChecked == true,
            ShowInPopupWindows = PopupBox.IsChecked == true,
            ShowOnlyOnHover = HoverBox.IsChecked == true,
            Position = PositionBox.SelectedValue is WebPanelPosition position ? position : WebPanelPosition.TopRight,
            ExcludedSites = ExceptionsBox.Text
                .Split(['\r', '\n', ' ', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length <= 255)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
        Accept();
    }
}
