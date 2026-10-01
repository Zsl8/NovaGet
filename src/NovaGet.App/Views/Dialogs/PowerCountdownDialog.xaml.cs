using System.Windows;
using System.Windows.Threading;
using NovaGet.App.Services;
using NovaGet.Core.Models;

namespace NovaGet.App.Views.Dialogs;

/// <summary>The cancellable 30-second countdown shown before any power action.</summary>
public partial class PowerCountdownDialog : DialogWindow
{
    public const int Seconds = 30;

    private readonly PowerAction _action;
    private readonly DispatcherTimer _timer;
    private int _remaining = Seconds;

    public PowerCountdownDialog(PowerAction action)
    {
        InitializeComponent();
        _action = action;
        CountdownBar.Maximum = Seconds;
        Update();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, OnTick, Dispatcher);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            Accept();
            return;
        }

        Update();
    }

    private void Update()
    {
        MessageText.Text = PowerActionNames.Countdown(_action, _remaining);
        CountdownBar.Value = Seconds - _remaining;
    }

    private void OnNow(object sender, RoutedEventArgs e) => Accept();
}
