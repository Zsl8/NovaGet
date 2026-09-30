using System.Windows;

namespace NovaGet.App.Services;

/// <summary>Message boxes and common dialogs, owned by the active NovaGet window.</summary>
public interface IDialogService
{
    void Info(string message, string? title = null);

    void Error(string message, string? title = null);

    bool Confirm(string message, string? title = null);

    /// <summary>Yes/No question with an optional check box; returns the answer and the box's state.</summary>
    (bool Yes, bool Checked) ConfirmWithCheck(string message, string checkText, bool checkDefault, string? title = null);

    /// <summary>Shows a window modally over the active window.</summary>
    bool? ShowModal(Window dialog);

    string? PickFolder(string? initialFolder, string? title = null);

    string? PickSaveFile(string? initialPath, string filter, string? title = null);

    string? PickOpenFile(string filter, string? title = null);

    Window? ActiveWindow { get; }
}
