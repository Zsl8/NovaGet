using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using NovaGet.App.Localization;
using NovaGet.App.Services;
using NovaGet.Core.CommandLine;
using NovaGet.Core.Formatting;
using NovaGet.Core.Models;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Download properties: destination, address swap, credentials, connections and checksum check.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The token source lives for one checksum run and is disposed in finally.")]
public partial class PropertiesDialog : DialogWindow
{
    private readonly Download _download;
    private readonly IDownloadService _downloads;
    private CancellationTokenSource? _verifying;

    public PropertiesDialog(Download download, IDownloadService downloads)
    {
        InitializeComponent();
        _download = download;
        _downloads = downloads;

        Title = $"{Localizer.Get("Props_Title")} - {download.FileName}";
        FileIcon.Source = ShellService.IconFor(download.FileName, large: true);
        NameText.Text = download.FileName;
        var extension = Path.GetExtension(download.FileName).TrimStart('.').ToUpperInvariant();
        TypeText.Text = extension.Length > 0 ? Localizer.Format("Props_TypeFile", extension) : string.Empty;
        StatusText.Text = download.Status == DownloadStatus.Error && download.LastError is { } error
            ? $"{Localizer.Get("Status_Error")}: {error}"
            : download.Status == DownloadStatus.Completed ? Localizer.Get("Status_Complete")
            : download.Size > 0 ? DisplayFormat.Percent(download.Downloaded, download.Size, Localizer.Culture)
            : Localizer.Get("Status_Paused");
        SizeText.Text = download.Size >= 0 ? DisplayFormat.Size(download.Size, 2, Localizer.Culture) : Localizer.Get("Progress_Unknown");
        SaveToBox.Text = download.SavePath;
        AddressBox.Text = download.Url;
        ReferrerBox.Text = download.Referrer ?? string.Empty;
        DescriptionBox.Text = download.Description ?? string.Empty;
        LoginBox.Text = download.AuthUser ?? string.Empty;
        PasswordBox.Password = download.AuthPassword ?? string.Empty;
        AddedText.Text = download.AddedAt.ToLocalTime().ToString("g", Localizer.Culture);
        CompletedText.Text = download.CompletedAt?.ToLocalTime().ToString("g", Localizer.Culture) ?? string.Empty;

        var connections = new List<string> { Localizer.Get("Props_ConnectionsDefault") };
        connections.AddRange(ConnectionSettings.AllowedConnectionCounts.Select(c => c.ToString(CultureInfo.CurrentCulture)));
        ConnectionsBox.ItemsSource = connections;
        ConnectionsBox.SelectedIndex = download.MaxConnections is { } max ? Math.Max(0, Array.IndexOf(ConnectionSettings.AllowedConnectionCounts, max) + 1) : 0;

        AlgorithmBox.ItemsSource = Checksum.Algorithms;
        AlgorithmBox.SelectedItem = download.ChecksumAlgo ?? Checksum.Sha256;
        ExpectedBox.Text = download.ChecksumExpected ?? string.Empty;

        var completed = download.Status == DownloadStatus.Completed;
        OpenButton.IsEnabled = completed;
        OpenWithButton.IsEnabled = completed;
        VerifyButton.IsEnabled = completed;
        Closed += (_, _) => _verifying?.Cancel();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = SaveToBox.Text };
        if (dialog.ShowDialog(this) == true)
        {
            SaveToBox.Text = dialog.FolderName;
        }
    }

    private void OnExpectedChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (Checksum.GuessAlgorithm(ExpectedBox.Text) is { } guess)
        {
            AlgorithmBox.SelectedItem = guess;
        }
    }

    private async void OnVerify(object sender, RoutedEventArgs e)
    {
        if (_verifying is not null)
        {
            return;
        }

        var algorithm = (string)AlgorithmBox.SelectedItem;
        _verifying = new CancellationTokenSource();
        VerifyButton.IsEnabled = false;
        var progress = new Progress<double>(p => ChecksumResult.Text = Localizer.Format("Props_Verifying", p.ToString("P0", CultureInfo.CurrentCulture)));
        try
        {
            var actual = await Checksum.ComputeAsync(_download.FullPath, algorithm, progress, _verifying.Token);
            ChecksumResult.Foreground = System.Windows.SystemColors.ControlTextBrush;
            if (string.IsNullOrWhiteSpace(ExpectedBox.Text))
            {
                ChecksumResult.Text = Localizer.Format("Props_ChecksumComputed", actual);
            }
            else if (Checksum.Matches(ExpectedBox.Text, actual))
            {
                ChecksumResult.Text = Localizer.Get("Props_ChecksumOk");
                ChecksumResult.Foreground = System.Windows.Media.Brushes.DarkGreen;
            }
            else
            {
                ChecksumResult.Text = Localizer.Format("Props_ChecksumBad", actual);
                ChecksumResult.Foreground = System.Windows.Media.Brushes.Firebrick;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ChecksumResult.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _verifying?.Dispose();
            _verifying = null;
            if (IsLoaded)
            {
                VerifyButton.IsEnabled = true;
            }
        }
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        var address = CommandLineParser.NormalizeUrlArgument(AddressBox.Text);
        if (address is null)
        {
            ShowError(Localizer.Get("Error_InvalidAddress"));
            return;
        }

        var folder = SaveToBox.Text.Trim();
        if (!Path.IsPathFullyQualified(folder))
        {
            ShowError(Localizer.Get("Error_InvalidPath"));
            return;
        }

        var edited = _downloads.Find(_download.Id);
        if (edited is null)
        {
            DialogResult = false;
            return;
        }

        if (address != edited.Url)
        {
            // Swapping the address (e.g. an expired link): the new one is used from now on, also for restarts.
            edited.Url = address;
            edited.OriginalUrl = address;
        }

        edited.Referrer = NullIfEmpty(ReferrerBox.Text);
        edited.Description = NullIfEmpty(DescriptionBox.Text);
        edited.AuthUser = NullIfEmpty(LoginBox.Text);
        edited.AuthPassword = string.IsNullOrEmpty(PasswordBox.Password) ? null : PasswordBox.Password;
        edited.MaxConnections = ConnectionsBox.SelectedIndex > 0 ? ConnectionSettings.AllowedConnectionCounts[ConnectionsBox.SelectedIndex - 1] : null;
        edited.ChecksumAlgo = (string)AlgorithmBox.SelectedItem;
        edited.ChecksumExpected = NullIfEmpty(ExpectedBox.Text);
        _downloads.Save(edited);

        if (!string.Equals(Path.GetFullPath(folder), Path.GetFullPath(edited.SavePath), StringComparison.OrdinalIgnoreCase))
        {
            var error = await _downloads.MoveOrRenameAsync(edited.Id, folder, edited.FileName);
            if (error is not null)
            {
                ShowError(error);
                return;
            }
        }

        Accept();
    }

    private void OnOpen(object sender, RoutedEventArgs e) => ShellService.OpenFile(_download.FullPath);

    private void OnOpenWith(object sender, RoutedEventArgs e) => ShellService.OpenWith(_download.FullPath);

    private void OnOpenFolder(object sender, RoutedEventArgs e) =>
        ShellService.OpenFolder(_download.Status == DownloadStatus.Completed ? _download.FullPath : _download.SavePath);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
