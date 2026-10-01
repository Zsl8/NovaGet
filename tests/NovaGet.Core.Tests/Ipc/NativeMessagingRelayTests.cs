using System.Text;
using System.Text.Json;
using NovaGet.Core.Ipc;

namespace NovaGet.Core.Tests.Ipc;

public sealed class NativeMessagingRelayTests
{
    private sealed class RecordingHandler : IIpcRequestHandler
    {
        public List<string> Types { get; } = [];

        public List<string?> Browsers { get; } = [];

        public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
        {
            Types.Add(request.Type);
            Browsers.Add(request.Args?.FirstOrDefault());
            var kind = request.Payload?.GetProperty("type").GetString();
            return Task.FromResult(IpcResponse.Success(IpcJson.ToElement(new { reply = kind })));
        }
    }

    private static async Task<MemoryStream> FramesAsync(params string[] messages)
    {
        var stream = new MemoryStream();
        foreach (var m in messages)
        {
            await MessageFraming.WriteAsync(stream, Encoding.UTF8.GetBytes(m));
        }

        stream.Position = 0;
        return stream;
    }

    private static async Task<List<IpcResponse>> ReadAllAsync(MemoryStream output)
    {
        output.Position = 0;
        var list = new List<IpcResponse>();
        while (await MessageFraming.ReadAsync(output) is { } body)
        {
            list.Add(JsonSerializer.Deserialize<IpcResponse>(body, IpcJson.Options)!);
        }

        return list;
    }

    [Fact]
    public async Task Relays_each_browser_message_to_the_app_as_native_requests()
    {
        var pipe = "NovaGet.Test." + Guid.NewGuid().ToString("N")[..12];
        var handler = new RecordingHandler();
        await using var server = new PipeServer(pipe, handler);
        server.Start();
        var connects = 0;
        var relay = new NativeMessagingRelay(async ct =>
        {
            connects++;
            return await PipeClient.TryConnectWithRetryAsync(pipe, TimeSpan.FromSeconds(5), ct);
        });

        using var input = await FramesAsync("""{"type":"ping"}""", "not json", """{"type":"download","url":"https://x/y.zip"}""");
        using var output = new MemoryStream();
        await relay.RunAsync(input, output);

        var responses = await ReadAllAsync(output);
        Assert.Equal(3, responses.Count);
        Assert.Equal("ping", responses[0].Payload!.Value.GetProperty("reply").GetString());
        Assert.False(responses[1].Ok);
        Assert.Equal("download", responses[2].Payload!.Value.GetProperty("reply").GetString());
        Assert.Equal([IpcRequestTypes.Native, IpcRequestTypes.Native], handler.Types);
        Assert.Equal(1, connects);
    }

    [Fact]
    public async Task The_calling_browser_goes_with_every_message()
    {
        var pipe = "NovaGet.Test." + Guid.NewGuid().ToString("N")[..12];
        var handler = new RecordingHandler();
        await using var server = new PipeServer(pipe, handler);
        server.Start();
        var relay = new NativeMessagingRelay(ct => PipeClient.TryConnectWithRetryAsync(pipe, TimeSpan.FromSeconds(5), ct), browser: "edge");

        using var input = await FramesAsync("""{"type":"hello"}""", """{"type":"ping"}""");
        using var output = new MemoryStream();
        await relay.RunAsync(input, output);

        Assert.Equal(["edge", "edge"], handler.Browsers);
    }

    [Fact]
    public async Task Reports_when_the_app_cannot_be_reached()
    {
        var relay = new NativeMessagingRelay(_ => Task.FromResult<PipeClient?>(null));

        using var input = await FramesAsync("""{"type":"ping"}""");
        using var output = new MemoryStream();
        await relay.RunAsync(input, output);

        var response = Assert.Single(await ReadAllAsync(output));
        Assert.False(response.Ok);
        Assert.Equal("NovaGet is not running.", response.Error);
    }
}
