using System.Security.AccessControl;
using System.Security.Principal;
using System.IO;

namespace PeerSync.App.Dashboard;

internal sealed record FolderGrantResult(IReadOnlyList<string> ProtectedChildren, bool Truncated);

internal static class FolderAccessGrant
{
    internal static Task<FolderGrantResult> GrantAsync(string path, IProgress<string>? progress,
        CancellationToken cancellationToken)
        => Task.Run(() => Grant(path, progress, cancellationToken), cancellationToken);

    private static FolderGrantResult Grant(string path, IProgress<string>? progress, CancellationToken token)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Selected folder does not exist.");
        var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", "PeerSync")
            .Translate(typeof(SecurityIdentifier));
        var directory = new DirectoryInfo(root);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Choose a normal folder, not a link or reparse point.");
        var security = directory.GetAccessControl(AccessControlSections.Access);
        var rule = new FileSystemAccessRule(serviceSid, FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow);
        bool covered = security.GetAccessRules(includeExplicit: true, includeInherited: false,
            typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().Any(existing =>
                existing.IdentityReference.Equals(serviceSid) && existing.AccessControlType == AccessControlType.Allow
                && (existing.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify
                && (existing.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                    == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        if (!covered) { security.AddAccessRule(rule); directory.SetAccessControl(security); }
        progress?.Report("Access granted. Checking children with protected ACLs…");

        var protectedChildren = new List<string>();
        var pending = new Stack<string>(); pending.Push(root);
        bool truncated = false;
        int visited = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string parent = pending.Pop();
            string[] children;
            try { children = Directory.GetDirectories(parent); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            {
                if (protectedChildren.Count < 100) protectedChildren.Add(parent); else truncated = true;
                continue;
            }
            foreach (string child in children)
            {
                token.ThrowIfCancellationRequested();
                if (++visited % 500 == 0) progress?.Report($"Checking folder access: {visited} directories…");
                var info = new DirectoryInfo(child);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                bool childProtected;
                try { childProtected = info.GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected; }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException)
                { childProtected = true; }
                if (childProtected)
                {
                    if (protectedChildren.Count == 100) { truncated = true; continue; }
                    protectedChildren.Add(child);
                }
                else pending.Push(child);
            }
        }
        return new(protectedChildren, truncated);
    }
}
