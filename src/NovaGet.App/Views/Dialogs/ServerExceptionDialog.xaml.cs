using System.Windows;
using NovaGet.App.Localization;
using NovaGet.Core.Settings;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Options → Connection → Exceptions: a server (wildcards allowed) and its maximum connections.</summary>
public partial class ServerExceptionDialog : DialogWindow
{
    public ServerExceptionDialog(string host, int maxConnections)
    {
        InitializeComponent();
        HostBox.Text = host;
        HostBox.SelectAll();
        CountBox.ItemsSource = ConnectionSettings.AllowedConnectionCounts;
        CountBox.SelectedItem = SettingsNormalizer.NearestAllowedConnections(maxConnections);
    }

    public string Host { get; private set; } = string.Empty;

    public int MaxConnections { get; private set; }

    /// <summary>A host name or a <c>*.domain</c> pattern; a pasted address is reduced to its host.</summary>
    public static string? NormalizeHost(string text)
    {
        text = text.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0 && text.Contains("://", StringComparison.Ordinal))
        {
            text = uri.Host;
        }

        var bare = text.StartsWith("*.", StringComparison.Ordinal) ? text[2..] : text;
        return bare.Length is > 0 and <= 253 && Uri.CheckHostName(bare) != UriHostNameType.Unknown ? text.ToLowerInvariant() : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (NormalizeHost(HostBox.Text) is not { } host)
        {
            ErrorText.Text = Localizer.Get("Options_ErrorServer");
            ErrorText.Visibility = Visibility.Visible;
            HostBox.Focus();
            return;
        }

        Host = host;
        MaxConnections = CountBox.SelectedItem is int count ? count : 8;
        Accept();
    }
}
