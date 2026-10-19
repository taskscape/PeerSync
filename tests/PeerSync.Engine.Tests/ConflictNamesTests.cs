using PeerSync.Core.Paths;
using PeerSync.Engine.Sync;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class ConflictNamesTests
{
    [Fact]
    public void CopyNamesKeepDirectoryExtensionUtcTimeDeviceCodeAndCollisionSuffix()
    {
        byte[] device = new byte[32]; device[0] = 0xab; device[1] = 0xcd;
        var time = new DateTimeOffset(2026, 10, 19, 12, 34, 56, TimeSpan.FromHours(2));
        ConflictNames.CopyPath("dir/name.ext", time, device).ShouldBe("dir/name.sync-conflict-20261019-103456-ABCD.ext");
        ConflictNames.CopyPath("dir/name.ext", time, device, 2).ShouldBe("dir/name.sync-conflict-20261019-103456-ABCD-2.ext");
        ConflictNames.CasePath("dir/name.ext").ShouldBe("dir/name.case-conflict.ext");
        ConflictNames.CasePath("dir/name.ext", 2).ShouldBe("dir/name.case-conflict-2.ext");
        ConflictNames.CasePath(".hidden").ShouldBe(".hidden.case-conflict");
    }

    [Fact]
    public void LongUnicodeNamesAreBoundedWithoutSplittingSurrogatesOrCreatingInvalidPaths()
    {
        string name = string.Concat(Enumerable.Repeat("😀", 124)) + ".txt";
        string result = ConflictNames.CopyPath("dir/" + name, DateTimeOffset.UtcNow, new byte[32]);
        PathRules.IsValid(result).ShouldBeTrue(); result[4..].Length.ShouldBeLessThanOrEqualTo(255);
        result.ShouldEndWith(".txt");
        Should.Throw<ArgumentException>(() => ConflictNames.CasePath("../escape"));
        Should.Throw<ArgumentOutOfRangeException>(() => ConflictNames.CasePath("name", 0));
    }

    [Fact]
    public void ExhaustedPathBudgetFailsBeforeTheApplierCanReplaceAnyData()
    {
        string path = string.Join('/', Enumerable.Repeat(new string('a', 254), 16)) + "/file";
        PathRules.IsValid(path).ShouldBeTrue();
        Should.Throw<PathTooLongException>(() => ConflictNames.CopyPath(path, DateTimeOffset.UtcNow, new byte[32]));
    }
}
