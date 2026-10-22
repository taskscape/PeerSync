using System.Text.Json.Serialization;
using PeerSync.Core.Keys;
using PeerSync.Core.Settings;

namespace PeerSync.Storage.StateDb;

public sealed record FolderDescriptor([property: JsonIgnore] byte[] Id, string Path,
    FolderKeyKind Access, FolderSettings Settings, bool Paused)
{
    public override string ToString() => $"Folder descriptor ({Access}, paused={Paused})";
}

public sealed record PendingApproval([property: JsonIgnore] byte[] FolderId,
    [property: JsonIgnore] byte[] DeviceId, string? Name, long RequestedTicks);

public sealed record AuditEntry(long Id, long OccurredTicks, string Action, string ActorSid,
    [property: JsonIgnore] byte[]? FolderId, [property: JsonIgnore] byte[]? DeviceId,
    string? TargetSid = null);
