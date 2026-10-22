using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Security.Principal;
using Grpc.Core;
using PeerSync.Core.Settings;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using Wire = PeerSync.Control.Contracts.V1;

namespace PeerSync.Service.Control;

internal sealed partial class ControlStatusRpc
{
    public override async Task<Wire.OperationResult> AuthorizeUser(Wire.AuthorizeUserRequest request,
        ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session);
        string actorSid = ControlCallerPolicy.RequireAdministrator(context);
        string targetSid;
        try { targetSid = new SecurityIdentifier(request.Sid).Value; }
        catch (ArgumentException) { throw new RpcException(new(StatusCode.InvalidArgument, "Invalid user SID.")); }
        try
        {
            await runtime.RequireFolderHost().AuditControlUserAuthorizationAsync(actorSid, targetSid,
                context.CancellationToken).ConfigureAwait(false);
            bool changed = settings.AuthorizeUser(targetSid);
            diagnostics?.TryWrite(new("control.user_authorized", "control")
                { SessionId = session, ItemsChanged = changed ? 1 : 0 });
            // Recycling this host in the installed Windows-service process ended
            // the service on a clean guest. The elevated app helper restarts it
            // after this response, rebuilding the pipe ACL and caller policy.
            return new() { Completed = true, Detail = changed ? "ServiceRestartRequired" : "AlreadyAuthorized" };
        }
        catch (ControlRuntimeUnavailableException) { throw new RpcException(new(StatusCode.Unavailable, "Device settings are unavailable.")); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { LogFolderFailure(session, error); throw new RpcException(new(StatusCode.FailedPrecondition, "User authorization could not be saved.")); }
    }
    public override async Task<Wire.FolderInfo> PauseFolder(Wire.FolderRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); string actorSid = Authorize(context, session);
        try
        {
            var folder = await runtime.RequireFolderHost().PauseFolderAsync(request.Folder, actorSid,
                context.CancellationToken).ConfigureAwait(false);
            return new() { FolderId = Convert.ToHexString(folder.Id), Path = folder.Path,
                Access = folder.Access.ToString(), Paused = folder.Paused };
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }

    public override async Task<Wire.FolderInfo> ResumeFolder(Wire.FolderRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); string actorSid = Authorize(context, session);
        try
        {
            var folder = await runtime.RequireFolderHost().ResumeFolderAsync(request.Folder, actorSid,
                context.CancellationToken).ConfigureAwait(false);
            return new() { FolderId = Convert.ToHexString(folder.Id), Path = folder.Path,
                Access = folder.Access.ToString(), Paused = folder.Paused };
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }

    public override async Task<Wire.OperationResult> RemoveFolder(Wire.FolderRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); string actorSid = Authorize(context, session);
        try
        {
            await runtime.RequireFolderHost().RemoveFolderAsync(request.Folder, actorSid,
                context.CancellationToken).ConfigureAwait(false);
            return new() { Completed = true };
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }

    public override async Task<Wire.OperationResult> PauseAll(Wire.PauseAllRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); string actorSid = Authorize(context, session);
        try
        {
            int count = await runtime.RequireFolderHost().PauseAllAsync(request.DurationSeconds,
                actorSid, context.CancellationToken).ConfigureAwait(false);
            return new() { Completed = true, Detail = count.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }

    public override Task<Wire.ListPendingDevicesResponse> ListPendingDevices(
        Wire.StatusRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session);
        try
        {
            var response = new Wire.ListPendingDevicesResponse();
            foreach (var row in runtime.RequireFolderHost().ListPendingDevices())
            {
                if (response.Devices.Count == 512) { response.Truncated = true; break; }
                response.Devices.Add(new Wire.PendingDeviceInfo
                {
                    FolderId = Convert.ToHexString(row.FolderId), DeviceId = Convert.ToHexString(row.DeviceId),
                    Name = row.Name ?? "", RequestedUtcTicks = row.RequestedTicks,
                });
            }
            diagnostics?.TryWrite(new("control.pending_devices_listed", "control")
                { SessionId = session, ItemsVisited = response.Devices.Count });
            return Task.FromResult(response);
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }

    public override Task<Wire.OperationResult> ApproveDevice(Wire.DeviceActionRequest request,
        ServerCallContext context) => DecideDeviceAsync(request, context, DeviceApproval.Approved);
    public override Task<Wire.OperationResult> DenyDevice(Wire.DeviceActionRequest request,
        ServerCallContext context) => DecideDeviceAsync(request, context, DeviceApproval.Unknown);
    public override Task<Wire.OperationResult> BlockDevice(Wire.DeviceActionRequest request,
        ServerCallContext context) => DecideDeviceAsync(request, context, DeviceApproval.Blocked);

    private async Task<Wire.OperationResult> DecideDeviceAsync(Wire.DeviceActionRequest request,
        ServerCallContext context, DeviceApproval decision)
    {
        Guid session = Guid.NewGuid(); string actorSid = Authorize(context, session);
        if (request.DeviceId.Length != 64)
            throw new RpcException(new(StatusCode.InvalidArgument, "Invalid device identifier."));
        byte[] remoteDevice;
        try { remoteDevice = Convert.FromHexString(request.DeviceId); }
        catch (FormatException) { throw new RpcException(new(StatusCode.InvalidArgument, "Invalid device identifier.")); }
        try
        {
            await runtime.RequireFolderHost().DecideDeviceAsync(request.Folder,
                remoteDevice, decision, actorSid, context.CancellationToken).ConfigureAwait(false);
            return new() { Completed = true, Detail = decision.ToString() };
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }

    public override async Task<Wire.SettingsResponse> UpdateFolderSettings(
        Wire.UpdateFolderSettingsRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); string actorSid = Authorize(context, session);
        try
        {
            FolderSettings proposed = FolderSettings.FromJson(request.Json);
            var result = await runtime.RequireFolderHost().UpdateFolderSettingsAsync(
                request.Folder, proposed, actorSid, context.CancellationToken).ConfigureAwait(false);
            return new() { Json = result.Settings.ToJson(), RestartRequired = result.RestartRequired };
        }
        catch (Exception error) when (error is JsonException or ValidationException or ArgumentException)
        { LogFolderFailure(session, error); throw new RpcException(new(StatusCode.InvalidArgument, "Invalid folder settings.")); }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }

    public override Task<Wire.IgnoreRulesResponse> GetIgnoreRules(Wire.FolderRequest request,
        ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session);
        try
        {
            var folder = runtime.RequireFolderHost().GetFolderDescriptor(request.Folder);
            return Task.FromResult(new Wire.IgnoreRulesResponse
                { FolderId = Convert.ToHexString(folder.Id), Rules = folder.Settings.IgnoreRules });
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }

    public override async Task<Wire.IgnoreRulesResponse> SetIgnoreRules(Wire.SetIgnoreRulesRequest request,
        ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); string actorSid = Authorize(context, session);
        try
        {
            var host = runtime.RequireFolderHost();
            FolderSettings current = host.GetFolderSettings(request.Folder);
            var updated = await host.UpdateFolderSettingsAsync(request.Folder,
                current with { IgnoreRules = request.Rules }, actorSid, context.CancellationToken).ConfigureAwait(false);
            var folder = host.GetFolderDescriptor(request.Folder);
            return new() { FolderId = Convert.ToHexString(folder.Id), Rules = updated.Settings.IgnoreRules,
                RestartRequired = updated.RestartRequired };
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }

    public override Task<Wire.OperationResult> RestoreOverwritten(
        Wire.RestoreArchiveRequest request, ServerCallContext context)
        => RestoreArchiveCoreAsync(request, context, overwrittenOnly: true);

    public override Task<Wire.OperationResult> RestoreArchived(
        Wire.RestoreArchiveRequest request, ServerCallContext context)
        => RestoreArchiveCoreAsync(request, context, overwrittenOnly: false);

    private async Task<Wire.OperationResult> RestoreArchiveCoreAsync(
        Wire.RestoreArchiveRequest request, ServerCallContext context, bool overwrittenOnly)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session);
        if (request.ArchiveId <= 0 || request.AsCopy && string.IsNullOrWhiteSpace(request.CopyPath))
            throw new RpcException(new(StatusCode.InvalidArgument, "Invalid archive restore request."));
        try
        {
            var folder = runtime.RequireFolderHost().ResolveFolder(request.Folder);
            var archive = folder.Index.Archive.Find(request.ArchiveId)
                ?? throw new KeyNotFoundException("Archived version was not found.");
            if (overwrittenOnly && !IsOverwritten(folder.Index, archive))
                throw new RpcException(new(StatusCode.FailedPrecondition, "This version is not an overwrite."));
            FileApplyOutcome result = await runtime.RequireFolderHost().RestoreArchivedAsync(
                request.Folder, request.ArchiveId, request.AsCopy, request.CopyPath,
                context.CancellationToken).ConfigureAwait(false);
            diagnostics?.TryWrite(new("control.archive_restore", "control")
                { SessionId = session, ErrorCode = result.ToString().ToLowerInvariant() });
            return new() { Completed = result == FileApplyOutcome.Applied, Detail = result.ToString() };
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }
}
