using Microsoft.Extensions.Logging;
using NovaGet.App.Services;
using NovaGet.Core;
using NovaGet.Core.CommandLine;
using NovaGet.Core.Ipc;

namespace NovaGet.App.Hosting;

/// <summary>Dispatches requests from second instances and the native host to the app.</summary>
internal sealed class IpcRequestRouter(IAppController controller, BrowserIntegrationService browser, ILogger<IpcRequestRouter> logger) : IIpcRequestHandler
{
    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case IpcRequestTypes.Ping:
                return Task.FromResult(IpcResponse.Success(IpcJson.ToElement(new
                {
                    product = AppInfo.ProductName,
                    version = AppInfo.InformationalVersion,
                    pid = Environment.ProcessId,
                })));

            case IpcRequestTypes.Activate:
                controller.ShowMainWindow();
                return Task.FromResult(IpcResponse.Success());

            case IpcRequestTypes.CommandLine:
                var options = CommandLineParser.Parse(request.Args ?? []);
                logger.LogInformation("Forwarded command line: {Args}", request.Args);
                controller.HandleCommandLine(options);
                return Task.FromResult(options.Errors.Count == 0
                    ? IpcResponse.Success()
                    : IpcResponse.Failure(string.Join("; ", options.Errors)));

            case IpcRequestTypes.Exit:
                controller.RequestExit();
                return Task.FromResult(IpcResponse.Success());

            case IpcRequestTypes.Native:
                return Task.FromResult(browser.Handle(request));

            default:
                logger.LogWarning("Unknown IPC request type {Type}", request.Type);
                return Task.FromResult(IpcResponse.Failure($"Unknown request type '{request.Type}'."));
        }
    }
}
