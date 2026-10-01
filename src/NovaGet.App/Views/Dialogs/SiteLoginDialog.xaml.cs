using System.Windows;
using NovaGet.App.Localization;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Options → Site Logins: a site pattern with the user name and password to send to it.</summary>
public partial class SiteLoginDialog : DialogWindow
{
    public SiteLoginDialog(string urlPattern, string user, string password)
    {
        InitializeComponent();
        SiteBox.Text = urlPattern;
        SiteBox.SelectAll();
        UserBox.Text = user;
        PasswordBox.Password = password;
    }

    public string UrlPattern { get; private set; } = string.Empty;

    public string User { get; private set; } = string.Empty;

    public string Password { get; private set; } = string.Empty;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var site = SiteBox.Text.Trim();
        string? error = null;
        if (site.Length == 0 || site.Length > 2048 || site.Any(char.IsWhiteSpace))
        {
            error = Localizer.Get("Options_ErrorPattern");
        }
        else if (UserBox.Text.Trim().Length == 0)
        {
            error = Localizer.Get("Options_ErrorUser");
        }

        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        UrlPattern = site;
        User = UserBox.Text.Trim();
        Password = PasswordBox.Password;
        Accept();
    }
}
