using System.Globalization;
using System.Windows;
using NovaGet.App.Localization;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Downloads → Speed Limiter → Settings.</summary>
public partial class SpeedLimiterDialog : DialogWindow
{
    public const int Minimum = 1;
    public const int Maximum = 10_000_000;

    public SpeedLimiterDialog(int maxKBps, bool queueOnly)
    {
        InitializeComponent();
        RateBox.Text = maxKBps.ToString(CultureInfo.CurrentCulture);
        RateBox.SelectAll();
        QueueOnlyBox.IsChecked = queueOnly;
    }

    public int MaxKBps { get; private set; }

    public bool QueueOnly => QueueOnlyBox.IsChecked == true;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RateBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value < Minimum || value > Maximum)
        {
            ErrorText.Text = Localizer.Format("Error_InvalidNumber", Minimum, Maximum);
            ErrorText.Visibility = Visibility.Visible;
            RateBox.Focus();
            return;
        }

        MaxKBps = value;
        Accept();
    }
}
