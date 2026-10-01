using System.Windows;
using System.Windows.Input;
using NovaGet.App.Localization;

namespace NovaGet.App.Views;

/// <summary>Common behavior for NovaGet dialogs: centered on the owner, no taskbar button, RTL-aware, Esc cancels.</summary>
public class DialogWindow : Window
{
    public DialogWindow()
    {
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FlowDirection = Localizer.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, SystemColors.ControlBrushKey);
        InputBindings.Add(new KeyBinding(new CloseCommand(this), Key.Escape, ModifierKeys.None));
    }

    /// <summary>The title bar gets its light/dark color before the window first shows.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.ThemeService.ApplyTitleBar(this);
    }

    /// <summary>Closes with <c>DialogResult = true</c> (use from OK buttons).</summary>
    protected void Accept()
    {
        DialogResult = true;
    }

    private sealed class CloseCommand(Window window) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            try
            {
                window.DialogResult = false;
            }
            catch (InvalidOperationException)
            {
                window.Close(); // not shown modally
            }
        }
    }
}
