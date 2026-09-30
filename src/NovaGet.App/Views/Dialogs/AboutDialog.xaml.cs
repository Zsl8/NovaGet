using System.IO;
using System.Reflection;
using System.Windows;
using NovaGet.App.Services;
using NovaGet.Core;

namespace NovaGet.App.Views.Dialogs;

public partial class AboutDialog : DialogWindow
{
    public AboutDialog()
    {
        InitializeComponent();
        ProductText.Text = $"{AppInfo.ProductName} {AppInfo.InformationalVersion}";
        CopyrightText.Text = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
    }

    private void OnNotices(object sender, RoutedEventArgs e)
    {
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.txt");
        if (File.Exists(notices))
        {
            ShellService.OpenFile(notices);
        }
    }
}
