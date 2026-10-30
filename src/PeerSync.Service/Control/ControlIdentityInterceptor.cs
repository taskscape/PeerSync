using System.ComponentModel;
using System.Security;
using Grpc.Core;
using Grpc.Core.Interceptors;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Service.Control;

/// <summary>Authenticates the impersonated pipe caller before dispatching any control RPC,
/// including methods added to the protobuf contract in later releases.</summary>
internal sealed class ControlIdentityInterceptor(ControlCallerPolicy callers, DiagnosticJournal? diagnostics = null) : Interceptor
{
    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request,
        ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        RequireCaller(context);
        return continuation(request, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request,
        IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        RequireCaller(context);
        return continuation(request, responseStream, context);
    }

    private void RequireCaller(ServerCallContext context)
    {
        Guid session = Guid.NewGuid();
        try { callers.RequireCaller(context); }
        catch (RpcException error)
        {
            diagnostics?.TryWrite(new("control.caller_rejected", "control")
                { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.StatusCode.ToString(),
                    Exception = error, HResult = error.HResult });
            throw;
        }
        catch (Exception error) when (error is SecurityException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            diagnostics?.TryWrite(new("control.caller_identification_failed", "control")
                { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult });
            throw new RpcException(new(StatusCode.PermissionDenied, "Control caller cannot be identified."));
        }
    }
}
