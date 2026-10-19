using System.Security.Cryptography;
using PeerSync.Engine.Hashing;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class FileHasherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PeerSync-Hasher-" + Guid.NewGuid().ToString("N"));

    public FileHasherTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(262144)]
    [InlineData(3145745)]
    public async Task SinglePassProducesEveryChunkHashAndWholeHash(int length)
    {
        byte[] data = RandomNumberGenerator.GetBytes(length);
        string path = Path.Combine(root, "file.bin");
        await File.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);
        var progress = new List<FileHashProgress>();
        var result = await FileHasher.HashAsync(path, new InlineProgress(progress.Add), TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();
        result.Length.ShouldBe(length);
        result.ChunkClass.ShouldBe(0u);
        var expectedWhole = Blake3.Hasher.Hash(data);
        result.WholeHash.ShouldBe(expectedWhole.AsSpan().ToArray());
        long offset = 0;
        foreach (var chunk in result.Chunks)
        {
            chunk.Offset.ShouldBe(offset);
            chunk.Length.ShouldBeGreaterThan(0);
            var digest = Blake3.Hasher.Hash(data.AsSpan((int)offset, chunk.Length));
            chunk.Hash.ShouldBe(digest.AsSpan().ToArray());
            offset += chunk.Length;
        }
        offset.ShouldBe(length);
        result.MtimeUtc.ShouldBe(File.GetLastWriteTimeUtc(path));
        progress.Select(p => p.BytesHashed).ShouldBe(progress.Select(p => p.BytesHashed).Order());
        progress.Last().BytesHashed.ShouldBe(length);
        progress.Last().ChunksHashed.ShouldBe(result.Chunks.Count);
    }

    [Fact]
    public async Task SharedWriterIsAllowedAndExclusiveWriterPropagatesAnIoError()
    {
        string path = await NewFile();
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            (await FileHasher.HashAsync(path, cancellationToken: TestContext.Current.CancellationToken)).ShouldNotBeNull();
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Should.ThrowAsync<IOException>(() => FileHasher.HashAsync(path, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("overwrite")]
    [InlineData("truncate")]
    [InlineData("append")]
    [InlineData("replace")]
    [InlineData("rename")]
    [InlineData("delete")]
    public async Task MidReadChangesReturnRetryAndDoNotReadPastTheOriginalLength(string change)
    {
        string path = await NewFile();
        const long originalLength = 2 * 1024 * 1024;
        DateTime mtime = File.GetLastWriteTimeUtc(path);
        bool changed = false;
        long maximumRead = 0;
        var progress = new InlineProgress(update =>
        {
            maximumRead = Math.Max(maximumRead, update.BytesHashed);
            if (changed || update.BytesHashed == 0) return;
            changed = true;
            switch (change)
            {
                case "overwrite":
                    using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        writer.WriteByte(0x5a);
                    // Same length and mtime: change metadata must still detect it.
                    File.SetLastWriteTimeUtc(path, mtime);
                    break;
                case "truncate":
                    using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        writer.SetLength(1);
                    break;
                case "append":
                    using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        writer.Write(new byte[1024 * 1024]);
                    break;
                case "replace":
                    File.Move(path, Path.Combine(root, "old.bin"));
                    File.WriteAllBytes(path, new byte[originalLength]);
                    File.SetLastWriteTimeUtc(path, mtime);
                    break;
                case "rename": File.Move(path, Path.Combine(root, "renamed.bin")); break;
                case "delete": File.Delete(path); break;
            }
        });
        var result = await FileHasher.HashAsync(path, progress, TestContext.Current.CancellationToken);
        changed.ShouldBeTrue();
        result.ShouldBeNull();
        maximumRead.ShouldBeLessThanOrEqualTo(originalLength);
    }

    [Fact]
    public async Task CancellationReleasesTheFileAndPropagatesCancellation()
    {
        string path = await NewFile();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<OperationCanceledException>(() => FileHasher.HashAsync(path,
            new InlineProgress(p => { if (p.BytesHashed > 0) cancellation.Cancel(); }), cancellation.Token));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        exclusive.Length.ShouldBe(2 * 1024 * 1024);
    }

    [Fact]
    public async Task ReparseFileIsRejectedRatherThanHashingItsTarget()
    {
        string target = await NewFile();
        string link = Path.Combine(root, "link.bin");
        File.CreateSymbolicLink(link, target);
        await Should.ThrowAsync<IOException>(() => FileHasher.HashAsync(link, cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<string> NewFile()
    {
        string path = Path.Combine(root, "file.bin");
        await File.WriteAllBytesAsync(path, new byte[2 * 1024 * 1024], TestContext.Current.CancellationToken);
        return path;
    }

    public void Dispose()
    {
        // Only this fixture's exact newly created directory, never a caller-supplied path.
        Directory.Delete(root, recursive: true);
    }

    private sealed class InlineProgress(Action<FileHashProgress> report) : IProgress<FileHashProgress>
    {
        public void Report(FileHashProgress value) => report(value);
    }
}
