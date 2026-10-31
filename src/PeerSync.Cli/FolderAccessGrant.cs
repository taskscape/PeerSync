using System.Security.AccessControl;
using System.Security.Principal;

namespace PeerSync.Cli;

internal static class FolderAccessGrant
{
    internal static void Grant(string path)
    {
        DirectoryInfo directory = ValidateFolder(path);
        var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", "PeerSync")
            .Translate(typeof(SecurityIdentifier));
        GrantForSid(directory.FullName, serviceSid);
    }

    internal static void GrantForSid(string path, SecurityIdentifier serviceSid)
    {
        DirectoryInfo directory = ValidateFolder(path);
        var security = directory.GetAccessControl(AccessControlSections.Access);
        bool covered = security.GetAccessRules(includeExplicit: true, includeInherited: false,
            typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().Any(existing =>
                existing.IdentityReference.Equals(serviceSid) && existing.AccessControlType == AccessControlType.Allow
                && (existing.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify
                && (existing.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                    == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        if (covered) return;

        security.AddAccessRule(new FileSystemAccessRule(serviceSid,
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static DirectoryInfo ValidateFolder(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var directory = new DirectoryInfo(root);
        if (!directory.Exists) throw new DirectoryNotFoundException("The selected folder does not exist.");
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Choose a normal folder, not a link or reparse point.");
        return directory;
    }
}
