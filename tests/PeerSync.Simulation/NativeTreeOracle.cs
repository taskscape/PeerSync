using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using PeerSync.Core.Paths;
using PeerSync.Crypto;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using Xunit;

namespace PeerSync.Simulation;

public sealed partial class RealEngineProperties
{
    private const string DirectoryMarker = "<directory>";
    private static SortedDictionary<string, string> VisibleTree(string root)
    {
        using var tree = new SafeFileTree(root); var pending = new Stack<string>(); pending.Push("");
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        while (pending.TryPop(out string? parent))
        {
            using var directory = tree.Directory(parent);
            foreach (var entry in directory.Entries(Token))
            {
                Token.ThrowIfCancellationRequested(); string relative = parent.Length == 0 ? entry.Name : parent + '/' + entry.Name;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || !PathRules.IsValid(relative)) continue;
                if (entry.IsDirectory) { result.Add(relative, DirectoryMarker); pending.Push(relative); }
                else
                {
                    using var file = directory.OpenFile(entry.Name); using var stream = new FileStream(file, FileAccess.Read);
                    result.Add(relative, Convert.ToHexString(SHA256.HashData(stream)));
                }
            }
        }
        return result;
    }
    private static string? ReadGuardedHash(string root, string relative, bool privatePath)
    {
        int separator = relative.LastIndexOf('/'); string parent = separator < 0 ? "" : relative[..separator], name = relative[(separator + 1)..];
        try
        {
            using var tree = new SafeFileTree(root); using var directory = tree.Directory(parent, privatePath: privatePath);
            using var file = directory.OpenFile(name); using var stream = new FileStream(file, FileAccess.Read);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3 or 267) { return null; }
    }

    [Fact]
    public async Task RecursiveLossOracleFindsNestedUserBytesButRejectsPrivateCachesUntilArchived()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-NestedLossOracle-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "tree"); Directory.CreateDirectory(Path.Combine(root, "nested", "empty"));
        byte[] bytes = Encoding.UTF8.GetBytes("nested generated version"); string hash = Convert.ToHexString(SHA256.HashData(bytes));
        using var keys = FolderKeys.CreateNew();
        try
        {
            await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
            await using var engine = new TransportFolder(root, index, keys, new byte[32]);
            string path = Path.Combine(root, "nested", "file"); File.WriteAllBytes(path, bytes); await engine.RunPassAsync(Token);
            Assert.Equal(DirectoryMarker, VisibleTree(root)["nested/empty"]); RequireVersions([hash], [(root, index)]);
            File.Delete(path); Assert.Throws<InvalidDataException>(() => RequireVersions([hash], [(root, index)]));
            engine.RequestScan(); await engine.RunPassAsync(Token); RequireVersions([hash], [(root, index)]);
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Fact]
    public async Task RecursiveOracleNeverCountsAnUnindexedPrivateCopyOrAReparseFile()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-OracleReparse-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "tree"); Directory.CreateDirectory(Path.Combine(root, ".psp"));
        byte[] bytes = "generated outside-root copy"u8.ToArray(); string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string outside = Path.Combine(fixture, "outside"); File.WriteAllBytes(outside, bytes);
        string link = Path.Combine(root, "linked-file");
        try
        {
            File.WriteAllBytes(Path.Combine(root, ".psp", "unindexed"), bytes); File.CreateSymbolicLink(link, outside);
            await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
            Assert.Empty(VisibleTree(root)); Assert.Throws<InvalidDataException>(() => RequireVersions([hash], [(root, index)]));
        }
        finally { File.Delete(link); Directory.Delete(fixture, true); }
    }
}
