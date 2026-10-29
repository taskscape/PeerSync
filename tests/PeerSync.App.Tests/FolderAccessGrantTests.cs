using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using PeerSync.App.Dashboard;
using Shouldly;
using Xunit;

namespace PeerSync.App.Tests;

public sealed class FolderAccessGrantTests
{
    [Fact]
    public async Task GrantReportsProtectedFilesAndDirectoriesWithoutTouchingTheirAcls()
    {
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-App-Grant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string protectedFile = Path.Combine(root, "private.txt");
        string ordinaryFile = Path.Combine(root, "ordinary.txt");
        string protectedDirectory = Path.Combine(root, "private-dir");
        File.WriteAllText(protectedFile, "private");
        File.WriteAllText(ordinaryFile, "ordinary");
        Directory.CreateDirectory(protectedDirectory);
        try
        {
            var file = new FileInfo(protectedFile);
            FileSecurity fileAcl = file.GetAccessControl(AccessControlSections.Access);
            fileAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            file.SetAccessControl(fileAcl);

            var directory = new DirectoryInfo(protectedDirectory);
            DirectorySecurity directoryAcl = directory.GetAccessControl(AccessControlSections.Access);
            directoryAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            directory.SetAccessControl(directoryAcl);

            var serviceSid = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
            FolderGrantResult result = await FolderAccessGrant.GrantForSidAsync(root, serviceSid, null,
                TestContext.Current.CancellationToken);

            result.ProtectedChildren.ShouldContain(protectedFile);
            result.ProtectedChildren.ShouldContain(protectedDirectory);
            result.ProtectedChildren.ShouldNotContain(ordinaryFile);
            result.Truncated.ShouldBeFalse();
            file.GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected.ShouldBeTrue();
            directory.GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected.ShouldBeTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
