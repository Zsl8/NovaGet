using System.Windows;
using NovaGet.App.Localization;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Asks for one line of text; <c>validate</c> returns an error message or null.</summary>
public partial class InputDialog : DialogWindow
{
    private readonly Func<string, string?>? _validate;

    public InputDialog(string title, string prompt, string value, Func<string, string?>? validate = null)
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Content = prompt;
        ValueBox.Text = value;
        ValueBox.SelectAll();
        _validate = validate;
    }

    public string Value => ValueBox.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var error = Value.Length == 0 ? Localizer.Get("Error_NameRequired") : _validate?.Invoke(Value);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            ValueBox.Focus();
            return;
        }

        Accept();
    }
}
