using System.Windows;
using NovaGet.App.ViewModels;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Tasks → Run site grabber: template and start page, save location, exploration, files (Back / Next / Finish).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed when the window closes.")]
public partial class GrabberWizardDialog : DialogWindow
{
    private readonly Func<string?, string?> _pickFolder;
    private readonly Func<Uri, CancellationToken, Task<string>>? _waitForLogin;
    private readonly Action<Uri>? _openInBrowser;
    private readonly CancellationTokenSource _closing = new();

    public GrabberWizardDialog(
        GrabberWizardViewModel viewModel,
        Func<string?, string?> pickFolder,
        Func<Uri, CancellationToken, Task<string>>? waitForLogin = null,
        Action<Uri>? openInBrowser = null)
    {
        ViewModel = viewModel;
        _pickFolder = pickFolder;
        _waitForLogin = waitForLogin;
        _openInBrowser = openInBrowser;
        DataContext = viewModel;
        InitializeComponent();
        PasswordBox.Password = viewModel.Password ?? string.Empty;
        PasswordBox.PasswordChanged += (_, _) => ViewModel.Password = PasswordBox.Password;
        Loaded += (_, _) => StartBox.Focus();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _closing.Dispose();
        };
    }

    public GrabberWizardViewModel ViewModel { get; }

    private void OnBack(object sender, RoutedEventArgs e) => ViewModel.Step = Math.Max(0, ViewModel.Step - 1);

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Validate(ViewModel.Step) is { } problem)
        {
            MessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ViewModel.Step = Math.Min(GrabberWizardViewModel.StepCount - 1, ViewModel.Step + 1);
    }

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ValidateAll() is { } problem)
        {
            ViewModel.Step = problem.Step;
            MessageBox.Show(this, problem.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Accept();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (_pickFolder(ViewModel.SaveFolder) is { } folder)
        {
            ViewModel.SaveFolder = folder;
        }
    }

    /// <summary>Opens the start page in the browser and waits for the login the user sends from the extension's popup.</summary>
    private async void OnLoginViaBrowser(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Validate(0) is { } problem)
        {
            MessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var start = new Uri(ViewModel.StartUrl.Trim());

        if (_waitForLogin is null || ViewModel.IsWaitingForLogin)
        {
            return;
        }

        ViewModel.IsWaitingForLogin = true;
        try
        {
            var login = _waitForLogin(start, _closing.Token);
            _openInBrowser?.Invoke(start);
            ViewModel.Cookies = await login;
        }
        catch (OperationCanceledException)
        {
            // The wizard was closed.
        }
        finally
        {
            ViewModel.IsWaitingForLogin = false;
        }
    }
}
