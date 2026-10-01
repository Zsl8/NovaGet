using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NovaGet.App.Services;

/// <summary>NovaGet's own icons (assets/icons/png), loaded once and frozen.</summary>
public static class AppImages
{
    private static readonly ConcurrentDictionary<string, ImageSource> s_cache = new();

    /// <summary>Icon <paramref name="name"/> rendered at <paramref name="pixels"/> (16, 20, 24, 32, 48, 64 or 96).</summary>
    public static ImageSource Get(string name, int pixels) =>
        s_cache.GetOrAdd($"{name}-{pixels}", static key =>
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/NovaGet;component/Assets/Icons/{key}.png", UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        });
}

/// <summary>Toolbar skins (View → Toolbar → Toolbar theme, Options → General): the icon set of the toolbar buttons.</summary>
public static class ToolbarSkins
{
    public const string Default = "Default";

    /// <summary>The same artwork without color (assets/icons/png/*-mono-*.png).</summary>
    public const string Monochrome = "Monochrome";

    public static IReadOnlyList<string> All { get; } = [Default, Monochrome];

    /// <summary>A known skin; anything else (an older or hand-edited settings file) is the default one.</summary>
    public static string Normalize(string? skin) =>
        All.FirstOrDefault(s => string.Equals(s, skin, StringComparison.OrdinalIgnoreCase)) ?? Default;

    public static string IconName(string icon, string skin) => Normalize(skin) == Monochrome ? icon + "-mono" : icon;

    public static string Title(string skin) => Normalize(skin) == Monochrome
        ? Localization.Localizer.Get("Theme_Monochrome")
        : Localization.Localizer.Get("Options_SkinDefault");
}
