using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using NovaGet.Core.Integration;
using NovaGet.Core.Settings;

namespace NovaGet.App.Services;

/// <summary>
/// "Monitor clipboard" (tray menu): when the copied text is a single address with an auto-start file type, the Add
/// URL dialog opens with it. Uses AddClipboardFormatListener on a message-only window.
/// </summary>
internal sealed partial class ClipboardMonitor(ISettingsService settings, Lazy<IAppController> controller, ILogger<ClipboardMonitor> logger) : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static long s_ignoreUntil;
    private HwndSource? _window;
    private string? _lastUrl;
    private long _lastAt;

    /// <summary>NovaGet is about to copy something itself (Copy address): don't react to it.</summary>
    public static void IgnoreNextChange() => Interlocked.Exchange(ref s_ignoreUntil, Environment.TickCount64 + 1500);

    /// <summary>Copies text without triggering the monitor.</summary>
    public static void SetText(string text)
    {
        IgnoreNextChange();
        Clipboard.SetText(text);
    }

    public void Initialize()
    {
        if (_window is not null || !OperatingSystem.IsWindows())
        {
            return;
        }

        _window = new HwndSource(new HwndSourceParameters("NovaGetClipboardListener")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: a message-only window
        });
        _window.AddHook(WndProc);
        if (!AddClipboardFormatListener(_window.Handle))
        {
            logger.LogWarning("Clipboard monitoring is unavailable (error {Error})", Marshal.GetLastPInvokeError());
        }
    }

    public void Dispose()
    {
        if (_window is null)
        {
            return;
        }

        RemoveClipboardFormatListener(_window.Handle);
        _window.RemoveHook(WndProc);
        _window.Dispose();
        _window = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            OnClipboardChanged();
        }

        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        if (!settings.Current.General.MonitorClipboard || Environment.TickCount64 < Interlocked.Read(ref s_ignoreUntil))
        {
            return;
        }

        var text = ReadText();
        if (text is null || text.Length > 8192 || LinkExtractor.SingleUrl(text) is not { } url)
        {
            return;
        }

        if (!new CaptureRules(settings.Current.FileTypes).ShouldCapture(url))
        {
            return;
        }

        // The same address copied twice in a row (some apps write the clipboard twice) opens one dialog.
        var now = Environment.TickCount64;
        if (url.AbsoluteUri == _lastUrl && now - _lastAt < 3000)
        {
            return;
        }

        _lastUrl = url.AbsoluteUri;
        _lastAt = now;
        controller.Value.ShowAddUrl(url.AbsoluteUri);
    }

    /// <summary>Another program may hold the clipboard open for a moment: retry briefly.</summary>
    private static string? ReadText()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
        }

        return null;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(IntPtr hwnd);
}
