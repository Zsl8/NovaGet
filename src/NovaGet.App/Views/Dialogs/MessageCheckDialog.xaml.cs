using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace NovaGet.App.Views.Dialogs;

/// <summary>A yes/no question with a check box, e.g. "Delete N file(s)?" + "Delete also from disk".</summary>
public partial class MessageCheckDialog : DialogWindow
{
    public MessageCheckDialog(string title, string message, string checkText, bool checkDefault)
    {
        InitializeComponent();
        Title = title;
        Message.Text = message;
        Check.Content = checkText;
        Check.IsChecked = checkDefault;
        IconImage.Source = Imaging.CreateBitmapSourceFromHIcon(
            System.Drawing.SystemIcons.Question.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    }

    public bool IsChecked => Check.IsChecked == true;

    private void OnYes(object sender, RoutedEventArgs e) => Accept();
}
