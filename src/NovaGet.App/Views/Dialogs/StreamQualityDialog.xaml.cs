using System.Windows;
using NovaGet.App.ViewModels;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Choose the quality (and audio language) of an HLS/DASH stream before it is added.</summary>
public partial class StreamQualityDialog : DialogWindow
{
    public StreamQualityDialog(StreamQualityViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public StreamQualityViewModel ViewModel { get; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not null)
        {
            Accept();
        }
    }
}
