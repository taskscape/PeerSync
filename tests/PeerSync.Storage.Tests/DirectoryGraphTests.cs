using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class DirectoryGraphTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-DirectoryGraphDb-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string Database => Path.Combine(fixture, "index.db");
    private static byte[] Root => new byte[24];
    private static byte[] Id(byte value = 1) => [value, .. new byte[31]];
    public DirectoryGraphTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task RetirementMutatesAtMostOneThousandRowsAndResumesWithoutExposingPartialCoverage()
    {
        await using (var index = await Open())
        {
            await Begin(index); var work = index.ReadDirectoryGraphWork(Id())!;
            var references = Enumerable.Range(0, 1001).Select(value =>
            { byte[] hash = new byte[32]; BinaryPrimitives.WriteInt32LittleEndian(hash, value); return new DirectoryGraphBlock(hash, 4); }).ToArray();
            await index.AppendDirectoryGraphReferencesAsync(Id(), references[..1000], Token);
            await index.AppendDirectoryGraphReferencesAsync(Id(), references[1000..], Token);
            await index.CompleteDirectoryGraphWorkAsync(Id(), work.Id, [], cancellationToken: Token); Age();
            var partial = await index.PruneDirectoryGraphsAsync(Root, DateTime.UtcNow.Ticks, true, cancellationToken: Token);
            partial.Mutations.ShouldBe(1000); partial.MoreWork.ShouldBeTrue(); index.ReadDirectoryGraph(Id())!.Retiring.ShouldBeTrue();
            Should.Throw<InvalidOperationException>(() => index.ReadDirectoryGraphReferences(Id()).ToArray());
            (await Begin(index)).Retiring.ShouldBeTrue();
        }
        await using var reopened = await Open();
        var last = await reopened.PruneDirectoryGraphsAsync(Root, DateTime.UtcNow.Ticks, false, cancellationToken: Token);
        last.Mutations.ShouldBe(4); last.MoreWork.ShouldBeFalse(); reopened.ReadDirectoryGraph(Id()).ShouldBeNull();
        var fresh = await Begin(reopened); fresh.Retiring.ShouldBeFalse(); fresh.Complete.ShouldBeFalse();
    }

    [Fact]
    public async Task TouchProtectsAnExpiredNeededGraphAndWrongRootOrCancellationCannotStartRetirement()
    {
        await using var index = await Open(); await Begin(index); Age();
        (await index.PruneDirectoryGraphsAsync(Root, DateTime.UtcNow.Ticks, false, cancellationToken: Token)).Mutations.ShouldBe(0);
        await Begin(index);
        (await index.PruneDirectoryGraphsAsync(Root, DateTime.UtcNow.Ticks, true, cancellationToken: Token)).Mutations.ShouldBe(0);
        Age(); byte[] changedRoot = Root; changedRoot[0] = 1;
        await Should.ThrowAsync<InvalidOperationException>(() => index.PruneDirectoryGraphsAsync(changedRoot, DateTime.UtcNow.Ticks, true, cancellationToken: Token));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.PruneDirectoryGraphsAsync(Root, DateTime.UtcNow.Ticks, true, cancellationToken: stop.Token));
        index.ReadDirectoryGraph(Id())!.Retiring.ShouldBeFalse();
    }

    [Fact]
    public async Task RestartKeepsPendingChildrenAndReferencePrefixesWhileIncompleteCoverageIsUnavailable()
    {
        await using (var index = await Open())
        {
            await Begin(index); var work = index.ReadDirectoryGraphWork(Id())!;
            await index.AppendDirectoryGraphReferencesAsync(Id(), [new(Id(2), 12)], Token);
            (await index.CompleteDirectoryGraphWorkAsync(Id(), work.Id, [Child("topic", 3)], cancellationToken: Token)).ShouldBeFalse();
            Should.Throw<InvalidOperationException>(() => index.ReadDirectoryGraphReferences(Id()).ToArray());
        }
        await using var reopened = await Open(); var next = reopened.ReadDirectoryGraphWork(Id())!;
        next.Hash.ShouldBe(Id(3)); next.ParentPath.ShouldBe("topic"); next.Kind.ShouldBe(DirectoryGraphWorkKind.Record);
        await reopened.AppendDirectoryGraphReferencesAsync(Id(), [new(Id(2), 12), new(Id(3), 48)], Token);
        (await reopened.CompleteDirectoryGraphWorkAsync(Id(), next.Id, [], new("topic/child", Id(3), 48), Token)).ShouldBeTrue();
        reopened.ReadDirectoryGraphReferences(Id()).Select(row => row.Length).ShouldBe([12, 48]);
        reopened.ReadDirectoryGraphMembers(Id()).Single().PathKey.ShouldBe("TOPIC/CHILD");
        reopened.ReadDirectoryGraph(Id())!.Complete.ShouldBeTrue(); reopened.ReadDirectoryGraphWork(Id()).ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => reopened.AppendDirectoryGraphReferencesAsync(Id(), [new(Id(4), 99)], Token));
    }

    [Fact]
    public async Task DuplicateStructureAndChildKeysRollBackTheWholeWorkCommit()
    {
        await using var index = await Open(); await Begin(index); var root = index.ReadDirectoryGraphWork(Id())!;
        await Should.ThrowAsync<InvalidDataException>(() => index.CompleteDirectoryGraphWorkAsync(Id(), root.Id, [Child("topic", 3), Child("topic", 3)], cancellationToken: Token));
        index.ReadDirectoryGraphWork(Id())!.Id.ShouldBe(root.Id);
        await index.CompleteDirectoryGraphWorkAsync(Id(), root.Id, [Child("topic", 3), Child("topic", 4)], cancellationToken: Token);
        var first = index.ReadDirectoryGraphWork(Id())!;
        await index.CompleteDirectoryGraphWorkAsync(Id(), first.Id, [], new("topic/child", Id(3), 48), Token);
        var second = index.ReadDirectoryGraphWork(Id())!;
        await Should.ThrowAsync<InvalidDataException>(() => index.CompleteDirectoryGraphWorkAsync(Id(), second.Id, [], new("TOPIC/CHILD", Id(4), 48), Token));
        index.ReadDirectoryGraphWork(Id())!.Id.ShouldBe(second.Id); index.ReadDirectoryGraphMembers(Id()).Count().ShouldBe(1);
        index.ReadDirectoryGraph(Id())!.Complete.ShouldBeFalse();
    }

    [Fact]
    public async Task ConflictingRootsSourcesLengthsAndContextsCannotAdvanceAJob()
    {
        await using var index = await Open(); await Begin(index); var work = index.ReadDirectoryGraphWork(Id())!;
        await Should.ThrowAsync<InvalidDataException>(() => index.BeginDirectoryGraphAsync(Id(), Root, "TOPIC", Id(2), 12, Token));
        await Should.ThrowAsync<InvalidDataException>(() => index.BeginDirectoryGraphAsync(Id(), Root, "topic", Id(9), 12, Token));
        byte[] changedRoot = Root; changedRoot[0] = 1;
        await Should.ThrowAsync<InvalidOperationException>(() => index.BeginDirectoryGraphAsync(Id(7), changedRoot, "topic", Id(2), 12, Token));
        await Should.ThrowAsync<InvalidDataException>(() => index.CompleteDirectoryGraphWorkAsync(Id(), work.Id, [Child("other", 3)], cancellationToken: Token));
        await index.AppendDirectoryGraphReferencesAsync(Id(), [new(Id(2), 12)], Token);
        await Should.ThrowAsync<InvalidDataException>(() => index.AppendDirectoryGraphReferencesAsync(Id(), [new(Id(2), 48)], Token));
        index.ReadDirectoryGraphWork(Id())!.Id.ShouldBe(work.Id);
    }

    [Fact]
    public async Task CancelledWorkCommitKeepsItsParentPendingAndBoundedInputsAreFrozen()
    {
        await using var index = await Open(); await Begin(index); var work = index.ReadDirectoryGraphWork(Id())!;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.CompleteDirectoryGraphWorkAsync(Id(), work.Id, [Child("topic", 3)], cancellationToken: stop.Token));
        index.ReadDirectoryGraphWork(Id())!.Id.ShouldBe(work.Id);
        Should.Throw<ArgumentException>(() => index.CompleteDirectoryGraphWorkAsync(Id(), work.Id, Enumerable.Repeat(Child("topic", 3), 129).ToArray(), cancellationToken: Token));
        Should.Throw<ArgumentException>(() => index.AppendDirectoryGraphReferencesAsync(Id(), Enumerable.Repeat(new DirectoryGraphBlock(Id(2), 12), 1001).ToArray(), Token));
        work.Hash[0] = 99; index.ReadDirectoryGraphWork(Id())!.Hash.ShouldBe(Id(2));
        var id = Id(); var bytes = Id(2); var task = index.BeginDirectoryGraphAsync(id, Root, "topic", bytes, 12, Token); id[0] = 98; bytes[0] = 97;
        (await task).Hash.ShouldBe(Id(2)); index.ReadDirectoryGraph(Id())!.Hash.ShouldBe(Id(2));
    }
    private static DirectoryGraphChild Child(string path, byte hash) => new(path, Id(hash), 48, DirectoryGraphWorkKind.Record, 0);
    private void Age()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "UPDATE directory_graphs SET last_used_ticks=1;"; command.ExecuteNonQuery();
    }
    private static Task<DirectoryGraphState> Begin(FolderIndex index) => index.BeginDirectoryGraphAsync(Id(), Root, "topic", Id(2), 12, Token);
    private async Task<FolderIndex> Open()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); return index; } catch { await index.DisposeAsync(); throw; }
    }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-DirectoryGraphDb-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
