using System.Windows;
using NovaGet.App.Localization;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Share text to copy; nothing is sent anywhere.</summary>
public partial class TellAFriendDialog : DialogWindow
{
    public TellAFriendDialog()
    {
        InitializeComponent();
        MessageBox.Text = Localizer.Get("TellFriend_Message");
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(MessageBox.Text);
            CopiedText.Text = Localizer.Get("TellFriend_Copied");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }
}
