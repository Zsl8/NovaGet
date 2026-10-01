using CommunityToolkit.WinUI.Notifications;
using NovaGet.App.Localization;

namespace NovaGet.App.Services;

/// <summary>
/// Windows toast notifications (section 16) with Open / Open folder buttons for finished downloads. Toasts carry only a
/// download id; the file is opened only when the user clicks and the download is complete. When toasts aren't
/// available (older or restricted systems) the tray balloon is used instead.
/// </summary>
internal static class Toasts
{
    public const string OpenAction = "open";
    public const string FolderAction = "folder";

    private static volatile bool s_unavailable;

    /// <summary>A toast (or one of its buttons) was clicked: the action and the download id. Raised on a background thread.</summary>
    public static event Action<string, long>? Activated;

    public static void Initialize()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var arguments = ToastArguments.Parse(e.Argument);
                if (arguments.TryGetValue("action", out var action) && arguments.TryGetValue("id", out var id)
                    && long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var downloadId))
                {
                    Activated?.Invoke(action, downloadId);
                }
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            s_unavailable = true;
        }
    }

    /// <summary>Shows a toast; false when toasts can't be shown (the caller falls back to a balloon).</summary>
    public static bool TryShow(string title, string message, long? downloadId = null)
    {
        if (s_unavailable)
        {
            return false;
        }

        try
        {
            var builder = new ToastContentBuilder().AddText(title).AddText(message);
            if (downloadId is { } id)
            {
                // Clicking the toast itself opens the folder; opening the file takes the explicit button.
                builder.AddArgument("action", FolderAction).AddArgument("id", id)
                    .AddButton(new ToastButton().SetContent(Localizer.Plain("Notify_Open")).AddArgument("action", OpenAction).AddArgument("id", id))
                    .AddButton(new ToastButton().SetContent(Localizer.Plain("Notify_OpenFolder")).AddArgument("action", FolderAction).AddArgument("id", id));
            }

            builder.Show();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            s_unavailable = true;
            return false;
        }
    }

    /// <summary>Removes the toast registration (the uninstaller's <c>/cleanup</c>).</summary>
    public static void Uninstall()
    {
        try
        {
            ToastNotificationManagerCompat.Uninstall();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing was registered.
        }
    }
}
