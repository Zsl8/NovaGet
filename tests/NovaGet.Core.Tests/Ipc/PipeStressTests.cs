using NovaGet.Core.Ipc;
using Xunit.Abstractions;

namespace NovaGet.Core.Tests.Ipc;

/// <summary>Opt-in (NOVAGET_STRESS=1): 200 rounds of 32 concurrent clients; guards the listener-handoff race.</summary>
public sealed class PipeStressTests(ITestOutputHelper output)
{
    private sealed class Echo : IIpcRequestHandler
    {
        public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(IpcResponse.Success());
    }

    [Fact]
    public async Task Stress()
    {
        if (Environment.GetEnvironmentVariable("NOVAGET_STRESS") != "1")
        {
            return;
        }

        for (var round = 0; round < 200; round++)
        {
            var name = "NovaGet.Stress." + Guid.NewGuid().ToString("N")[..12];
            await using var server = new PipeServer(name, new Echo());
            server.Start();
            var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
            {
                try
                {
                    return (await PipeClient.SendOnceAsync(name, new IpcRequest { Type = "x" }, TimeSpan.FromSeconds(10)))?.Ok == true ? null : "not ok";
                }
                catch (Exception ex)
                {
                    return ex.GetType().FullName + ": " + ex.Message + " <- " + ex.InnerException?.GetType().FullName;
                }
            }));
            var results = await Task.WhenAll(tasks);
            foreach (var error in results.Where(r => r is not null).Distinct())
            {
                output.WriteLine($"round {round}: {error}");
                Assert.Fail(error);
            }
        }
    }
}
