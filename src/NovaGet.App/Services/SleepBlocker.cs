using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using NovaGet.Core.Engine;
using NovaGet.Core.Settings;

namespace NovaGet.App.Services;

/// <summary>
/// Section 16: keeps Windows from sleeping while downloads run (<c>SetThreadExecutionState</c>, Options → General,
/// on by default). The state belongs to the UI thread, which lives as long as the app.
/// </summary>
internal sealed partial class SleepBlocker(IDownloadEngine engine, ISettingsService settings) : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private DispatcherTimer? _timer;
    private bool _blocking;

    public bool IsBlocking => _blocking;

    public void Initialize()
    {
        if (_timer is not null || Application.Current is null)
        {
            return;
        }

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Update(), Application.Current.Dispatcher);
        _timer.Start();
    }

    /// <summary>Blocks sleep while anything downloads (and the option is on); allows it again otherwise.</summary>
    public void Update()
    {
        var wanted = settings.Current.General.PreventSleepWhileDownloading && engine.RunningIds.Count > 0;
        if (wanted == _blocking)
        {
            return;
        }

        // A zero result means the call failed; the next tick tries again.
        _blocking = !OperatingSystem.IsWindows() || SetThreadExecutionState(wanted ? EsContinuous | EsSystemRequired : EsContinuous) != 0 ? wanted : _blocking;
    }

    public void Dispose()
    {
        _timer?.Stop();
        if (_blocking && OperatingSystem.IsWindows())
        {
            _ = SetThreadExecutionState(EsContinuous);
        }

        _blocking = false;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
}
