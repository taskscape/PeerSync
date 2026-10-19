using System.Security.AccessControl;
using System.Security.Principal;
using PeerSync.Engine.Native;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class FileMetadataTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-Metadata-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(fixture, "tree");
    public FileMetadataTests() => Directory.CreateDirectory(Root);

    [Fact]
    public void ReplacementPreparationPreservesProtectedDaclAndCreationTimeWithoutClosingOwnedHandles()
    {
        string original = Path.Combine(Root, "original"); File.WriteAllText(original, "original bytes");
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(original).SetAccessControl(security);
        DateTime creation = new(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetCreationTimeUtc(original, creation);
        string expected = new FileInfo(original).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using (var tree = new SafeFileTree(Root))
        using (var parent = tree.Directory(""))
        using (var source = parent.OpenFile("original", mutation: true))
        using (var temporary = parent.CreateFile("replacement.!psp", temporary: true))
        {
            FileMetadata.CopyReplacementMetadata(source, temporary);
            source.IsClosed.ShouldBeFalse(); temporary.IsClosed.ShouldBeFalse();
            FileMetadata.Read(temporary).CreationTime.ShouldBe(creation.ToFileTimeUtc());
        }
        var actual = new FileInfo(Path.Combine(Root, "replacement.!psp")).GetAccessControl();
        actual.AreAccessRulesProtected.ShouldBeTrue();
        actual.GetSecurityDescriptorSddlForm(AccessControlSections.Access).ShouldBe(expected);
        File.ReadAllText(original).ShouldBe("original bytes");
    }

    [Fact]
    public void ANewSiblingInheritsItsParentAndMtimeThenSynchronizedAttributesAreAppliedByHandle()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Root).SetAccessControl(security);
        }
        DateTime time = new(2024, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        using (var tree = new SafeFileTree(Root))
        using (var parent = tree.Directory(""))
        using (var temporary = parent.CreateFile("new.!psp", temporary: true))
        {
            FileMetadata.SetMtime(temporary, time.Ticks);
            FileMetadata.SetSyncedAttributes(temporary, (uint)(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
            var actual = FileMetadata.Read(temporary);
            actual.LastWriteTime.ShouldBe(time.ToFileTimeUtc());
            ((FileAttributes)actual.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden)).ShouldBe(FileAttributes.ReadOnly | FileAttributes.Hidden);
            ((FileAttributes)actual.Attributes & FileAttributes.System).ShouldBe((FileAttributes)0);
        }
        var acl = new FileInfo(Path.Combine(Root, "new.!psp")).GetAccessControl();
        acl.AreAccessRulesProtected.ShouldBeFalse();
        acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ShouldContain(r => r.IsInherited);
        File.GetLastWriteTimeUtc(Path.Combine(Root, "new.!psp")).ShouldBe(time);
    }

    [Fact]
    public void ReadOnlyOriginalCanBeGuardedClearedMovedAndRestoredWithoutChangingItsBytes()
    {
        string path = Path.Combine(Root, "original"); File.WriteAllText(path, "preserved bytes");
        File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Archive);
        using (var tree = new SafeFileTree(Root))
        using (var parent = tree.Directory(""))
        using (var archive = tree.Directory(".psp/archive", create: true, privatePath: true))
        using (var original = parent.OpenFile("original", mutation: true))
        {
            FileAttributes attributes = FileMetadata.ClearReadOnly(original);
            (attributes & FileAttributes.ReadOnly).ShouldNotBe((FileAttributes)0);
            archive.MoveHere(original, "preserved");
            FileMetadata.RestoreAttributes(original, attributes);
        }
        string archived = Path.Combine(Root, ".psp", "archive", "preserved");
        File.ReadAllText(archived).ShouldBe("preserved bytes");
        File.GetAttributes(archived).ShouldBe(FileAttributes.ReadOnly | FileAttributes.Archive);
    }

    [Fact]
    public void UnrepresentableTimestampsFailBeforeChangingAnyMetadata()
    {
        using var tree = new SafeFileTree(Root); using var parent = tree.Directory(""); using var file = parent.CreateFile("file");
        var before = FileMetadata.Read(file);
        foreach (long ticks in new[] { -1L, 0L, new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, long.MaxValue })
            Should.Throw<ArgumentOutOfRangeException>(() => FileMetadata.SetMtime(file, ticks));
        var after = FileMetadata.Read(file);
        after.LastWriteTime.ShouldBe(before.LastWriteTime); after.Attributes.ShouldBe(before.Attributes);
    }

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(fixture, recursive: true);
    }
}
