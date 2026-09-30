using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NovaGet.App.Services;

/// <summary>NovaGet's own icons (assets/icons/png), loaded once and frozen.</summary>
public static class AppImages
{
    private static readonly ConcurrentDictionary<string, ImageSource> s_cache = new();

    /// <summary>Icon <paramref name="name"/> rendered at <paramref name="pixels"/> (16, 24, 32, 48, 64 or 96).</summary>
    public static ImageSource Get(string name, int pixels) =>
        s_cache.GetOrAdd($"{name}-{pixels}", static key =>
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/Assets/Icons/{key}.png", UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        });
}
