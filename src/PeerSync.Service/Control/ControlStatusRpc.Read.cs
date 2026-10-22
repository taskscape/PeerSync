using System.Security.Cryptography;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Grpc.Core;
using PeerSync.Core.Paths;
using PeerSync.Storage.FolderDb;
using Wire = PeerSync.Control.Contracts.V1;

namespace PeerSync.Service.Control;

internal sealed partial class ControlStatusRpc
{
    private const int MaximumEventBytes = 240 * 1024;
    private const int MaximumArchiveRows = 512;

    public override Task<Wire.SettingsResponse> GetSettings(Wire.StatusRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session);
        try { return Task.FromResult(new Wire.SettingsResponse { Json = ControlSettingsRuntime.Serialize(settings.Read()) }); }
        catch (ControlRuntimeUnavailableException) { throw new RpcException(new(StatusCode.Unavailable, "Device settings are unavailable.")); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { LogFolderFailure(session, error); throw new RpcException(new(StatusCode.Unavailable, "Device settings are unavailable.")); }
    }

    public override async Task<Wire.SettingsResponse> UpdateSettings(Wire.UpdateSettingsRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); string actorSid = Authorize(context, session);
        try
        {
            await runtime.RequireFolderHost().AuditDeviceSettingsUpdateRequestAsync(actorSid,
                context.CancellationToken).ConfigureAwait(false);
            var updated = settings.Update(request.Json);
            diagnostics?.TryWrite(new("control.settings_updated", "control") { SessionId = session, ItemsChanged = 1 });
            return new Wire.SettingsResponse
                { Json = ControlSettingsRuntime.Serialize(updated.Value), RestartRequired = updated.RestartRequired };
        }
        catch (Exception error) when (error is JsonException or ValidationException or ArgumentException)
        { LogFolderFailure(session, error); throw new RpcException(new(StatusCode.InvalidArgument, "Invalid device settings.")); }
        catch (ControlRuntimeUnavailableException) { throw new RpcException(new(StatusCode.Unavailable, "Device settings are unavailable.")); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { LogFolderFailure(session, error); throw new RpcException(new(StatusCode.Unavailable, "Device settings could not be saved.")); }
    }

    public override Task<Wire.SettingsResponse> GetFolderSettings(Wire.FolderRequest request, ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session);
        try
        {
            var selected = runtime.RequireFolderHost().GetFolderSettings(request.Folder);
            return Task.FromResult(new Wire.SettingsResponse { Json = selected.ToJson() });
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }

    public override Task<Wire.ListArchiveResponse> ListArchive(Wire.FolderRequest request, ServerCallContext context)
        => ListArchiveCore(request, context, overwrittenOnly: false);

    public override Task<Wire.ListArchiveResponse> ListOverwritten(Wire.FolderRequest request, ServerCallContext context)
        => ListArchiveCore(request, context, overwrittenOnly: true);

    private Task<Wire.ListArchiveResponse> ListArchiveCore(Wire.FolderRequest request, ServerCallContext context, bool overwrittenOnly)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session);
        try
        {
            var folder = runtime.RequireFolderHost().ResolveFolder(request.Folder);
            var response = new Wire.ListArchiveResponse { FolderId = Convert.ToHexString(folder.Keys.FolderId()) };
            foreach (var row in folder.Index.ArchivesByAge())
            {
                bool overwritten = IsOverwritten(folder.Index, row);
                if (overwrittenOnly && !overwritten) continue;
                if (response.Versions.Count == MaximumArchiveRows) { response.Truncated = true; break; }
                response.Versions.Add(new Wire.ArchiveInfo
                {
                    Id = row.Id, OriginalPath = row.OriginalPath, ArchivedUtcTicks = row.ArchivedTicks,
                    Size = row.Size, Overwritten = overwritten, FolderId = response.FolderId,
                });
            }
            diagnostics?.TryWrite(new(overwrittenOnly ? "control.overwritten_listed" : "control.archive_listed", "control")
                { SessionId = session, ItemsVisited = response.Versions.Count });
            return Task.FromResult(response);
        }
        catch (Exception error) { LogFolderFailure(session, error); throw MapFolderError(error); }
    }

    private static bool IsOverwritten(FolderIndex index, ArchiveRow archive)
    {
        if (archive.FileHash is null) return false;
        var current = index.LocalFiles.Find(PathRules.Key(archive.OriginalPath));
        return current is { Deleted: false, Type: 0, FileHash: not null }
            && !CryptographicOperations.FixedTimeEquals(current.FileHash, archive.FileHash);
    }

    public override async Task WatchMetrics(Wire.StatusRequest request, IServerStreamWriter<Wire.ControlMetrics> responseStream,
        ServerCallContext context)
    {
        Guid session = Guid.NewGuid(); Authorize(context, session);
        if (!subscriptions.TryEnter()) throw new RpcException(new(StatusCode.ResourceExhausted, "Control subscription limit reached."));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.Token);
        var previous = runtime.ReadTransferTotals();
        long previousHashed = runtime.ReadHashedBytes();
        long previousTime = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                var snapshot = runtime.ReadStatus();
                var current = runtime.ReadTransferTotals();
                long currentHashed = runtime.ReadHashedBytes();
                long currentTime = System.Diagnostics.Stopwatch.GetTimestamp();
                double seconds = Math.Max(0.001,
                    (currentTime - previousTime) / (double)System.Diagnostics.Stopwatch.Frequency);
                ulong uploadRate = (ulong)(Math.Max(0, current.Uploaded - previous.Uploaded) / seconds);
                ulong downloadRate = (ulong)(Math.Max(0, current.Downloaded - previous.Downloaded) / seconds);
                ulong hashingRate = (ulong)(Math.Max(0, currentHashed - previousHashed) / seconds);
                previous = current; previousTime = currentTime;
                previousHashed = currentHashed;
                int folders = 0;
                int pending = 0;
                try { var host = runtime.RequireFolderHost(); folders = host.ListFolders().Count; pending = host.ListPendingDevices().Count; }
                catch (ControlRuntimeUnavailableException) { }
                await responseStream.WriteAsync(new Wire.ControlMetrics
                {
                    InstanceId = snapshot.InstanceId.ToString("N"), Sequence = snapshot.Sequence,
                    SampledUtcTicks = DateTime.UtcNow.Ticks,
                    ActivePeerSessions = (uint)runtime.ActivePeerSessionCount,
                    ActiveFolders = (uint)folders,
                    PendingDevices = (uint)pending,
                    UploadBytesPerSecond = uploadRate,
                    DownloadBytesPerSecond = downloadRate,
                    HashingBytesPerSecond = hashingRate,
                    QueueDepth = (uint)runtime.PendingTransferRequests,
                    TransferMetricsAvailable = true,
                }, stop.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token).ConfigureAwait(false);
            }
        }
        finally { subscriptions.Exit(); }
    }

    private Wire.ControlEvent BuildEvent(Guid session)
    {
        var message = new Wire.ControlEvent { Status = Read(session), FullSnapshot = true };
        while (message.CalculateSize() > MaximumEventBytes && message.Status.Clocks.Count > 0)
        {
            message.Status.Clocks.RemoveAt(message.Status.Clocks.Count - 1);
            message.Status.ClockStatusTruncated = true;
            message.Truncated = true;
        }
        try
        {
            foreach (var error in runtime.ReadRecentErrors())
            {
                var item = new Wire.ControlError
                    { Code = error.Code, Source = error.Source, OccurredUtcTicks = error.OccurredUtcTicks };
                if (!TryAdd(message, () => message.Errors.Add(item),
                    () => message.Errors.RemoveAt(message.Errors.Count - 1))) break;
            }
            foreach (var progress in runtime.ReadTransferProgress())
            {
                var item = new Wire.ControlTransferProgress
                {
                    FolderId = progress.FolderId,
                    UploadedBytes = (ulong)progress.Uploaded,
                    DownloadedBytes = (ulong)progress.Downloaded,
                };
                if (!TryAdd(message, () => message.Progress.Add(item),
                    () => message.Progress.RemoveAt(message.Progress.Count - 1))) break;
            }
            var host = runtime.RequireFolderHost();
            foreach (var folder in host.ListFolders())
            {
                var info = new Wire.FolderInfo
                {
                    FolderId = Convert.ToHexString(folder.Id), Path = folder.Path,
                    Access = folder.Access.ToString(), Paused = folder.Paused,
                };
                if (!TryAdd(message, () => message.Folders.Add(info), () => message.Folders.RemoveAt(message.Folders.Count - 1))) break;
                if (folder.Paused) continue;
                foreach (var peer in host.ListPeers(info.FolderId))
                {
                    var item = new Wire.FolderPeerInfo
                    {
                        FolderId = info.FolderId,
                        Peer = new Wire.PeerInfo
                        {
                            DeviceId = Convert.ToHexString(peer.DeviceId), Name = peer.Name ?? "",
                            Approval = peer.Approval.ToString(), Pinned = peer.Pinned,
                            Transport = runtime.ReadPeerTransport(peer.DeviceId),
                        },
                    };
                    if (peer.LastSeenTicks is { } ticks) item.Peer.LastSeenUtcTicks = ticks;
                    if (!TryAdd(message, () => message.Peers.Add(item), () => message.Peers.RemoveAt(message.Peers.Count - 1))) break;
                }
                var index = host.ResolveFolder(info.FolderId).Index;
                int archivedVisited = 0;
                foreach (var archive in index.ArchivesByAge())
                {
                    if (++archivedVisited > 2048) { message.Truncated = true; break; }
                    if (!IsOverwritten(index, archive)) continue;
                    var item = new Wire.ArchiveInfo
                    {
                        Id = archive.Id, OriginalPath = archive.OriginalPath,
                        ArchivedUtcTicks = archive.ArchivedTicks, Size = archive.Size,
                        Overwritten = true, FolderId = info.FolderId,
                    };
                    if (!TryAdd(message, () => message.Overwrites.Add(item), () => message.Overwrites.RemoveAt(message.Overwrites.Count - 1))) break;
                }
            }
            foreach (var pending in host.ListPendingDevices())
            {
                var item = new Wire.PendingDeviceInfo
                {
                    FolderId = Convert.ToHexString(pending.FolderId),
                    DeviceId = Convert.ToHexString(pending.DeviceId),
                    Name = pending.Name ?? "", RequestedUtcTicks = pending.RequestedTicks,
                };
                if (!TryAdd(message, () => message.PendingDevices.Add(item),
                    () => message.PendingDevices.RemoveAt(message.PendingDevices.Count - 1))) break;
            }
        }
        catch (ControlRuntimeUnavailableException) { }
        return message;
    }

    private static bool TryAdd(Wire.ControlEvent message, Action add, Action remove)
    {
        add();
        if (message.CalculateSize() <= MaximumEventBytes) return true;
        remove(); message.Truncated = true; return false;
    }
}
