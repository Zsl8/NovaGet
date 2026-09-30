using System.Windows;
using System.Windows.Threading;

namespace NovaGet.App.Tests;

/// <summary>
/// One WPF Application on a dedicated STA thread for all UI tests (WPF allows one per process).
/// The app's theme is merged like App.xaml does; the app uses assembly-qualified pack URIs, so its
/// resources resolve even though the test host is the entry assembly.
/// </summary>
public sealed class WpfFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _ready = new();

    public WpfFixture()
    {
        _thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/NovaGet;component/Themes/Classic.xaml", UriKind.Absolute),
            });
            _ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
        Dispatcher = _ready.Task.GetAwaiter().GetResult();
    }

    public Dispatcher Dispatcher { get; }

    /// <summary>Runs <paramref name="action"/> on the UI thread and rethrows its exception.</summary>
    public void Run(Action action) => Dispatcher.Invoke(action);

    public T Run<T>(Func<T> func) => Dispatcher.Invoke(func);

    /// <summary>Lets the dispatcher process layout, bindings and rendering.</summary>
    public void Pump() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

[CollectionDefinition(Name)]
public sealed class WpfCollection : ICollectionFixture<WpfFixture>
{
    public const string Name = "WPF";
}
