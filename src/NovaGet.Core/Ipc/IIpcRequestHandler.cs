namespace NovaGet.Core.Ipc;

/// <summary>Handles requests arriving on the app's named pipe.</summary>
public interface IIpcRequestHandler
{
    Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken);
}
