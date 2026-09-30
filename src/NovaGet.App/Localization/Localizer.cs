using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.Json;

namespace NovaGet.App.Localization;

/// <summary>
/// UI strings. English and Arabic ship as .resx; any other language can be added as a JSON pack
/// (<c>lang\&lt;culture&gt;.json</c>, a flat object of key → text) that overrides the resx entries.
/// The language is chosen at startup; changing it applies after a restart.
/// </summary>
public static class Localizer
{
    private static readonly ResourceManager s_resources = new("NovaGet.App.Localization.Strings", typeof(Localizer).Assembly);
    private static Dictionary<string, string> s_pack = [];

    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    public static bool IsRightToLeft => Culture.TextInfo.IsRightToLeft;

    /// <summary>Selects the UI language and loads its JSON pack if one exists in <paramref name="languageFolder"/>.</summary>
    public static void Initialize(string language, string? languageFolder)
    {
        try
        {
            Culture = string.IsNullOrWhiteSpace(language) ? CultureInfo.CurrentUICulture : CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            Culture = CultureInfo.GetCultureInfo("en");
        }

        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        s_pack = LoadPack(languageFolder, Culture);
    }

    public static string Get(string key)
    {
        if (s_pack.TryGetValue(key, out var packed))
        {
            return packed;
        }

        return s_resources.GetString(key, Culture) ?? key;
    }

    public static string Format(string key, params object?[] args) => string.Format(Culture, Get(key), args);

    /// <summary>Text without the access-key underscore (for tooltips and plain labels).</summary>
    public static string Plain(string key) => Get(key).Replace("_", string.Empty, StringComparison.Ordinal);

    /// <summary>Languages offered in View → Language: the built-in ones plus any JSON packs found.</summary>
    public static IReadOnlyList<CultureInfo> AvailableLanguages(string? languageFolder)
    {
        var cultures = new List<CultureInfo> { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("ar") };
        if (languageFolder is not null && Directory.Exists(languageFolder))
        {
            foreach (var file in Directory.EnumerateFiles(languageFolder, "*.json"))
            {
                try
                {
                    var culture = CultureInfo.GetCultureInfo(Path.GetFileNameWithoutExtension(file));
                    if (!cultures.Exists(c => c.Name == culture.Name))
                    {
                        cultures.Add(culture);
                    }
                }
                catch (CultureNotFoundException)
                {
                }
            }
        }

        return cultures;
    }

    private static Dictionary<string, string> LoadPack(string? folder, CultureInfo culture)
    {
        if (folder is null)
        {
            return [];
        }

        foreach (var name in new[] { culture.Name, culture.TwoLetterISOLanguageName })
        {
            var file = Path.Combine(folder, name + ".json");
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Serilog.Log.Warning(ex, "Ignoring invalid language pack {File}", file);
            }
        }

        return [];
    }
}
