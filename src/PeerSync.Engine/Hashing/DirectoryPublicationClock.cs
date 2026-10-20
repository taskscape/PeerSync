using System.Buffers.Binary;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

internal static class DirectoryPublicationClock
{
    // Same non-vector body means the exact original tree/metadata already
    // covered by this publication's existing authority, not a different root.
    internal static FileRecordBody Template(FolderIndex index, FolderKeys keys, byte[] own, DirectoryPublication queued, long changedTicks)
    {
        var template = VersionView.Open(queued.Template, keys, own);
        var body = queued.ReconstructionOnly
            ? DirectoryRebaseProof.CreateTemplate(template, own, changedTicks, queued.Path, queued.Attributes)
            : template.ExportBody();
        var prior = index.LocalFiles.Find(queued.Path);
        if ((prior?.LocalSequence ?? 0) != queued.ExpectedLocalSequence) throw new InvalidOperationException("Directory parent clock source changed.");
        if (prior is not { Type: 1, Deleted: false, Record: not null }) return body;
        var original = VersionView.Open(prior.Record, keys, own);
        var metadata = original.ExportBody();
        if (metadata.DirectorySnapshotHash.Length != 32) return body;
        metadata.Vv.Clear(); IReadOnlyDictionary<ulong, ulong> floor = original.Vector;
        foreach (var remote in index.RemotesForPath(queued.Path))
        {
            var version = VersionView.Open(remote.Record, keys, remote.DeviceId); var other = version.ExportBody(); other.Vv.Clear();
            if (metadata.Equals(other)) floor = VersionVector.Merge(floor, version.Vector);
        }
        if (VersionVector.Compare(floor, original.Vector) == VvOrder.Equal) return body;
        var vector = VersionVector.Merge(body.Vv.ToDictionary(entry => entry.Device, entry => entry.Counter),
            VersionVector.Increment(floor, BinaryPrimitives.ReadUInt64LittleEndian(own)));
        var entries = body.Vv.ToDictionary(entry => entry.Device);
        body.Vv.Clear(); body.Vv.AddRange(vector.OrderBy(entry => entry.Key).Select(entry =>
        {
            var value = entries.GetValueOrDefault(entry.Key) ?? new VvEntry { Device = entry.Key };
            value.Counter = entry.Value; return value;
        }));
        return body;
    }
}
