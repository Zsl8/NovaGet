using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NovaGet.Core.Ipc;

/// <summary>Request types understood by the running instance.</summary>
public static class IpcRequestTypes
{
    /// <summary>Liveness check; replies with the app version.</summary>
    public const string Ping = "ping";

    /// <summary>Bring the main window to the front.</summary>
    public const string Activate = "activate";

    /// <summary>Command line forwarded from a second instance; <see cref="IpcRequest.Args"/> holds the arguments.</summary>
    public const string CommandLine = "commandLine";

    /// <summary>Exit the app (used by the installer/uninstaller).</summary>
    public const string Exit = "exit";

    /// <summary>A message from the browser extension relayed by the native host; <see cref="IpcRequest.Payload"/> holds it verbatim.</summary>
    public const string Native = "native";
}

public sealed class IpcRequest
{
    public string Type { get; set; } = string.Empty;

    public List<string>? Args { get; set; }

    public JsonElement? Payload { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public JsonElement? Payload { get; set; }

    public static IpcResponse Success(JsonElement? payload = null) => new() { Ok = true, Payload = payload };

    public static IpcResponse Failure(string error) => new() { Ok = false, Error = error };
}

public static class IpcJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        options.MakeReadOnly();
        return options;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(byte[] utf8) => JsonSerializer.Deserialize<T>(utf8, Options);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
