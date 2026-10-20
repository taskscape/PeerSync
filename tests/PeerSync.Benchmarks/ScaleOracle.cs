using System.ComponentModel;
using System.Security.Cryptography;
using PeerSync.Core.Paths;
using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Benchmarks;

internal static class ScaleOracle
{
    internal const string DirectoryMarker = "<directory>";

    internal static SortedDictionary<string, string> Snapshot(string root, CancellationToken token)
    {
        using var tree = new SafeFileTree(root);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push("");
        while (pending.TryPop(out string? parent))
        {
            using var directory = tree.Directory(parent);
            foreach (var entry in directory.Entries(token))
            {
                token.ThrowIfCancellationRequested();
                string path = parent.Length == 0 ? entry.Name : parent + '/' + entry.Name;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || !PathRules.IsValid(path)) continue;
                if (entry.IsDirectory) { result.Add(path, DirectoryMarker); pending.Push(path); }
                else
                {
                    using var file = directory.OpenFile(entry.Name); using var stream = new FileStream(file, FileAccess.Read);
                    result.Add(path, Convert.ToHexString(SHA256.HashData(stream)));
                }
            }
        }
        return result;
    }

    internal static void AddArchives(string root, FolderIndex index, HashSet<string> surviving, CancellationToken token)
    {
        using var tree = new SafeFileTree(root);
        foreach (var archive in index.Archive.ReadAll())
        {
            token.ThrowIfCancellationRequested();
            string relative = archive.ArchivePath; int separator = relative.LastIndexOf('/');
            string parent = separator < 0 ? "" : relative[..separator], leaf = relative[(separator + 1)..];
            try
            {
                using var directory = tree.Directory(parent, privatePath: true); using var file = directory.OpenFile(leaf);
                using var stream = new FileStream(file, FileAccess.Read); surviving.Add(Convert.ToHexString(SHA256.HashData(stream)));
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3 or 267) { }
        }
    }

    internal static void RequireSurvival(IEnumerable<string> expected, HashSet<string> surviving)
    {
        int missing = expected.Count(hash => !surviving.Contains(hash));
        if (missing != 0) throw new InvalidDataException($"Published payload versions absent from visible files/indexed native archives: {missing}.");
    }

    internal static void SelfCheck(string fixture, CancellationToken token)
    {
        string root = Path.Combine(fixture, "oracle"); Directory.CreateDirectory(Path.Combine(root, "nested"));
        byte[] bytes = "PeerSync generated scale oracle control"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string path = Path.Combine(root, "nested", "file"); File.WriteAllBytes(path, bytes);
        RequireSurvival([hash], Snapshot(root, token).Values.ToHashSet(StringComparer.Ordinal));
        File.Delete(path); MustReject();
        Directory.CreateDirectory(Path.Combine(root, ".psp")); File.WriteAllBytes(Path.Combine(root, ".psp", "unindexed"), bytes); MustReject();
        string outside = Path.Combine(fixture, "outside"); File.WriteAllBytes(outside, bytes);
        string link = Path.Combine(root, "alias"); File.CreateSymbolicLink(link, outside);
        try { MustReject(); }
        finally { File.Delete(link); }

        void MustReject()
        {
            try { RequireSurvival([hash], Snapshot(root, token).Values.ToHashSet(StringComparer.Ordinal)); }
            catch (InvalidDataException) { return; }
            throw new InvalidDataException("Scale loss oracle accepted missing/private/reparse-only bytes.");
        }
    }
}
