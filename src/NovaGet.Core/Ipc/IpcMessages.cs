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

/// <summary>
/// JSON for the pipe: camelCase names, nulls left out. The request and response envelopes use source-generated
/// serializers, so the trimmed native host needs no reflection; payloads built by the app use <see cref="Options"/>.
/// </summary>
public static class IpcJson
{
    /// <summary>Reflection-based options for payload objects (the app only).</summary>
    public static JsonSerializerOptions Options => Reflection.Options;

    public static byte[] Serialize(IpcRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, IpcJsonContext.Default.IpcRequest);

    public static byte[] Serialize(IpcResponse response) => JsonSerializer.SerializeToUtf8Bytes(response, IpcJsonContext.Default.IpcResponse);

    public static IpcRequest? DeserializeRequest(byte[] utf8) => JsonSerializer.Deserialize(utf8, IpcJsonContext.Default.IpcRequest);

    public static IpcResponse? DeserializeResponse(byte[] utf8) => JsonSerializer.Deserialize(utf8, IpcJsonContext.Default.IpcResponse);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    private static class Reflection
    {
        internal static readonly JsonSerializerOptions Options = Create();

        private static JsonSerializerOptions Create()
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
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
internal sealed partial class IpcJsonContext : JsonSerializerContext
{
}
