using System.Security.Principal;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security;
using Grpc.Core;
using Microsoft.AspNetCore.Connections.Features;
using PeerSync.Control.Contracts;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Service.Hosting;
using Wire = PeerSync.Control.Contracts.V1;

namespace PeerSync.Service.Control;

internal sealed class ControlCallerPolicy(IReadOnlyList<SecurityIdentifier> allowed, bool administrators)
{
    private readonly SecurityIdentifier[] allowed = allowed.ToArray();
    internal string RequireCaller(ServerCallContext context)
    {
        bool permitted = false; string? actorSid = null; var pipe = context.GetHttpContext().Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe;
        if (pipe is not null) pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
            if (identity?.User is null) return;
            actorSid = identity.User.Value;
            var principal = new WindowsPrincipal(identity);
            permitted = administrators && principal.IsInRole(WindowsBuiltInRole.Administrator)
                || allowed.Any(sid => sid.Equals(identity.User) || principal.IsInRole(sid));
        });
        if (!permitted) throw new RpcException(new(StatusCode.PermissionDenied, "Control caller is not authorized."));
        return actorSid ?? throw new RpcException(new(StatusCode.PermissionDenied, "Control caller cannot be identified."));
    }

    internal static string RequireAdministrator(ServerCallContext context)
    {
        string? actorSid = null; bool administrator = false;
        var pipe = context.GetHttpContext().Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe;
        if (pipe is not null) pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
            actorSid = identity?.User?.Value;
            administrator = identity is not null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        });
        if (!administrator || actorSid is null)
            throw new RpcException(new(StatusCode.PermissionDenied, "An administrator is required."));
        return actorSid;
    }
}
internal sealed class ControlServerLifetime : IDisposable
{
    private readonly CancellationTokenSource source = new();
    private readonly TaskCompletionSource restart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationToken Token => source.Token;
    internal Task RestartRequested => restart.Task;
    internal void RequestRestartAfterResponse()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            restart.TrySetResult();
        });
    }
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

internal sealed partial class ControlStatusRpc(ControlStatusRuntime runtime, ControlCallerPolicy callers, ControlSubscriptionBudget subscriptions,
    ControlServerLifetime lifetime, ControlSettingsRuntime settings, DiagnosticJournal? diagnostics = null) : Wire.PeerSyncControl.PeerSyncControlBase
{
    public override Task<Wire.ServiceStatus> GetStatus(Wire.StatusRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session); context.CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(session));
    }
    public override Task<Wire.ListFoldersResponse> ListFolders(Wire.ListFoldersRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session);
        try
        {
            var folders = runtime.RequireFolderHost().ListFolders();
            diagnostics?.TryWrite(new("control.folders_listed", "control") { SessionId = session, ItemsVisited = folders.Count });
            return Task.FromResult(new Wire.ListFoldersResponse
            {
                Folders = { folders.Select(folder => new Wire.FolderInfo
                {
                    FolderId = Convert.ToHexString(folder.Id), Path = folder.Path,
                    Access = folder.Access.ToString(), Paused = folder.Paused,
                }) },
            });
        }
        catch (Exception error) { throw MapFolderError(error); }
    }
    public override async Task<Wire.SetPredefinedHostsResponse> SetPredefinedHosts(
        Wire.SetPredefinedHostsRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); string actorSid = Authorize(context, session);
        if (request.Hosts.Count > 256 || request.Hosts.Any(host => host.Length > 512))
            throw new RpcException(new(StatusCode.InvalidArgument, "Invalid predefined host list."));
        try
        {
            string[] hosts = await runtime.RequireFolderHost().SetPredefinedHostsAsync(
                request.Folder, request.Hosts, actorSid, context.CancellationToken).ConfigureAwait(false);
            string folderId = Convert.ToHexString(runtime.RequireFolderHost().ResolveFolder(request.Folder).Keys.FolderId());
            diagnostics?.TryWrite(new("control.predefined_hosts_updated", "control")
                { SessionId = session, ItemsChanged = hosts.Length });
            var response = new Wire.SetPredefinedHostsResponse { FolderId = folderId };
            response.Hosts.AddRange(hosts);
            return response;
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }
    public override async Task<Wire.FolderInfo> AddFolder(Wire.AddFolderRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); string actorSid = Authorize(context, session);
        FolderKeys? keys = null;
        try
        {
            keys = FolderKeys.CreateNew();
            var folder = await runtime.RequireFolderHost().AddFolderAsync(keys, request.Path, actorSid, context.CancellationToken).ConfigureAwait(false);
            keys = null; // Ownership transfers to the folder runtime.
            diagnostics?.TryWrite(new("control.folder_added", "control") { SessionId = session, ItemsChanged = 1 });
            return new() { FolderId = Convert.ToHexString(folder.Id), Path = folder.Path, Access = folder.Access.ToString(), Paused = folder.Paused };
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
        finally { keys?.Dispose(); }
    }
    public override async Task<Wire.FolderInfo> JoinFolder(Wire.JoinFolderRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); string actorSid = Authorize(context, session);
        FolderKeys? keys = null;
        try
        {
            keys = FolderKeys.FromText(request.Key);
            var folder = await runtime.RequireFolderHost().AddFolderAsync(keys, request.Path, actorSid, context.CancellationToken).ConfigureAwait(false);
            keys = null; // Ownership transfers to the folder runtime.
            diagnostics?.TryWrite(new("control.folder_joined", "control") { SessionId = session, ItemsChanged = 1 });
            return new() { FolderId = Convert.ToHexString(folder.Id), Path = folder.Path, Access = folder.Access.ToString(), Paused = folder.Paused };
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
        finally { keys?.Dispose(); }
    }
    public override async Task<Wire.GetFolderKeysResponse> GetFolderKeys(Wire.GetFolderKeysRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); string actorSid = Authorize(context, session);
        FolderKeys? keys = null;
        try
        {
            keys = await runtime.RequireFolderHost().ReadFolderKeysAsync(request.Folder, actorSid, context.CancellationToken).ConfigureAwait(false);
            string key = keys.ExportText();
            diagnostics?.TryWrite(new("control.folder_keys_read", "control") { SessionId = session });
            return new() { FolderId = Convert.ToHexString(keys.FolderId()), Access = keys.Kind.ToString(), Key = key };
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
        finally { keys?.Dispose(); }
    }
    public override Task<Wire.ListPeersResponse> ListPeers(Wire.ListPeersRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session);
        try
        {
            var host = runtime.RequireFolderHost(); var folder = host.ResolveFolder(request.Folder);
            byte[] folderId = folder.Keys.FolderId();
            var rows = host.ListPeers(request.Folder);
            diagnostics?.TryWrite(new("control.peers_listed", "control") { SessionId = session, ItemsVisited = rows.Count });
            return Task.FromResult(new Wire.ListPeersResponse
            {
                FolderId = Convert.ToHexString(folderId),
                Peers = { rows.Select(peer =>
                {
                    var info = new Wire.PeerInfo
                    {
                        DeviceId = Convert.ToHexString(peer.DeviceId), Name = peer.Name ?? "",
                        Approval = peer.Approval.ToString(), Pinned = peer.Pinned,
                        Transport = runtime.ReadPeerTransport(peer.DeviceId),
                    };
                    if (peer.LastSeenTicks is { } ticks) info.LastSeenUtcTicks = ticks;
                    return info;
                }) },
            });
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }
    public override async Task DumpIndex(Wire.DumpIndexRequest request, IServerStreamWriter<Wire.IndexEntry> responseStream, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session);
        try
        {
            int count = 0;
            foreach (var row in runtime.RequireFolderHost().ReadIndex(request.Folder))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await responseStream.WriteAsync(new Wire.IndexEntry
                { Path = row.Path, Size = row.Size, ModifiedUtcTicks = row.MtimeTicks, Deleted = row.Deleted }, context.CancellationToken).ConfigureAwait(false);
                count++;
            }
            diagnostics?.TryWrite(new("control.index_dumped", "control") { SessionId = session, ItemsVisited = count });
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
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
                await responseStream.WriteAsync(BuildEvent(session), stop.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token).ConfigureAwait(false);
            }
        }
        finally { subscriptions.Exit(); diagnostics?.TryWrite(new("control.events_closed", "control") { SessionId = session, ItemsVisited = subscriptions.Active }); }
    }
    private string Authorize(ServerCallContext context, Guid session)
    {
        try { return callers.RequireCaller(context); }
        catch (RpcException error)
        {
            diagnostics?.TryWrite(new("control.caller_rejected", "control") { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.StatusCode.ToString(), Exception = error, HResult = error.HResult }); throw;
        }
        catch (Exception error) when (error is SecurityException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            diagnostics?.TryWrite(new("control.caller_identification_failed", "control") { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult });
            throw new RpcException(new(StatusCode.PermissionDenied, "Control caller cannot be identified."));
        }
    }
    private static RpcException MapFolderError(Exception error) => error switch
    {
        RpcException rpc => rpc,
        ControlRuntimeUnavailableException => new(new(StatusCode.Unavailable, "Folder management is unavailable.")),
        InstalledFolderHost.FolderAlreadyRegisteredException => new(new(StatusCode.AlreadyExists, "Folder path is already registered.")),
        KeyNotFoundException => new(new(StatusCode.NotFound, "Folder was not found.")),
        ArgumentException or FormatException or ValidationException => new(new(StatusCode.InvalidArgument, "Invalid folder request.")),
        DirectoryNotFoundException => new(new(StatusCode.NotFound, "The folder path does not exist.")),
        IOException or UnauthorizedAccessException => new(new(StatusCode.FailedPrecondition, "The folder could not be opened.")),
        InvalidOperationException => new(new(StatusCode.FailedPrecondition, "The folder request conflicts with current service state.")),
        OperationCanceledException => new(new(StatusCode.Cancelled, "Folder request was cancelled.")),
        _ => new(new(StatusCode.Internal, "Folder request failed.")),
    };
    private void LogFolderFailure(Guid session, Exception error)
        => diagnostics?.TryWrite(new("control.folder_request_failed", "control")
            { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult });
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
            diagnostics?.TryWrite(new("control.status_unavailable", "control") { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult });
            throw new RpcException(new(StatusCode.Unavailable, "Control status is unavailable."));
        }
    }
}
