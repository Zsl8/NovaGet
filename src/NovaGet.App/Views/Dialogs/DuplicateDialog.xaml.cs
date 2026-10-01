using System.Windows;
using NovaGet.App.Localization;
using NovaGet.Core.Settings;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Asks what to do when an address that is already in the list is added again.</summary>
public partial class DuplicateDialog : DialogWindow
{
    public DuplicateDialog(string existingName)
    {
        InitializeComponent();
        MessageText.Text = Localizer.Format("Duplicate_Message", existingName);
    }

    public DuplicateDownloadAction Action { get; private set; }

    private void OnNumbered(object sender, RoutedEventArgs e) => Done(DuplicateDownloadAction.AddNumbered);

    private void OnOverwrite(object sender, RoutedEventArgs e) => Done(DuplicateDownloadAction.AddAndOverwrite);

    private void OnShowExisting(object sender, RoutedEventArgs e) => Done(DuplicateDownloadAction.ShowCompleteOrResume);

    private void Done(DuplicateDownloadAction action)
    {
        Action = action;
        Accept();
    }
}
