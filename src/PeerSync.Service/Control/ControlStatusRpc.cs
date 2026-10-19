using System.Security.Principal;
using System.ComponentModel;
using System.Security;
using Grpc.Core;
using Microsoft.AspNetCore.Connections.Features;
using PeerSync.Control.Contracts;
using PeerSync.Core.Diagnostics;
using Wire = PeerSync.Control.Contracts.V1;

namespace PeerSync.Service.Control;

internal sealed class ControlCallerPolicy(IReadOnlyList<SecurityIdentifier> allowed, bool administrators)
{
    private readonly SecurityIdentifier[] allowed = allowed.ToArray();
    internal void RequireCaller(ServerCallContext context)
    {
        bool permitted = false; var pipe = context.GetHttpContext().Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe;
        if (pipe is not null) pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
            if (identity?.User is null) return;
            var principal = new WindowsPrincipal(identity);
            permitted = administrators && principal.IsInRole(WindowsBuiltInRole.Administrator)
                || allowed.Any(sid => sid.Equals(identity.User) || principal.IsInRole(sid));
        });
        if (!permitted) throw new RpcException(new(StatusCode.PermissionDenied, "Control caller is not authorized."));
    }
}
internal sealed class ControlServerLifetime : IDisposable
{
    private readonly CancellationTokenSource source = new();
    internal CancellationToken Token => source.Token;
    internal void Cancel() => source.Cancel();
    public void Dispose() => source.Dispose();
}
internal sealed class ControlSubscriptionBudget
{
    internal const int Maximum = 32;
    private int active;
    internal int Active => Volatile.Read(ref active);
    internal bool TryEnter()
    {
        if (Interlocked.Increment(ref active) <= Maximum) return true;
        Interlocked.Decrement(ref active); return false;
    }
    internal void Exit() => Interlocked.Decrement(ref active);
}

internal sealed class ControlStatusRpc(ControlStatusRuntime runtime, ControlCallerPolicy callers, ControlSubscriptionBudget subscriptions,
    ControlServerLifetime lifetime, DiagnosticJournal? diagnostics = null) : Wire.PeerSyncControl.PeerSyncControlBase
{
    public override Task<Wire.ServiceStatus> GetStatus(Wire.StatusRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session); context.CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(session));
    }
    public override async Task WatchEvents(Wire.StatusRequest request, IServerStreamWriter<Wire.ControlEvent> responseStream, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session);
        if (!subscriptions.TryEnter())
        {
            diagnostics?.TryWrite(new("control.events_limit", "control") { SessionId = session, Level = DiagnosticLevel.Warning, ItemsVisited = subscriptions.Active });
            throw new RpcException(new(StatusCode.ResourceExhausted, "Control event subscription limit reached."));
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.Token);
        try
        {
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                await responseStream.WriteAsync(new() { Status = Read(session) }, stop.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token).ConfigureAwait(false);
            }
        }
        finally { subscriptions.Exit(); diagnostics?.TryWrite(new("control.events_closed", "control") { SessionId = session, ItemsVisited = subscriptions.Active }); }
    }
    private void Authorize(ServerCallContext context, Guid session)
    {
        try { callers.RequireCaller(context); }
        catch (RpcException error)
        {
            diagnostics?.TryWrite(new("control.caller_rejected", "control") { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.StatusCode.ToString() }); throw;
        }
        catch (Exception error) when (error is SecurityException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            diagnostics?.TryWrite(new("control.caller_identification_failed", "control") { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
            throw new RpcException(new(StatusCode.PermissionDenied, "Control caller cannot be identified."));
        }
    }
    private Wire.ServiceStatus Read(Guid session)
    {
        try
        {
            var status = runtime.ReadStatus(); var response = ControlStatusWire.Encode(status);
            diagnostics?.TryWrite(new("control.status_read", "control") { SessionId = session, Level = DiagnosticLevel.Debug, ItemsVisited = status.Clocks.Count });
            return response;
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException or InvalidDataException)
        {
            diagnostics?.TryWrite(new("control.status_unavailable", "control") { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
            throw new RpcException(new(StatusCode.Unavailable, "Control status is unavailable."));
        }
    }
}
