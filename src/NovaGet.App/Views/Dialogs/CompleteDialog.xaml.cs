using System.Windows;
using NovaGet.App.Services;
using NovaGet.Core.Formatting;
using NovaGet.Core.Models;
using NovaGet.Core.Settings;

namespace NovaGet.App.Views.Dialogs;

/// <summary>"Download complete" with Open / Open with / Open folder. Non-modal.</summary>
public partial class CompleteDialog : DialogWindow
{
    private readonly string _path;
    private readonly ISettingsService _settings;

    public CompleteDialog(Download download, ISettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        _path = download.FullPath;
        FileIcon.Source = ShellService.IconFor(download.FileName, large: true);
        AddressText.Text = string.IsNullOrEmpty(download.OriginalUrl) ? download.Url : download.OriginalUrl;
        SizeText.Text = DisplayFormat.Size(download.Size, 2, Localization.Localizer.Culture);
        PathText.Text = _path;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DontShowBox.IsChecked == true)
        {
            _settings.Update(s => s.Downloads.ShowCompleteDialog = false);
        }

        base.OnClosed(e);
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        ShellService.OpenFile(_path);
        Close();
    }

    private void OnOpenWith(object sender, RoutedEventArgs e)
    {
        ShellService.OpenWith(_path);
        Close();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        ShellService.OpenFolder(_path);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
