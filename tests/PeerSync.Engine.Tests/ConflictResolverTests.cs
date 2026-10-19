using System.Security.Cryptography;
using Google.Protobuf;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using Shouldly;
using Xunit;
using Envelope = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Engine.Tests;

public sealed class ConflictResolverTests : IDisposable
{
    private readonly FolderKeys keys = FolderKeys.CreateNew();

    [Theory]
    [InlineData(2, 1, Outcome.Ignore)]
    [InlineData(1, 2, Outcome.ApplyRemote)]
    [InlineData(1, 1, Outcome.Ignore)]
    public void CausalityOverridesTimestamp(int localCounter, int remoteCounter, Outcome expected)
    {
        var local = View(1, (ulong)localCounter, 100, 100, "a");
        var remote = View(1, (ulong)remoteCounter, 1, 1, localCounter == remoteCounter ? "a" : "b");
        var result = ConflictResolver.Resolve(local, remote, new());
        result.Outcome.ShouldBe(expected);
    }

    [Fact]
    public void LiveMtimeWinsOverLateObservationAndTombstonesUseDeletionTime()
    {
        var oldNoticedLate = View(1, 1, 10, 1000, "a");
        var editedLater = View(2, 1, 20, 30, "b");
        var result = ConflictResolver.Resolve(oldNoticedLate, editedLater, new());
        result.Outcome.ShouldBe(Outcome.ApplyRemote); result.LocalLoserFate.ShouldBe(LoserFate.Archive);
        var deletion = View(1, 1, 10, 40, "", deleted: true);
        ConflictResolver.Resolve(editedLater, deletion, new()).Winner.ShouldBe(deletion);
        ConflictResolver.Resolve(deletion, editedLater, new()).Winner.ShouldBe(deletion);
        ConflictResolver.Resolve(deletion, editedLater, new()).LocalLoserFate.ShouldBe(LoserFate.None);
    }

    [Fact]
    public void ConcurrentMetadataAndDeviceTiesAreDeterministicAndEqualContentNeedsNoArchive()
    {
        var first = View(1, 1, 20, 100, "same");
        var second = View(2, 1, 20, 1, "same");
        var forward = ConflictResolver.Resolve(first, second, new());
        var reverse = ConflictResolver.Resolve(second, first, new());
        forward.Outcome.ShouldBe(Outcome.MergeVectorsOnly); reverse.Outcome.ShouldBe(Outcome.MergeVectorsOnly);
        forward.Winner.ShouldBe(second); reverse.Winner.ShouldBe(second);
        forward.LocalLoserFate.ShouldBe(LoserFate.None);
        forward.Vector.Count.ShouldBe(2); forward.Vector.Values.ShouldAllBe(v => v == 1);
    }

    [Fact]
    public void AllPermutationsChooseOnlyAmongCausallyMaximalVersions()
    {
        var stale = View(1, 1, 1000, 1000, "stale");
        var descendant = View(1, 2, 1, 1, "new causal version");
        var concurrent = View(2, 1, 50, 50, "concurrent");
        VersionView[] all = [stale, descendant, concurrent];
        foreach (var a in all)
        foreach (var b in all.Where(b => !ReferenceEquals(a, b)))
        {
            var c = all.Single(c => !ReferenceEquals(a, c) && !ReferenceEquals(b, c));
            ConflictResolver.SelectWinner([a, b, c]).ShouldBe(concurrent);
            var vector = ConflictResolver.MergeVectors([a, b, c]);
            vector[1].ShouldBe(2ul); vector[2].ShouldBe(1ul);
        }
    }

    [Theory]
    [InlineData(false, 1, LoserFate.Archive)]
    [InlineData(true, 1, LoserFate.ConflictCopy)]
    [InlineData(false, 0, LoserFate.ConflictCopy)]
    public void LosingDataUsesTheConfiguredArchiveOrConflictCopyPolicy(bool keepBoth, long limit, LoserFate expected)
    {
        var local = View(1, 1, 1, 1, "a"); var remote = View(2, 1, 2, 2, "b");
        ConflictResolver.Resolve(local, remote, new() { KeepBothCopies = keepBoth, ArchiveMaxFileBytes = limit })
            .LocalLoserFate.ShouldBe(expected);
        ConflictResolver.Resolve(remote, local, new()).LocalLoserFate.ShouldBe(LoserFate.None);
    }

    [Fact]
    public void MergesAreIdenticalAcrossRwKeysPreserveUnknownFieldsAndRoForwardsOriginalBytes()
    {
        var winner = View(2, 1, 20, 20, "winner", unknownField: true);
        var other = View(1, 1, 1, 1, "other");
        var vector = ConflictResolver.MergeVectors([winner, other]);
        using var sameFolder = FolderKeys.FromText(keys.ExportReadWriteText());
        var merged = ConflictResolver.MergeRecord(winner, vector, keys);
        var independentlyMerged = ConflictResolver.MergeRecord(winner, vector, sameFolder);
        merged.Body.ToArray().ShouldBe(independentlyMerged.Body.ToArray());
        merged.Signature.ToArray().ShouldBe(independentlyMerged.Signature.ToArray());
        merged.Body.ToArray().TakeLast(3).ShouldBe(new byte[] { 0xa0, 0x06, 7 });
        var opened = VersionView.Open(merged, keys, new byte[32]);
        opened.Vector.Count.ShouldBe(2); opened.Vector.Values.ShouldAllBe(v => v == 1);
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        var forwarded = ConflictResolver.MergeRecord(winner, vector, readOnly);
        forwarded.Body.ToArray().ShouldBe(winner.Record.Body.ToArray());
        forwarded.Signature.ToArray().ShouldBe(winner.Record.Signature.ToArray());
        Should.Throw<ArgumentException>(() => ConflictResolver.MergeRecord(winner, other.Vector, keys));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("chunk")]
    [InlineData("hash")]
    [InlineData("class")]
    [InlineData("time")]
    [InlineData("author")]
    [InlineData("type")]
    public void SignedButMalformedVersionMetadataIsRejected(string invalid)
    {
        var body = Body(1, 1, 1, 1, "data", false);
        switch (invalid)
        {
            case "size": body.Size++; break;
            case "chunk": body.Chunks[0].Length = 0; break;
            case "hash": body.FileHash = ByteString.Empty; break;
            case "class": body.ChunkClass = 2; break;
            case "time": body.ChangedTicks = -1; break;
            case "author": body.ModifiedBy = ByteString.Empty; break;
            case "type": body.Type = 4; break;
        }
        var record = SignedRecordFactory.Create(body, keys);
        Should.Throw<InvalidDataException>(() => VersionView.Open(record, keys, new byte[32]));
    }

    [Fact]
    public void VerificationPrecedesParsingAndExportedViewsCannotMutateOwnedMetadata()
    {
        Should.Throw<CryptographicException>(() => VersionView.Open(new Envelope(new byte[] { 0xff }, new byte[64]), keys, new byte[32]));
        var view = View(1, 1, 1, 1, "data");
        var body = view.ExportBody(); body.Path = "changed"; body.Vv[0].Counter = 100;
        view.Path.ShouldBe("file"); view.Vector[1].ShouldBe(1ul);
        view.ToString().ShouldNotContain("data"); view.ToString().ShouldNotContain("file/");
    }

    private VersionView View(ulong author, ulong counter, long mtime, long changed, string content, bool deleted = false, bool unknownField = false)
    {
        var record = SignedRecordFactory.Create(Body(author, counter, mtime, changed, content, deleted), keys);
        if (unknownField)
        {
            byte[] bytes = [.. record.Body, 0xa0, 0x06, 7];
            record = new(bytes, keys.Sign([.. "psp1 record"u8, .. keys.FolderId(), .. bytes]));
        }
        return VersionView.Open(record, keys, new byte[32]);
    }
    internal static FileRecordBody Body(ulong author, ulong counter, long mtime, long changed, string content, bool deleted)
    {
        byte[] authorId = new byte[32]; System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(authorId, author);
        byte[] data = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = Blake3.Hasher.Hash(data);
        var body = new FileRecordBody { Path = "file", MtimeTicks = mtime, ChangedTicks = changed,
            Deleted = deleted, ModifiedBy = ByteString.CopyFrom(authorId), Size = deleted ? 0ul : (ulong)data.Length,
            FileHash = deleted ? ByteString.Empty : ByteString.CopyFrom(hash.AsSpan()) };
        body.Vv.Add(new VvEntry { Device = author, Counter = counter });
        if (!deleted && data.Length != 0) body.Chunks.Add(new Chunk { Length = (uint)data.Length, Hash = body.FileHash });
        return body;
    }
    public void Dispose() => keys.Dispose();
}
