using System.Text.Json;
using System.Text.Json.Nodes;
using NovaGet.Core.Models;

namespace NovaGet.Core.Settings;

/// <summary>What Options → Advanced → Export settings writes and Import settings reads.</summary>
public sealed class SettingsPackage
{
    public required AppSettings Settings { get; init; }

    public IReadOnlyList<Category> Categories { get; init; } = [];

    public IReadOnlyList<ServerException> ServerExceptions { get; init; } = [];
}

/// <summary>
/// Settings export/import. The file is JSON: <c>{ "format": "novaget-settings", "version": 1, "settings": …,
/// "categories": […], "serverExceptions": […] }</c>. Passwords are never exported: they are encrypted for this
/// Windows account only and would be useless (and a leak) anywhere else. A bare settings.json is accepted too.
/// </summary>
public static class SettingsTransfer
{
    public const string Format = "novaget-settings";
    public const int Version = 1;
    public const int MaxFileBytes = 4 * 1024 * 1024;

    public static string Export(SettingsPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var settings = SettingsJson.Clone(package.Settings);
        settings.Proxy.Http.ProtectedPassword = string.Empty;
        settings.Proxy.Https.ProtectedPassword = string.Empty;
        settings.Proxy.Ftp.ProtectedPassword = string.Empty;
        settings.Proxy.Socks.ProtectedPassword = string.Empty;
        settings.DialUp.ProtectedPassword = string.Empty;

        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["settings"] = JsonSerializer.SerializeToNode(settings, SettingsJson.Options),
            ["categories"] = new JsonArray([.. package.Categories.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name,
                ["builtIn"] = c.IsBuiltIn,
                ["extensions"] = c.Extensions,
                ["folder"] = c.DefaultSaveDir,
            })]),
            ["serverExceptions"] = new JsonArray([.. package.ServerExceptions.Select(e => (JsonNode)new JsonObject
            {
                ["host"] = e.Host,
                ["maxConnections"] = e.MaxConnections,
            })]),
        };
        return root.ToJsonString(SettingsJson.Options);
    }

    /// <summary>Parses an exported file. Throws <see cref="FormatException"/> with a readable message when it isn't one.</summary>
    public static SettingsPackage Import(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxFileBytes)
        {
            throw new FormatException("The file is too large to be a settings file.");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new FormatException("The file is not valid JSON: " + ex.Message, ex);
        }

        if (root is not JsonObject obj)
        {
            throw new FormatException("The file does not contain NovaGet settings.");
        }

        try
        {
            if (obj["format"]?.GetValue<string>() != Format)
            {
                // A plain settings.json copied from another installation.
                if (obj["schemaVersion"] is null && obj["general"] is null)
                {
                    throw new FormatException("The file does not contain NovaGet settings.");
                }

                return new SettingsPackage { Settings = Normalized(SettingsJson.Deserialize(json)) };
            }

            var version = obj["version"]?.GetValue<int>() ?? 0;
            if (version is < 1 or > Version)
            {
                throw new FormatException($"Settings file version {version} is not supported by this version of NovaGet.");
            }

            var settings = obj["settings"]?.Deserialize<AppSettings>(SettingsJson.Options)
                ?? throw new FormatException("The file has no settings section.");

            var categories = new List<Category>();
            foreach (var node in obj["categories"] as JsonArray ?? [])
            {
                var name = node?["name"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrEmpty(name) || name.Length > 100)
                {
                    continue;
                }

                categories.Add(new Category
                {
                    Name = name,
                    IsBuiltIn = node?["builtIn"]?.GetValue<bool>() ?? false,
                    Extensions = Limit(node?["extensions"]?.GetValue<string>(), 4000) ?? string.Empty,
                    DefaultSaveDir = Limit(node?["folder"]?.GetValue<string>(), 1000),
                });
            }

            var exceptions = new List<ServerException>();
            foreach (var node in obj["serverExceptions"] as JsonArray ?? [])
            {
                var host = node?["host"]?.GetValue<string>()?.Trim();
                var max = node?["maxConnections"]?.GetValue<int>() ?? 0;
                if (!string.IsNullOrEmpty(host) && host.Length <= 255 && max is >= 1 and <= 32)
                {
                    exceptions.Add(new ServerException { Host = host, MaxConnections = max });
                }
            }

            return new SettingsPackage { Settings = Normalized(settings), Categories = categories, ServerExceptions = exceptions };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new FormatException("The settings file is damaged: " + ex.Message, ex);
        }
    }

    private static AppSettings Normalized(AppSettings settings)
    {
        SettingsNormalizer.Normalize(settings);
        return settings;
    }

    private static string? Limit(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value.Trim() : null;
}
