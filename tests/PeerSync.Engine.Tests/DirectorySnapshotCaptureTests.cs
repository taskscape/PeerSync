using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Directories;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;
using SignedRecord = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Engine.Tests;

public sealed class DirectorySnapshotCaptureTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-DirectoryCapture-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly byte[] device = [1, .. new byte[31]];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    public DirectorySnapshotCaptureTests() => Directory.CreateDirectory(Path.Combine(Root, "topic"));

    [Fact]
    public async Task InconsistentCheckpointCountCannotOmitAcceptedMembers()
    {
        await using var index = await Open(); var children = Children(3); await Advance(index, children, 1);
        byte[] empty = new DirectorySnapshotBuilder(static (_, _) => throw new InvalidOperationException()).ExportCheckpoint();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE directory_captures SET builder_state=$state;";
            command.Parameters.AddWithValue("$state", empty); command.ExecuteNonQuery();
        }
        await Should.ThrowAsync<InvalidDataException>(() => Advance(index, children, 1000));
        index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(1); index.ReadDirectoryCapture("topic")!.Hash.ShouldBeNull();
        (await Advance(index, Children(2), 1000)).Hash.ShouldNotBeNull("a new input starts an independent draft");
    }

    [Fact]
    public async Task RootReplacementCannotReuseAnotherTreesDraftOrCaptureIntoIt()
    {
        await using var index = await Open(); var children = Children(3); var prior = await Advance(index, children, 1);
        Directory.Move(Root, Path.Combine(fixture, "old-tree")); Directory.CreateDirectory(Path.Combine(Root, "topic"));
        await Should.ThrowAsync<IOException>(() => Advance(index, children, 1000));
        index.ReadDirectoryCapture("topic")!.InputHash.ShouldBe(prior.InputHash); index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(1);
        Directory.Exists(Path.Combine(Root, ".psp")).ShouldBeFalse();
    }

    [Fact]
    public async Task DuplicateKeysAndForeignEnvelopesCannotCreateACaptureDraft()
    {
        await using var index = await Open();
        await Should.ThrowAsync<InvalidDataException>(() => Advance(index, [Sign("topic/a"), Sign("topic/A")], 1000));
        using var foreign = FolderKeys.CreateNew(); SignedRecordFactory.TryOpen(Sign("topic/a"), keys, out var body).ShouldBeTrue();
        await Should.ThrowAsync<System.Security.Cryptography.CryptographicException>(() => Advance(index, [SignedRecordFactory.Create(body!, foreign)], 1000));
        index.HasDirectoryCaptures().ShouldBeFalse();
    }

    [Fact]
    public async Task CaptureDiagnosticsRetainCountsAndSanitizedFailuresWithoutPrivateState()
    {
        await using var index = await Open(); using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "capture", debugEnabled: true);
        using var gate = await FolderOperationGate.AcquireAsync(index, Token);
        var capture = new DirectorySnapshotCapture(Root, index, keys, device, journal); var children = Children(3);
        await capture.AdvanceUnderGateAsync("topic", children, 1, Token);
        var completed = await capture.AdvanceUnderGateAsync("topic", children, 1000, Token);
        await Should.ThrowAsync<InvalidDataException>(() => capture.AdvanceUnderGateAsync("topic", [Sign("other/private-file")], 1000, Token));
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        log.ShouldContain("directory.capture_pending"); log.ShouldContain("directory.capture_complete"); log.ShouldContain("directory.capture_failed");
        log.ShouldNotContain("topic"); log.ShouldNotContain("file000"); log.ShouldNotContain("private-file"); log.ShouldNotContain(fixture);
        log.ShouldNotContain(Convert.ToHexString(completed.Hash!)); log.ShouldNotContain(Convert.ToBase64String(children[0].Body)); log.ShouldNotContain(keys.ExportText());
    }

    [Theory]
    [InlineData("member")]
    [InlineData("captured")]
    [InlineData("progress")]
    public async Task InterruptedCaptureResumesDurablePrefixAndMatchesUninterruptedSnapshot(string boundary)
    {
        var children = Children(129); DirectorySnapshotLink expected;
        await using (var index = await Open())
        {
            using var blocks = new DirectorySnapshotBlocks(Root, index, keys, device);
            expected = await blocks.CaptureDirectoryAsync("topic", children, Token);
            await Should.ThrowAsync<Stop>(() => Advance(index, children, 128, stage => { if (stage == boundary) throw new Stop(); }));
            index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(boundary == "progress" ? 128 : 0);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var complete = await Advance(reopened, children, 1000); complete.Hash.ShouldBe(expected.Hash); complete.Length.ShouldBe(expected.EncodedLength);
        complete.Accepted.ShouldBe(129); complete.BuilderState.ShouldBeNull(); reopened.HasDirectoryCaptures().ShouldBeTrue();
        (await Advance(reopened, children, 1)).Hash.ShouldBe(expected.Hash);
    }

    [Fact]
    public async Task ChangedDirectoryIdentityResetsDraftWhileLocalSequenceChangesDoNot()
    {
        await using var index = await Open(); var children = Children(3);
        var first = await Advance(index, children, 1); first.Accepted.ShouldBe(1);
        var second = await Advance(index, children.Select(record => record.WithLocalSequence(99)), 1); second.Accepted.ShouldBe(2); second.InputHash.ShouldBe(first.InputHash);
        string topic = Path.Combine(Root, "topic"), old = Path.Combine(Root, "old-topic"); Directory.Move(topic, old); Directory.CreateDirectory(topic);
        var reset = await Advance(index, children, 1); reset.Accepted.ShouldBe(1); reset.NativeId.ShouldNotBe(first.NativeId);
        (await Advance(index, children, 1000)).Hash.ShouldNotBeNull();
    }

    [Fact]
    public async Task ChangingEnumerationCannotCommitAMixedSnapshotAndStableRetryCompletes()
    {
        await using var index = await Open(); int enumeration = 0;
        IEnumerable<SignedRecord> Unstable()
        {
            enumeration++; yield return Sign("topic/a"); yield return Sign(enumeration % 2 == 1 ? "topic/b" : "topic/c");
        }
        await Should.ThrowAsync<InvalidDataException>(() => Advance(index, Unstable(), 1000));
        index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(0); index.ReadDirectoryCapture("topic")!.Hash.ShouldBeNull();
        var stable = new[] { Sign("topic/a"), Sign("topic/b") }; var completed = await Advance(index, stable, 1000);
        using var blocks = new DirectorySnapshotBlocks(Root, index, keys, device);
        completed.Hash.ShouldBe((await blocks.CaptureDirectoryAsync("topic", stable, Token)).Hash);
    }

    [Fact]
    public async Task CancellationAfterMetadataFlushPreservesOldPrefixAndIndependentBytesForRetry()
    {
        await using var index = await Open(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var children = Children(3);
        await Should.ThrowAsync<OperationCanceledException>(() => Advance(index, children, 1, stage => { if (stage == "captured") stop.Cancel(); }, stop.Token));
        index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(0); index.ReadBlockObjects().ShouldNotBeEmpty();
        (await Advance(index, children, 1000)).Hash.ShouldNotBeNull();
    }

    [Fact]
    public async Task PinnedNativeDirectoryCannotBeReplacedDuringCapture()
    {
        await using var index = await Open(); bool attempted = false;
        var result = await Advance(index, Children(1), 1, stage =>
        {
            if (stage != "member") return; attempted = true;
            Should.Throw<IOException>(() => Directory.Move(Path.Combine(Root, "topic"), Path.Combine(Root, "replacement")));
        });
        attempted.ShouldBeTrue(); result.Hash.ShouldNotBeNull(); Directory.Exists(Path.Combine(Root, "replacement")).ShouldBeFalse();
    }

    [Fact]
    public async Task DifferentManifestRestartsRatherThanReusingAnotherChildPrefix()
    {
        await using var index = await Open(); var before = await Advance(index, Children(3), 1);
        var changed = new[] { Sign("topic/a"), Sign("topic/z") }; var reset = await Advance(index, changed, 1);
        reset.Accepted.ShouldBe(1); reset.InputHash.ShouldNotBe(before.InputHash); reset.LastKey.ShouldBe("TOPIC/A");
        var complete = await Advance(index, changed, 1); complete.Hash.ShouldNotBeNull(); complete.Accepted.ShouldBe(2);
    }

    [Fact]
    public async Task EmptyDirectoryProducesTheCanonicalEmptyRootAndKeepsItsUnpublishedBarrier()
    {
        await using var index = await Open(); var result = await Advance(index, [], 1);
        result.Hash.ShouldBe(DirectorySnapshotNode.Entries([]).CalculateHash()); result.Length.ShouldBe(12); result.Accepted.ShouldBe(0);
        index.HasDirectoryCaptures().ShouldBeTrue();
    }

    private async Task<DirectoryCaptureState> Advance(FolderIndex index, IEnumerable<SignedRecord> children, int budget,
        Action<string>? checkpoint = null, CancellationToken? token = null)
    {
        var cancellation = token ?? Token; using var gate = await FolderOperationGate.AcquireAsync(index, cancellation);
        return await new DirectorySnapshotCapture(Root, index, keys, device) { Checkpoint = checkpoint }.AdvanceUnderGateAsync("topic", children, budget, cancellation);
    }
    private SignedRecord[] Children(int count) => Enumerable.Range(0, count).Select(value => Sign("topic/file" + value.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
    private SignedRecord Sign(string path)
    {
        var body = new FileRecordBody { Path = path, Type = 0, MtimeTicks = 638000000000000000, ChangedTicks = 638000000000000000,
            ModifiedBy = ByteString.CopyFrom(device), FileHash = ByteString.CopyFrom(Blake3.Hasher.Hash([]).AsSpan()) };
        body.Vv.Add(new VvEntry { Device = 1, Counter = 1 }); return SignedRecordFactory.Create(body, keys);
    }
    private async Task<FolderIndex> Open()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = new Scanner(Root, index, keys, device); (await scanner.ScanAsync(Token)).Problems.ShouldBeEmpty(); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private sealed class Stop : Exception;
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-DirectoryCapture-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
