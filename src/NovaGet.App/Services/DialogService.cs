using System.Linq;
using System.Windows;
using Microsoft.Win32;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core;

namespace NovaGet.App.Services;

internal sealed class DialogService : IDialogService
{
    public Window? ActiveWindow =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;

    public void Info(string message, string? title = null) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string message, string? title = null) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string? title = null) =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public (bool Yes, bool Checked) ConfirmWithCheck(string message, string checkText, bool checkDefault, string? title = null)
    {
        var dialog = new MessageCheckDialog(title ?? AppInfo.ProductName, message, checkText, checkDefault);
        var yes = ShowModal(dialog) == true;
        return (yes, dialog.IsChecked);
    }

    public bool? ShowModal(Window dialog)
    {
        var owner = ActiveWindow;
        if (owner is not null && !ReferenceEquals(owner, dialog) && owner.IsVisible)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog();
    }

    public string? PickFolder(string? initialFolder, string? title = null)
    {
        var dialog = new OpenFolderDialog { Title = title ?? string.Empty, InitialDirectory = initialFolder ?? string.Empty };
        return dialog.ShowDialog(ActiveWindow) == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string? initialPath, string filter, string? title = null)
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            Title = title ?? string.Empty,
            FileName = System.IO.Path.GetFileName(initialPath) ?? string.Empty,
            InitialDirectory = System.IO.Path.GetDirectoryName(initialPath) ?? string.Empty,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(ActiveWindow) == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string filter, string? title = null)
    {
        var dialog = new OpenFileDialog { Filter = filter, Title = title ?? string.Empty };
        return dialog.ShowDialog(ActiveWindow) == true ? dialog.FileName : null;
    }

    private MessageBoxResult Show(string message, string? title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = ActiveWindow;
        var options = Localization.Localizer.IsRightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
        return owner is { IsVisible: true }
            ? MessageBox.Show(owner, message, title ?? AppInfo.ProductName, buttons, image, MessageBoxResult.None, options)
            : MessageBox.Show(message, title ?? AppInfo.ProductName, buttons, image, MessageBoxResult.None, options);
    }
}
