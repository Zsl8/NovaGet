using System.Globalization;

namespace NovaGet.Core.Formatting;

/// <summary>Sizes, rates and durations in the compact style of classic download managers ("4.7 GB", "3 min 5 sec").</summary>
public static class DisplayFormat
{
    private static readonly string[] s_units = ["KB", "MB", "GB", "TB", "PB"];

    /// <summary><c>512 Bytes</c>, <c>85 MB</c>, <c>4.71 GB</c>; empty for an unknown size (&lt; 0).</summary>
    public static string Size(long bytes, int decimals = 2, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 0)
        {
            return string.Empty;
        }

        if (bytes < 1024)
        {
            return string.Create(culture, $"{bytes} Bytes");
        }

        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < s_units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(Format(decimals), culture) + " " + s_units[unit];
    }

    /// <summary><c>12.4 MB/sec</c>; empty when not transferring.</summary>
    public static string Rate(double bytesPerSecond, int decimals = 1, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytesPerSecond <= 0 || double.IsNaN(bytesPerSecond))
        {
            return string.Empty;
        }

        if (bytesPerSecond < 1024)
        {
            return bytesPerSecond.ToString("0", culture) + " Bytes/sec";
        }

        var value = bytesPerSecond / 1024;
        var unit = 0;
        while (value >= 1024 && unit < s_units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(Format(decimals), culture) + " " + s_units[unit] + "/sec";
    }

    /// <summary>
    /// <c>45 sec</c>, <c>4 min 3 sec</c>, <c>1 hour 5 min</c>, <c>2 days 3 hours</c>. With <paramref name="compact"/>
    /// only the largest unit is kept when it is minutes or more ("3 min"), as in the download list.
    /// </summary>
    public static string Duration(TimeSpan? duration, bool compact = false)
    {
        if (duration is not { } d || d < TimeSpan.Zero)
        {
            return string.Empty;
        }

        var totalSeconds = (long)Math.Ceiling(d.TotalSeconds);
        var days = totalSeconds / 86_400;
        var hours = totalSeconds % 86_400 / 3_600;
        var minutes = totalSeconds % 3_600 / 60;
        var seconds = totalSeconds % 60;

        if (days > 0)
        {
            return compact || hours == 0 ? Plural(days, "day") : $"{Plural(days, "day")} {Plural(hours, "hour")}";
        }

        if (hours > 0)
        {
            return compact || minutes == 0 ? Plural(hours, "hour") : $"{Plural(hours, "hour")} {minutes} min";
        }

        if (minutes > 0)
        {
            return compact || seconds == 0 ? $"{minutes} min" : $"{minutes} min {seconds} sec";
        }

        return $"{seconds} sec";
    }

    /// <summary>Progress percentage for the Status column: <c>37.5%</c>.</summary>
    public static string Percent(long downloaded, long size, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (size <= 0)
        {
            return string.Empty;
        }

        var percent = Math.Min(100.0, downloaded * 100.0 / size);
        return percent.ToString("0.##", culture) + "%";
    }

    /// <summary>Last Try Date column: <c>Oct 01 14:02</c> in local time.</summary>
    public static string ShortDate(DateTime? utc, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return utc is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime().ToString("MMM dd HH:mm", culture) : string.Empty;
    }

    private static string Plural(long value, string unit) => value == 1 ? $"1 {unit}" : $"{value} {unit}s";

    private static string Format(int decimals) => decimals <= 0 ? "0" : "0." + new string('#', decimals);
}
