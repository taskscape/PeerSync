using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class DirectoryCaptureTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-DirectoryCaptureDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Root => new byte[24];
    private static byte[] Native => new byte[16];
    private static byte[] Input => new byte[32];
    private static byte[] Initial => [0x50, 0x53, 0x44, 0x42, 1, .. new byte[11]];
    public DirectoryCaptureTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task DurablePrefixAndFinalUnpublishedRootSurviveReopening()
    {
        await using (var index = await Open())
        {
            var draft = await Begin(index);
            await index.AdvanceDirectoryCaptureAsync(draft, 1, "topic/a", Initial, null, null, Token);
            index.HasDirectoryCaptures().ShouldBeTrue();
        }
        await using var reopened = await Open(); var before = reopened.ReadDirectoryCapture("TOPIC")!;
        before.Accepted.ShouldBe(1); before.LastKey.ShouldBe("TOPIC/A"); before.BuilderState.ShouldBe(Initial);
        var completed = await reopened.AdvanceDirectoryCaptureAsync(before, 2, "topic/b", null, new byte[32], 12, Token);
        completed.Accepted.ShouldBe(2); completed.BuilderState.ShouldBeNull(); completed.Length.ShouldBe(12);
        reopened.HasDirectoryCaptures().ShouldBeTrue("completion is not publication");
        await Should.ThrowAsync<InvalidOperationException>(() => reopened.AdvanceDirectoryCaptureAsync(completed, 2, "topic/b", null, new byte[32], 12, Token));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("input")]
    [InlineData("spelling")]
    public async Task ChangedInputOrParentIdentityResetsOnlyThatDraftAndRejectsStaleProgress(string change)
    {
        await using var index = await Open(); var draft = await Begin(index);
        var prior = await index.AdvanceDirectoryCaptureAsync(draft, 1, "topic/a", Initial, null, null, Token);
        byte[] native = Native, input = Input; string path = "topic";
        if (change == "native") native[0] = 1; if (change == "input") input[0] = 1; if (change == "spelling") path = "TOPIC";
        var reset = await index.BeginDirectoryCaptureAsync(path, Root, native, input, 2, Initial, Token);
        reset.Accepted.ShouldBe(0); reset.LastKey.ShouldBeNull(); reset.ParentPath.ShouldBe(path);
        await Should.ThrowAsync<InvalidOperationException>(() => index.AdvanceDirectoryCaptureAsync(prior, 2, "topic/b", null, new byte[32], 12, Token));
        index.ReadDirectoryCapture(path)!.Accepted.ShouldBe(0);
    }

    [Fact]
    public async Task CompareAndSwapCancellationAndInvalidCursorsPreserveLastProgress()
    {
        await using var index = await Open(); var draft = await Begin(index);
        var first = await index.AdvanceDirectoryCaptureAsync(draft, 1, "topic/a", Initial, null, null, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.AdvanceDirectoryCaptureAsync(draft, 2, "topic/b", null, Input, 12, Token));
        Should.Throw<ArgumentException>(() => index.AdvanceDirectoryCaptureAsync(first, 1, "topic/b", Initial, null, null, Token));
        Should.Throw<ArgumentException>(() => index.AdvanceDirectoryCaptureAsync(first, 2, "other/b", null, Input, 12, Token));
        Should.Throw<ArgumentException>(() => index.AdvanceDirectoryCaptureAsync(first, 2, "topic/a", null, Input, 12, Token));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.AdvanceDirectoryCaptureAsync(first, 2, "topic/b", null, Input, 12, stop.Token));
        index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(1);
    }

    [Fact]
    public async Task BuffersAreOwnedAndFreshOrForeignRootDraftsCannotExpire()
    {
        await using var index = await Open(); byte[] native = Native, input = Input, initial = Initial;
        var starting = index.BeginDirectoryCaptureAsync("topic", Root, native, input, 2, initial, Token);
        native[0] = 7; input[0] = 8; initial[0] = 9; var draft = await starting;
        draft.NativeId.ShouldBe(Native); draft.InputHash.ShouldBe(Input); draft.BuilderState.ShouldBe(Initial);
        draft.NativeId[0] = 99; index.ReadDirectoryCapture("topic")!.NativeId.ShouldBe(Native);
        (await index.PruneDirectoryCapturesAsync(Root, DateTime.UtcNow.Ticks, cancellationToken: Token)).ShouldBe(0);
        byte[] foreign = Root; foreign[0] = 1;
        await Should.ThrowAsync<InvalidOperationException>(() => index.PruneDirectoryCapturesAsync(foreign, DateTime.UtcNow.Ticks, cancellationToken: Token));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "UPDATE directory_captures SET last_used_ticks=1;"; command.ExecuteNonQuery();
        (await index.PruneDirectoryCapturesAsync(Root, DateTime.UtcNow.Ticks, 1, Token)).ShouldBe(1); index.HasDirectoryCaptures().ShouldBeFalse();
    }
    private static Task<DirectoryCaptureState> Begin(FolderIndex index) => index.BeginDirectoryCaptureAsync("topic", Root, Native, Input, 2, Initial, Token);
    private async Task<FolderIndex> Open()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); return index; } catch { await index.DisposeAsync(); throw; }
    }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-DirectoryCaptureDb-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
