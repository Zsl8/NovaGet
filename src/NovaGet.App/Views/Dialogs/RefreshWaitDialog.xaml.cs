using System.Windows;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Shown while NovaGet waits for the browser to deliver a download's new address.</summary>
public partial class RefreshWaitDialog : DialogWindow
{
    private readonly Action _cancel;

    public RefreshWaitDialog(string fileName, Action cancel)
    {
        _cancel = cancel;
        InitializeComponent();
        FileText.Text = fileName;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancel();
        Close();
    }
}
