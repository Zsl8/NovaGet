using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NovaGet.Core.Settings;

/// <summary>Shared JSON options for settings and settings export/import.</summary>
public static class SettingsJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        options.MakeReadOnly();
        return options;
    }

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static AppSettings Deserialize(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, Options) ?? throw new JsonException("Settings file is empty.");

    /// <summary>Deep copy through JSON (settings are small; this keeps the code obvious).</summary>
    public static AppSettings Clone(AppSettings settings) => Deserialize(Serialize(settings));
}
