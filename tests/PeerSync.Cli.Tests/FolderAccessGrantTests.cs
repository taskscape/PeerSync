using System.Security.AccessControl;
using System.Security.Principal;
using PeerSync.Cli;
using Shouldly;
using Xunit;

namespace PeerSync.Cli.Tests;

public sealed class FolderAccessGrantTests
{
    [Fact]
    public async Task JoinRejectsInvalidKeyBeforeChangingFolderAccess()
    {
        string path = Path.Combine(Path.GetTempPath(), "PeerSync-Cli-Invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            int exit = await FolderCommand.RunAsync("join", ["invalid-key", path], output, error,
                TestContext.Current.CancellationToken);
            exit.ShouldBe(1);
            error.ToString().ShouldContain("Invalid folder key");
            output.ToString().ShouldBeEmpty();
        }
        finally { Directory.Delete(path); }
    }

    [Fact]
    public void GrantTouchesOnlySelectedFolderAndIsIdempotent()
    {
        string parent = Path.Combine(Path.GetTempPath(), "PeerSync-Cli-Grant-" + Guid.NewGuid().ToString("N"));
        string selected = Path.Combine(parent, "selected");
        string sibling = Path.Combine(parent, "sibling");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(sibling);
        var testSid = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        try
        {
            FolderAccessGrant.GrantForSid(selected, testSid);
            FolderAccessGrant.GrantForSid(selected, testSid);

            var grants = ExplicitGrants(selected, testSid);
            grants.Count.ShouldBe(1);
            (grants[0].FileSystemRights & FileSystemRights.Modify).ShouldBe(FileSystemRights.Modify);
            (grants[0].InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                .ShouldBe(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
            ExplicitGrants(sibling, testSid).ShouldBeEmpty();
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static List<FileSystemAccessRule> ExplicitGrants(string path, SecurityIdentifier sid)
        => new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Allow)
            .ToList();
}
