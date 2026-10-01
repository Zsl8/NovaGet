using System.Windows;
using NovaGet.Core.Settings;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Options → General → Edit context menu items: which entries the extension adds to the browser's menu.</summary>
public partial class ContextMenuItemsDialog : DialogWindow
{
    public ContextMenuItemsDialog(ContextMenuSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        LinkBox.IsChecked = settings.DownloadWithNovaGet;
        AllLinksBox.IsChecked = settings.DownloadAllLinks;
        VideoBox.IsChecked = settings.DownloadVideo;
    }

    public ContextMenuSettings Result { get; private set; } = new();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = new ContextMenuSettings
        {
            DownloadWithNovaGet = LinkBox.IsChecked == true,
            DownloadAllLinks = AllLinksBox.IsChecked == true,
            DownloadVideo = VideoBox.IsChecked == true,
        };
        Accept();
    }
}
