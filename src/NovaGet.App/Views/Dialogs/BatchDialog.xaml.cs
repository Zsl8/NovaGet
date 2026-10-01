using System.Windows;
using NovaGet.App.ViewModels;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Tasks → Add batch download.</summary>
public partial class BatchDialog : DialogWindow
{
    public BatchDialog(string? initialAddress = null)
    {
        ViewModel = new BatchViewModel(initialAddress);
        DataContext = ViewModel;
        InitializeComponent();
    }

    public BatchViewModel ViewModel { get; }

    /// <summary>The login for every file, when "Use authorization" is checked.</summary>
    public (string User, string Password)? Login =>
        ViewModel.UseAuthorization && !string.IsNullOrWhiteSpace(ViewModel.UserName) ? (ViewModel.UserName.Trim(), PasswordBox.Password) : null;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (BatchViewModel.ErrorText(ViewModel.Generator.Error) is { } error)
        {
            MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            AddressBox.Focus();
            return;
        }

        Accept();
    }
}
