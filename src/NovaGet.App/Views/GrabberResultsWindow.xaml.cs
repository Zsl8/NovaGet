using System.Windows;
using System.Windows.Threading;
using NovaGet.App.Localization;
using NovaGet.App.ViewModels;
using NovaGet.Core.Engine;
using NovaGet.Core.Services;

namespace NovaGet.App.Views;

/// <summary>The live results of a site grabber run.</summary>
public partial class GrabberResultsWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly IDownloadService _downloads;
    private readonly Func<long?> _pickQueue;

    /// <param name="pickQueue">Asks which queue "Add selected to queue" uses (null = cancelled).</param>
    public GrabberResultsWindow(GrabberResultsViewModel viewModel, IDownloadService downloads, Func<long?> pickQueue)
    {
        ViewModel = viewModel;
        _downloads = downloads;
        _pickQueue = pickQueue;
        DataContext = viewModel;
        InitializeComponent();
        FlowDirection = Localizer.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Title = Localizer.Format("Grabber_ResultsWindowTitle", viewModel.ProjectName);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _downloads.StateChanged += OnDownloadStateChanged;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _downloads.StateChanged -= OnDownloadStateChanged;
            ViewModel.Dispose();
        };
        Tick();
        _timer.Start();
    }

    public GrabberResultsViewModel ViewModel { get; }

    private void Tick()
    {
        ViewModel.Flush();
        PauseButton.Content = Localizer.Get(ViewModel.IsPaused ? "Grabber_Resume" : "Grabber_Pause");
    }

    private void OnDownloadStateChanged(object? sender, DownloadStateChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => ViewModel.OnDownloadStateChanged(e));

    private void OnDownloadSelected(object sender, RoutedEventArgs e) => Add(queueId: null);

    private void OnAddToQueue(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected.Count == 0)
        {
            MessageBox.Show(this, Localizer.Get("Links_NothingSelected"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_pickQueue() is { } queueId)
        {
            Add(queueId);
        }
    }

    private void Add(long? queueId)
    {
        if (ViewModel.Selected.Count == 0)
        {
            MessageBox.Show(this, Localizer.Get("Links_NothingSelected"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ViewModel.AddSelected(queueId);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
