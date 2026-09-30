using System.Windows;
using NovaGet.Core.Settings;

namespace NovaGet.App.Services;

/// <summary>Restores and captures window bounds, keeping windows on a visible screen.</summary>
internal static class WindowPlacementHelper
{
    public static void Apply(Window window, WindowPlacement placement)
    {
        if (placement.Width >= window.MinWidth && placement.Height >= window.MinHeight)
        {
            window.Width = placement.Width;
            window.Height = placement.Height;
        }

        if (placement.Left is { } left && placement.Top is { } top && IsOnScreen(left, top, window.Width))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (placement.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    public static void Capture(Window window, WindowPlacement placement)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.Left))
        {
            return;
        }

        placement.Left = bounds.Left;
        placement.Top = bounds.Top;
        placement.Width = bounds.Width;
        placement.Height = bounds.Height;
        placement.Maximized = window.WindowState == WindowState.Maximized;
    }

    private static bool IsOnScreen(double left, double top, double width)
    {
        // At least the title bar's left part must land on the virtual desktop.
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        return screen.IntersectsWith(new Rect(left, top, Math.Min(width, 200), 30));
    }
}
