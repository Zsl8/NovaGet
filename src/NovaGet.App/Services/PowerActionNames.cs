using System.Globalization;
using System.Windows.Data;
using NovaGet.App.Localization;
using NovaGet.Core.Models;

namespace NovaGet.App.Services;

/// <summary>Display names for power actions (Shut down, Sleep, Hibernate, Log off).</summary>
public static class PowerActionNames
{
    public static IValueConverter Converter { get; } = new NameConverter();

    public static string Name(PowerAction action) => action switch
    {
        PowerAction.ShutDown => Localizer.Get("Power_ShutDown"),
        PowerAction.Sleep => Localizer.Get("Power_Sleep"),
        PowerAction.Hibernate => Localizer.Get("Power_Hibernate"),
        PowerAction.LogOff => Localizer.Get("Power_LogOff"),
        _ => string.Empty,
    };

    public static string Countdown(PowerAction action, int seconds) => action switch
    {
        PowerAction.Sleep => Localizer.Format("Power_CountdownSleep", seconds),
        PowerAction.Hibernate => Localizer.Format("Power_CountdownHibernate", seconds),
        PowerAction.LogOff => Localizer.Format("Power_CountdownLogOff", seconds),
        _ => Localizer.Format("Power_CountdownShutDown", seconds),
    };

    private sealed class NameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is PowerAction action ? Name(action) : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
