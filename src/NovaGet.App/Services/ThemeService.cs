using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using NovaGet.Core.Settings;

namespace NovaGet.App.Services;

/// <summary>
/// Section 17 theming. The classic light look is the default; the dark color theme merges Themes/Dark.xaml over it
/// ("Use Windows setting" follows the Windows app mode). Title bars follow the Windows app mode (or the dark theme)
/// through DWM. High contrast always gets the classic theme, which is drawn from the system colors.
/// </summary>
internal sealed partial class ThemeService(ISettingsService settings) : IDisposable
{
    private static readonly Uri s_darkTheme = new("pack://application:,,,/NovaGet;component/Themes/Dark.xaml", UriKind.Absolute);
    private static bool s_classHandlerRegistered;
    private static bool s_darkTitleBars;

    private ResourceDictionary? _dark;
    private bool _initialized;

    /// <summary>The dark color theme is applied.</summary>
    public bool IsDark => _dark is not null;

    public void Initialize()
    {
        Apply();
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        settings.Changed += OnSettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        if (!s_classHandlerRegistered)
        {
            // Every window, including ones created by libraries, gets its title bar colored when it loads.
            s_classHandlerRegistered = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));
        }
    }

    /// <summary>Whether the content uses the dark theme.</summary>
    public static bool UseDarkTheme(AppTheme theme, bool windowsDark, bool highContrast) =>
        !highContrast && (theme == AppTheme.Dark || (theme == AppTheme.System && windowsDark));

    /// <summary>Whether title bars are dark: the Windows app mode, or the dark theme.</summary>
    public static bool UseDarkTitleBars(AppTheme theme, bool windowsDark, bool highContrast) =>
        !highContrast && (windowsDark || UseDarkTheme(theme, windowsDark, highContrast));

    /// <summary>Windows "Choose your app mode: Dark" (Settings → Personalization → Colors).</summary>
    public static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>Applies the theme from the settings and the Windows mode to the app and every open window.</summary>
    public void Apply()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(Apply);
            return;
        }

        var theme = settings.Current.Ui.Theme;
        var windowsDark = WindowsUsesDarkApps();
        var highContrast = SystemParameters.HighContrast;
        var dark = UseDarkTheme(theme, windowsDark, highContrast);
        if (dark && _dark is null)
        {
            _dark = new ResourceDictionary { Source = s_darkTheme };
            app.Resources.MergedDictionaries.Add(_dark);
        }
        else if (!dark && _dark is not null)
        {
            app.Resources.MergedDictionaries.Remove(_dark);
            _dark = null;
        }

        s_darkTitleBars = UseDarkTitleBars(theme, windowsDark, highContrast);
        foreach (Window window in app.Windows)
        {
            ApplyTitleBar(window);
        }
    }

    /// <summary>Colors the window's title bar (Windows 10 1809 and later; ignored elsewhere).</summary>
    public static void ApplyTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var value = s_darkTitleBars ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE is 20 since Windows 10 20H1 and was 19 before.
        if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
        }
    }

    public void Dispose()
    {
        if (_initialized)
        {
            settings.Changed -= OnSettingsChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Apply();

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Switching the Windows app mode or high contrast arrives as General/Color/Accessibility.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
        {
            Apply();
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
