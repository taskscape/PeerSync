using System.Security.Cryptography;
using PeerSync.Engine.Native;
using PeerSync.Engine.Hashing;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class NativeRootTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PeerSync-Native-" + Guid.NewGuid().ToString("N"));
    public NativeRootTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task MetadataAndFileIdsMatchActualFilesAndRelativeReadsWork()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(131);
        await File.WriteAllBytesAsync(Path.Combine(root, "é.txt"), bytes, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(root, "dir"));
        using var native = new NativeRoot(root);
        var entries = native.Enumerate("", TestContext.Current.CancellationToken).ToDictionary(e => e.Name);
        entries.Count.ShouldBe(2); entries["é.txt"].Length.ShouldBe(131);
        entries["é.txt"].MtimeTicks.ShouldBe(File.GetLastWriteTimeUtc(Path.Combine(root, "é.txt")).Ticks);
        entries["dir"].IsDirectory.ShouldBeTrue();
        using var handle = native.OpenFile("é.txt");
        using var stream = new FileStream(handle, FileAccess.Read, 0, isAsync: false);
        byte[] actual = new byte[bytes.Length];
        await stream.ReadExactlyAsync(actual, TestContext.Current.CancellationToken);
        actual.ShouldBe(bytes);
        var hashed = await FileHasher.HashRelativeAsync(native, "é.txt", cancellationToken: TestContext.Current.CancellationToken);
        hashed.ShouldNotBeNull();
        hashed.FileId.ShouldBe(entries["é.txt"].FileId);
        hashed.Volume.ShouldBe(native.Volume);
        hashed.Attributes.ShouldBe(entries["é.txt"].Attributes);
    }

    [Fact]
    public void DescendantReparsePointsAreListedButCannotBeTraversed()
    {
        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "private test fixture");
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        using var native = new NativeRoot(root);
        native.Enumerate("", TestContext.Current.CancellationToken).Single(e => e.Name == "link").IsReparse.ShouldBeTrue();
        Should.Throw<System.ComponentModel.Win32Exception>(() => native.OpenFile("link/secret.txt"));
        Should.Throw<IOException>(() => native.Enumerate("link", TestContext.Current.CancellationToken).ToArray());
        Should.Throw<IOException>(() => native.OpenFile("../escape"));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
