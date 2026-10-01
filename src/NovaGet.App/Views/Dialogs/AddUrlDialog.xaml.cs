using System.Windows;
using NovaGet.App.Localization;
using NovaGet.Core.CommandLine;
using NovaGet.Core.Engine;

namespace NovaGet.App.Views.Dialogs;

/// <summary>"Enter new address to download". OK probes the address, then the caller shows the File Info dialog.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The token source lives for one probe and is disposed in finally.")]
public partial class AddUrlDialog : DialogWindow
{
    private readonly Func<RequestContext, CancellationToken, Task<ProbeResult>> _probe;
    private readonly string _userAgent;
    private CancellationTokenSource? _probing;
    private string? _failedUrl;

    public AddUrlDialog(IEnumerable<string> history, string? initialUrl, string userAgent, Func<RequestContext, CancellationToken, Task<ProbeResult>> probe)
    {
        InitializeComponent();
        _probe = probe;
        _userAgent = userAgent;
        AddressBox.ItemsSource = history.ToList();
        AddressBox.Text = initialUrl ?? string.Empty;
        AddressBox.Loaded += (_, _) =>
        {
            if (AddressBox.Template.FindName("PART_EditableTextBox", AddressBox) is System.Windows.Controls.TextBox box)
            {
                box.SelectAll();
            }
        };
    }

    public string Url { get; private set; } = string.Empty;

    public string? UserName => UseAuthBox.IsChecked == true && LoginBox.Text.Length > 0 ? LoginBox.Text : null;

    public string? Password => UseAuthBox.IsChecked == true ? PasswordBox.Password : null;

    /// <summary>What the server said, or null when the user chose to add the address without a successful probe.</summary>
    public ProbeResult? Probe { get; private set; }

    private void OnAuthToggled(object sender, RoutedEventArgs e) =>
        AuthGrid.Visibility = UseAuthBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        var url = CommandLineParser.NormalizeUrlArgument(AddressBox.Text);
        if (url is null)
        {
            ShowStatus(Localizer.Get("Error_InvalidAddress"));
            return;
        }

        Url = url;
        if (url == _failedUrl)
        {
            // Second OK after a failed check: add it anyway and let the engine try.
            Accept();
            return;
        }

        SetBusy(true);
        ShowStatus(Localizer.Get("AddUrl_Checking"));
        _probing = new CancellationTokenSource();
        try
        {
            Probe = await _probe(new RequestContext
            {
                Url = new Uri(url),
                UserAgent = _userAgent,
                UserName = UserName,
                Password = Password,
                Timeout = TimeSpan.FromSeconds(20),
            }, _probing.Token);
            Accept();
        }
        catch (OperationCanceledException)
        {
        }
        catch (DownloadException ex)
        {
            _failedUrl = url;
            ShowStatus(Localizer.Format("Error_ProbeFailed", ex.Message) + "\n" + Localizer.Get("AddUrl_AddAnyway"));
        }
        finally
        {
            _probing?.Dispose();
            _probing = null;
            if (IsLoaded)
            {
                SetBusy(false);
            }
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _probing?.Cancel();
        DialogResult = false;
    }

    private void SetBusy(bool busy)
    {
        AddressBox.IsEnabled = !busy;
        UseAuthBox.IsEnabled = !busy;
        AuthGrid.IsEnabled = !busy;
        OkButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }
}
