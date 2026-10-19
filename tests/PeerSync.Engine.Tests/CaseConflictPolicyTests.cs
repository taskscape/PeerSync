using System.Buffers.Binary;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Signing;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class CaseConflictPolicyTests : IDisposable
{
    private readonly FolderKeys keys = FolderKeys.CreateNew();

    [Fact]
    public void WinnerAndPortableLoserIdentityDoNotDependOnArrivalOrForwarder()
    {
        var first = Version("file.txt", 1, 10); var second = Version("FILE.txt", 2, 20);
        var a = CaseConflictPolicy.SelectSources([first, second]).Single();
        var b = CaseConflictPolicy.SelectSources([second, first]).Single();
        a.SourceId.ShouldBe(b.SourceId); a.Record.Body.ToArray().ShouldBe(first.Record.Body.ToArray());
        var forwarded = VersionView.Open(first.Record.WithLocalSequence(999), keys, Device(17));
        CaseConflictPolicy.SourceId(forwarded).ShouldBe(a.SourceId);
        var path = ConflictNames.CaseSourcePath(first.Path, a.SourceId);
        path.ShouldContain(".case-conflict-"); path.ShouldEndWith(".txt"); PathRules.IsValid(path).ShouldBeTrue();
        ConflictNames.CaseSourcePath(forwarded.Path, b.SourceId).ShouldBe(path);
        ConflictNames.CaseSourcePath(first.Path, a.SourceId, 2).ShouldNotBe(path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CausalCaseRenameAndEditAreNotPreservationWork(bool edit)
    {
        var first = Version("file.txt", 1, 10);
        var body = first.ExportBody(); body.Path = "FILE.txt"; body.Vv[0].Counter++;
        if (edit) body.MtimeTicks++;
        var second = VersionView.Open(SignedRecordFactory.Create(body, keys), keys, Device(1));
        CaseConflictPolicy.SelectSources([first, second]).ShouldBeEmpty();
    }

    [Fact]
    public void SameLeafParentOnlyAndDominatedVersionsDoNotCreateCopies()
    {
        CaseConflictPolicy.SelectSources([Version("file.txt", 1, 10), Version("file.txt", 2, 20)]).ShouldBeEmpty();
        CaseConflictPolicy.SelectSources([Version("Parent/file.txt", 1, 10), Version("PARENT/file.txt", 2, 20)]).ShouldBeEmpty();
        var old = Version("file.txt", 1, 10); var body = old.ExportBody(); body.Path = "FILE.txt"; body.Vv[0].Counter++;
        var fresh = VersionView.Open(SignedRecordFactory.Create(body, keys), keys, Device(1));
        CaseConflictPolicy.SelectSources([old, fresh, Version("FILE.txt", 2, 20)]).ShouldBeEmpty();
    }

    [Fact]
    public void EqualPayloadsStillPreserveDistinctNamesAndForwardedDuplicatesCollapse()
    {
        var first = Version("file.txt", 1, 10); var second = Version("FILE.txt", 2, 20);
        CaseConflictPolicy.SelectSources([first, VersionView.Open(first.Record, keys, Device(9)), second]).Count.ShouldBe(1);
        CaseConflictPolicy.SelectSources([]).ShouldBeEmpty();
    }

    [Fact]
    public void NamesRespectComponentAndPathBoundsWithoutSplittingSurrogates()
    {
        var version = Version("file", 1, 10); byte[] id = CaseConflictPolicy.SourceId(version);
        string path = new string('x', 240) + "😀.txt";
        string result = ConflictNames.CaseSourcePath(path, id);
        result.Length.ShouldBeLessThanOrEqualTo(255); PathRules.IsValid(result).ShouldBeTrue();
        string parent = string.Join('/', Enumerable.Repeat(new string('p', 200), 19));
        result = ConflictNames.CaseSourcePath(parent + "/" + path, id);
        result.Length.ShouldBeLessThanOrEqualTo(4096); PathRules.IsValid(result).ShouldBeTrue();
        Should.Throw<ArgumentException>(() => ConflictNames.CaseSourcePath("file", new byte[31]));
    }

    private VersionView Version(string path, ulong author, long ticks)
    {
        var body = ConflictResolverTests.Body(author, 1, ticks, ticks, "same payload", false); body.Path = path;
        return VersionView.Open(SignedRecordFactory.Create(body, keys), keys, Device(author));
    }
    private static byte[] Device(ulong value) { byte[] device = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(device, value); return device; }
    public void Dispose() => keys.Dispose();
}
