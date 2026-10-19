using System.ComponentModel;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class SafeFileTreeTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-SafeTree-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(fixture, "tree");
    public SafeFileTreeTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task ExclusiveCreatesAndHandleMovesPreserveIdentityAndNeverOverwriteAnOccupiedName()
    {
        using var tree = new SafeFileTree(Root);
        using var directory = tree.Directory("a/b", create: true);
        using var archive = tree.Directory(".psp/archive/a", create: true, privatePath: true);
        byte[] data = "verified fixture data"u8.ToArray();
        using (var file = directory.CreateFile("transfer.!psp", temporary: true))
        {
            await using var stream = new FileStream(file, FileAccess.ReadWrite, 0, isAsync: false);
            await stream.WriteAsync(data, TestContext.Current.CancellationToken);
            stream.Flush(flushToDisk: true);
            var before = FileSnapshot.Read(file);
            directory.MoveHere(file, "target.txt");
            FileSnapshot.Read(file).IdLow.ShouldBe(before.IdLow);
            archive.MoveHere(file, "target~20261019-120000.txt");
            FileSnapshot.Read(file).IdLow.ShouldBe(before.IdLow);
        }
        File.ReadAllBytes(Path.Combine(Root, ".psp", "archive", "a", "target~20261019-120000.txt")).ShouldBe(data);
        File.Exists(Path.Combine(Root, "a", "b", "target.txt")).ShouldBeFalse();
        using (var existing = directory.CreateFile("occupied")) { }
        using (var other = directory.CreateFile("other"))
        {
            Should.Throw<Win32Exception>(() => directory.MoveHere(other, "occupied"));
        }
        File.Exists(Path.Combine(Root, "a", "b", "other")).ShouldBeTrue();
        Should.Throw<Win32Exception>(() => directory.CreateFile("occupied"));
    }

    [Fact]
    public void DirectoryLeasesPinEveryAncestorAndReleaseThemWhenDisposed()
    {
        using var tree = new SafeFileTree(Root);
        using (var child = tree.Directory("a/b", create: true))
            Should.Throw<IOException>(() => Directory.Move(Path.Combine(Root, "a"), Path.Combine(Root, "moved")));
        Directory.Move(Path.Combine(Root, "a"), Path.Combine(Root, "moved"));
        Should.Throw<IOException>(() => Directory.Move(Root, Path.Combine(fixture, "moved-root")));
    }

    [Fact]
    public void MutationGuardExcludesNewWritersAndDeletersButCanArchiveItsOwnHandle()
    {
        File.WriteAllText(Path.Combine(Root, "file"), "original");
        using var tree = new SafeFileTree(Root);
        using var parent = tree.Directory("");
        using var archive = tree.Directory(".psp/archive", create: true, privatePath: true);
        using (var file = parent.OpenFile("file", mutation: true))
        {
            Should.Throw<IOException>(() => File.OpenWrite(Path.Combine(Root, "file")));
            Should.Throw<IOException>(() => File.Delete(Path.Combine(Root, "file")));
            archive.MoveHere(file, "file~20261019-120000");
        }
        File.ReadAllText(Path.Combine(Root, ".psp", "archive", "file~20261019-120000")).ShouldBe("original");
        File.Exists(Path.Combine(Root, "file")).ShouldBeFalse();
    }

    [Fact]
    public void ReparseParentsAndInvalidComponentsAreRejectedBeforeAnyWrite()
    {
        string outside = Path.Combine(fixture, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep"), "unchanged");
        Directory.CreateSymbolicLink(Path.Combine(Root, "link"), outside);
        using var tree = new SafeFileTree(Root);
        Should.Throw<IOException>(() => tree.Directory("link", create: true));
        Should.Throw<IOException>(() => tree.ProbeDirectory("link/child"));
        Should.Throw<IOException>(() => tree.ProbeDirectory("../outside"));
        Should.Throw<IOException>(() => tree.Directory("../outside", create: true));
        Should.Throw<IOException>(() => tree.Directory(".psp", create: true));
        using var parent = tree.Directory("");
        Should.Throw<IOException>(() => parent.CreateFile("../escape"));
        Should.Throw<IOException>(() => parent.CreateFile("file.!psp"));
        Should.Throw<IOException>(() => parent.CreateFile(new string('a', 251) + ".!psp", temporary: true));
        string tooLong = string.Join('/', Enumerable.Repeat(new string('a', 255), 17));
        Should.Throw<IOException>(() => tree.Directory(tooLong, create: true));
        Directory.Exists(Path.Combine(Root, new string('a', 255))).ShouldBeFalse();
        File.ReadAllText(Path.Combine(outside, "keep")).ShouldBe("unchanged");
        Directory.EnumerateFiles(outside).Count().ShouldBe(1);
    }

    [Fact]
    public void AbsenceProbePinsFileAncestorsAndNeverCreatesDirectories()
    {
        using var tree=new SafeFileTree(Root);
        using(var missing=tree.ProbeDirectory("missing/child")) missing.Absent.ShouldBeTrue();
        Directory.Exists(Path.Combine(Root,"missing")).ShouldBeFalse();
        Directory.CreateDirectory(Path.Combine(Root,"present")); File.WriteAllText(Path.Combine(Root,"present","file"),"keep bytes");
        using(var blocked=tree.ProbeDirectory("present/file/child"))
        {
            blocked.Absent.ShouldBeTrue();
            Should.Throw<IOException>(()=>File.Delete(Path.Combine(Root,"present","file")));
            Should.Throw<IOException>(()=>Directory.Move(Path.Combine(Root,"present"),Path.Combine(Root,"moved")));
        }
        File.Move(Path.Combine(Root,"present","file"),Path.Combine(Root,"present","renamed"));
        using var present=tree.ProbeDirectory("present"); present.Absent.ShouldBeFalse(); present.Directory.ShouldNotBeNull();
    }

    [Fact]
    public void SharingFailureIsNotAbsence()
    {
        File.WriteAllText(Path.Combine(Root,"locked"),"keep"); using var locked=File.Open(Path.Combine(Root,"locked"),FileMode.Open,FileAccess.Read,FileShare.None);
        using var tree=new SafeFileTree(Root); var error=Should.Throw<Win32Exception>(()=>tree.ProbeDirectory("locked/child"));
        (error.NativeErrorCode is 32 or 33).ShouldBeTrue();
    }

    public void Dispose() => Directory.Delete(fixture, recursive: true);
}
