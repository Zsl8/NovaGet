using System.IO;
using System.Windows;
using Microsoft.Win32;
using NovaGet.App.Localization;
using NovaGet.Core.Models;
using NovaGet.Core.Services;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Move/Rename: a finished file is moved on disk; an unfinished download gets the new destination.</summary>
public partial class MoveRenameDialog : DialogWindow
{
    private readonly long _id;
    private readonly IDownloadService _downloads;

    public MoveRenameDialog(Download download, IDownloadService downloads)
    {
        InitializeComponent();
        _id = download.Id;
        _downloads = downloads;
        NameBox.Text = download.FileName;
        FolderBox.Text = download.SavePath;
        var stem = Path.GetFileNameWithoutExtension(download.FileName).Length;
        NameBox.Select(0, stem);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = FolderBox.Text };
        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        var name = NameBox.Text.Trim();
        if (name.Length == 0 || folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            ShowError(Localizer.Get(name.Length == 0 ? "Error_NameRequired" : "Error_InvalidPath"));
            return;
        }

        OkButton.IsEnabled = false;
        var error = await _downloads.MoveOrRenameAsync(_id, folder, name);
        OkButton.IsEnabled = true;
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        Accept();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
