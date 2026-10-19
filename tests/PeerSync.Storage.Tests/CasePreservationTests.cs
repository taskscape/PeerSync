using System.Buffers.Binary;
using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class CasePreservationTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-CaseDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static readonly byte[] Root = new byte[24], Device = new byte[32];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public CasePreservationTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task SourcesSurviveReopenRowReplacementAndNeededRemovalWithExactOriginalBytes()
    {
        byte[] id = Id(1), device = Device.ToArray();
        var source = new CasePreservationSource(id, device, "file", Record(1)); id[0] = 99; device[0] = 99;
        await using (var index = await Open())
        {
            await Receive(index, [source]);
            var replay = await Receive(index, [source]); replay.Applied.ShouldBeFalse();
            await index.ApplyRemoteIndexAsync(Device, 1, 2, [Row("file", 2)], _ => null, Token);
            index.Needed.Count().ShouldBe(0); index.HasPendingCasePreservation("FILE").ShouldBeTrue();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var saved = reopened.ReadPendingCasePreservations().Single();
        saved.RootId.ShouldBe(Root); saved.Source.SourceId.ShouldBe(Id(1)); saved.Source.SourceDevice.ShouldBe(Device);
        saved.Source.Record.Body.ToArray().ShouldBe(new byte[] { 1 }); saved.Source.Record.LocalSequence.ShouldBe(0ul);
        saved.Source.SourceId[0] = 98; saved.RootId[0] = 98;
        reopened.ReadPendingCasePreservations().Single().Source.SourceId.ShouldBe(Id(1));
        source.ToString().ShouldNotContain("file"); saved.ToString().ShouldNotContain("file");
    }

    [Fact]
    public async Task ForwardedDuplicateIsIdempotentButMismatchedIdentityRollsBackAllReceiptState()
    {
        await using var index = await Open(); var source = Source(1);
        await Receive(index, [source]);
        await index.RecomputeNeededAsync(["file"], s => Needed(s.PathKey, [new(source.SourceId, Id(6), "file", source.Record)]), Token);
        index.ReadPendingCasePreservations().Count.ShouldBe(1);
        await Should.ThrowAsync<InvalidDataException>(() => index.ApplyRemoteIndexAsync(Device, 1, 2, [Row("file", 2)],
            s => Needed(s.PathKey, [new(source.SourceId, Device, "file", Record(9))]), Token));
        index.Devices.Find(Device)!.LastSequenceReceived.ShouldBe(1);
        index.RemotesForPath("file").Single().Record.Body.ToArray().ShouldBe(new byte[] { 1 });
        index.ReadPendingCasePreservations().Single().Source.Record.Body.ToArray().ShouldBe(new byte[] { 1 });
    }

    [Fact]
    public async Task CapacityBackpressureRollsBackSourcesNeededRowsAndCursorTogether()
    {
        await using var index = await Open();
        await Receive(index, Enumerable.Range(1, FolderIndex.MaximumPendingCaseSources - 1).Select(Source).ToArray());
        await Should.ThrowAsync<InvalidOperationException>(() => index.ApplyRemoteIndexAsync(Device, 1, 2, [Row("file", 2)],
            s => Needed(s.PathKey, [Source(1000), Source(1001)]), Token));
        index.Devices.Find(Device)!.LastSequenceReceived.ShouldBe(1); index.ReadPendingCasePreservations().Count.ShouldBe(999);
        // Writer remains usable and can acknowledge exactly the remaining capacity.
        await index.ApplyRemoteIndexAsync(Device, 1, 2, [Row("file", 2)], s => Needed(s.PathKey, [Source(1000)]), Token);
        index.ReadPendingCasePreservations().Count.ShouldBe(1000);
        Should.Throw<ArgumentOutOfRangeException>(() => index.ReadPendingCasePreservations(1001));
    }

    [Fact]
    public async Task FailedFinalAdmissionRollsBackNewSourcesAndExistingCursor()
    {
        await using var index = await Open(); int admissions = 0;
        await Should.ThrowAsync<InvalidOperationException>(() => index.ApplyRemoteIndexWithAdmissionAsync(Device, 0, 1, [Row("file", 1)],
            s => Needed(s.PathKey, [Source(1)]), () => { if (++admissions == 2) throw new InvalidOperationException("revoked"); }, Token));
        index.ReadPendingCasePreservations().ShouldBeEmpty(); index.Needed.Count().ShouldBe(0);
        index.Devices.Count().ShouldBe(0); index.RemoteFiles.Count().ShouldBe(0);
    }

    [Fact]
    public async Task UnboundOrChangedRootAndWrongPathFailClosed()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => Receive(index, [Source(1)]));
        index.Devices.Count().ShouldBe(0);
        await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); await Receive(index, [Source(1)]);
        await Should.ThrowAsync<InvalidOperationException>(() => index.RecomputeNeededAsync(["other"], s => Needed(s.PathKey, [Source(2)]), Token));
        await index.Meta.UpsertAsync([new("_root-identity", Enumerable.Repeat((byte)2, 24).ToArray())], Token);
        await Should.ThrowAsync<InvalidDataException>(() => index.RecomputeNeededAsync(["file"], s => Needed(s.PathKey, [Source(1)]), Token));
        index.ReadPendingCasePreservations().Count.ShouldBe(1);
    }

    [Fact]
    public async Task PendingCaseSourceProtectsUnneededStagingWithoutAnActiveApply()
    {
        await using var index = await Open(); await Receive(index, [Source(1)]); await index.Needed.DeleteAsync(["file"], Token);
        var intent = new ApplyIntent(Guid.NewGuid(), "file", null, [1]); await index.ReserveApplyAsync(intent, Token);
        var staging = new StagedObject(Guid.NewGuid(), Root, new byte[16], "file", Guid.NewGuid().ToString("N") + ".!psp", false, 1, 1, new byte[32], 1);
        var saved = await index.ParkStagingAsync(intent, staging, Token); await index.AbandonApplyAsync(intent, Token);
        (await index.ReserveStagingRetirementAsync(saved, 1 + FolderIndex.StagingGraceTicks, Token)).ShouldBeFalse();
    }

    private async Task<FolderIndex> Open()
    { var index = await FolderIndex.OpenAsync(Database, Token); await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); return index; }
    private static Task<RemoteIndexResult> Receive(FolderIndex index, IReadOnlyList<CasePreservationSource> sources) =>
        index.ApplyRemoteIndexAsync(Device, 0, 1, [Row("file", 1)], s => Needed(s.PathKey, sources), Token);
    private static NeededRow Needed(string path, IReadOnlyList<CasePreservationSource> sources) => new(path, Device, 0) { CasePreservations = sources };
    private static CasePreservationSource Source(int number) => new(Id(number), Device, "file", Record(1));
    private static byte[] Id(int number) { byte[] id = new byte[32]; BinaryPrimitives.WriteInt32LittleEndian(id, number); return id; }
    private static SignedRecord Record(byte value) => new([value], new byte[64]);
    private static RemoteFileRow Row(string path, ulong sequence) => new(Device, path, new Dictionary<ulong, ulong> { [1] = sequence }, Record((byte)sequence).WithLocalSequence(sequence));
    public void Dispose() => Directory.Delete(fixture, true);
}
