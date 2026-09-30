using System.Text.Json;
using NovaGet.Core.Ipc;

namespace NovaGet.Core.Tests.Ipc;

public sealed class PipeServerTests
{
    private sealed class EchoHandler : IIpcRequestHandler
    {
        public List<IpcRequest> Received { get; } = [];

        public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
        {
            lock (Received)
            {
                Received.Add(request);
            }

            return request.Type switch
            {
                "boom" => throw new InvalidOperationException("handler failure"),
                IpcRequestTypes.Ping => Task.FromResult(IpcResponse.Success(IpcJson.ToElement(new { version = "test" }))),
                _ => Task.FromResult(IpcResponse.Success(IpcJson.ToElement(new { echo = request.Args }))),
            };
        }
    }

    private static string UniquePipeName() => "NovaGet.Test." + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task Request_response_round_trip()
    {
        var name = UniquePipeName();
        var handler = new EchoHandler();
        await using var server = new PipeServer(name, handler);
        server.Start();

        var response = await PipeClient.SendOnceAsync(name, new IpcRequest { Type = IpcRequestTypes.Ping }, TimeSpan.FromSeconds(5));

        Assert.NotNull(response);
        Assert.True(response.Ok);
        Assert.Equal("test", response.Payload!.Value.GetProperty("version").GetString());
    }

    [Fact]
    public async Task One_connection_carries_many_requests()
    {
        var name = UniquePipeName();
        var handler = new EchoHandler();
        await using var server = new PipeServer(name, handler);
        server.Start();

        await using var client = await PipeClient.ConnectAsync(name, TimeSpan.FromSeconds(5));
        for (var i = 0; i < 5; i++)
        {
            var response = await client.SendAsync(new IpcRequest { Type = IpcRequestTypes.CommandLine, Args = [$"/d", $"https://h/{i}"] });
            Assert.True(response.Ok);
            Assert.Equal($"https://h/{i}", response.Payload!.Value.GetProperty("echo")[1].GetString());
        }

        Assert.Equal(5, handler.Received.Count);
    }

    [Fact]
    public async Task Concurrent_clients_are_served()
    {
        var name = UniquePipeName();
        var handler = new EchoHandler();
        await using var server = new PipeServer(name, handler);
        server.Start();

        var tasks = Enumerable.Range(0, 8).Select(i =>
            PipeClient.SendOnceAsync(name, new IpcRequest { Type = "x", Args = [i.ToString(System.Globalization.CultureInfo.InvariantCulture)] }, TimeSpan.FromSeconds(10)));
        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.True(r!.Ok));
        Assert.Equal(8, handler.Received.Count);
    }

    [Fact]
    public async Task Handler_exceptions_become_error_responses()
    {
        var name = UniquePipeName();
        await using var server = new PipeServer(name, new EchoHandler());
        server.Start();

        await using var client = await PipeClient.ConnectAsync(name, TimeSpan.FromSeconds(5));
        var failed = await client.SendAsync(new IpcRequest { Type = "boom" });
        var ok = await client.SendAsync(new IpcRequest { Type = IpcRequestTypes.Ping });

        Assert.False(failed.Ok);
        Assert.Equal("Internal error.", failed.Error);
        Assert.True(ok.Ok);
    }

    [Fact]
    public async Task Malformed_json_gets_error_response()
    {
        var name = UniquePipeName();
        await using var server = new PipeServer(name, new EchoHandler());
        server.Start();

        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await MessageFraming.WriteAsync(pipe, "not json"u8.ToArray());
        var body = await MessageFraming.ReadAsync(pipe);

        var response = JsonSerializer.Deserialize<IpcResponse>(body!, IpcJson.Options)!;
        Assert.False(response.Ok);
    }

    [Fact]
    public async Task No_server_returns_null_after_timeout()
    {
        var response = await PipeClient.SendOnceAsync(UniquePipeName(), new IpcRequest { Type = IpcRequestTypes.Ping }, TimeSpan.FromMilliseconds(300));

        Assert.Null(response);
    }
}
