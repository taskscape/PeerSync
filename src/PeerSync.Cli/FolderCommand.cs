using System.Text.Json;
using System.Security.Principal;
using Grpc.Core;
using PeerSync.Control.Contracts;
using PeerSync.Core.Keys;
using Wire = PeerSync.Control.Contracts.V1;

namespace PeerSync.Cli;

public static class FolderCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string command, string[] args, TextWriter? output = null,
        TextWriter? error = null, CancellationToken cancellationToken = default)
    {
        output ??= Console.Out; error ??= Console.Error;
        bool json = args.Length > 0 && args[^1] == "--json";
        string[] values = json ? args[..^1] : args;
        int expected = command switch { "add" => 1, "join" => 2, "hosts" => -2, "share" or "peers" or "dump-index" => 1, _ => -1 };
        bool clearHosts = command == "hosts" && values.Length == 2 && values[1] == "--clear";
        bool invalidClear = command == "hosts" && values.Skip(1).Any(value => value == "--clear") && !clearHosts;
        if ((expected >= 0 && values.Length != expected) || expected == -2 &&
            (values.Length < 2 || values.Skip(1).Any(string.IsNullOrWhiteSpace)) || expected == -1 || invalidClear)
        {
            await error.WriteLineAsync(command switch
            {
                "add" => "Usage: psp add <path> [--json]",
                "join" => "Usage: psp join <key> <path> [--json]",
                "hosts" => "Usage: psp hosts <folder> <host:port ...> [--json] | psp hosts <folder> --clear [--json]",
                "share" => "Usage: psp share <folder> [--json]",
                "peers" => "Usage: psp peers <folder> [--json]",
                "dump-index" => "Usage: psp dump-index <folder> [--json]",
                _ => "Unknown folder command.",
            }).ConfigureAwait(false);
            return 2;
        }

        try
        {
            using var client = ControlStatusClient.CreateForInstalledService();
            switch (command)
            {
                case "hosts":
                {
                    string[] hosts = clearHosts ? [] : values.Skip(1).ToArray();
                    Wire.SetPredefinedHostsResponse result = await client.SetPredefinedHostsAsync(values[0], hosts, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(json ? JsonSerializer.Serialize(new
                    { folderId = result.FolderId, hosts = result.Hosts }, JsonOptions)
                        : $"Configured {result.Hosts.Count} predefined host(s) for folder {result.FolderId}.").ConfigureAwait(false);
                    break;
                }
                case "add":
                {
                    if (!await GrantFolderAccessAsync(values[0], error).ConfigureAwait(false)) return 1;
                    Wire.FolderInfo folder = await client.AddFolderAsync(values[0], cancellationToken).ConfigureAwait(false);
                    Wire.GetFolderKeysResponse keys = await client.GetFolderKeysAsync(folder.FolderId, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(json ? JsonSerializer.Serialize(new
                    { folderId = folder.FolderId, path = folder.Path, access = folder.Access, paused = folder.Paused, shareKey = keys.Key }, JsonOptions)
                        : $"Added {folder.Path} ({folder.FolderId}, {folder.Access}){Environment.NewLine}Share key: {keys.Key}").ConfigureAwait(false);
                    break;
                }
                case "join":
                {
                    try { using var parsed = KeyText.Decode(values[0]); }
                    catch (Exception exception) when (exception is FormatException or ArgumentException)
                    {
                        await error.WriteLineAsync("Invalid folder key.").ConfigureAwait(false);
                        return 1;
                    }
                    if (!await GrantFolderAccessAsync(values[1], error).ConfigureAwait(false)) return 1;
                    Wire.FolderInfo folder = await client.JoinFolderAsync(values[0], values[1], cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(json ? JsonSerializer.Serialize(FolderDto(folder), JsonOptions)
                        : $"Joined {folder.Path} ({folder.FolderId}, {folder.Access})").ConfigureAwait(false);
                    break;
                }
                case "share":
                {
                    Wire.GetFolderKeysResponse keys = await client.GetFolderKeysAsync(values[0], cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(json ? JsonSerializer.Serialize(new
                    { folderId = keys.FolderId, access = keys.Access, shareKey = keys.Key }, JsonOptions) : keys.Key).ConfigureAwait(false);
                    break;
                }
                case "peers":
                {
                    Wire.ListPeersResponse peers = await client.ListPeersAsync(values[0], cancellationToken).ConfigureAwait(false);
                    object result = new { folderId = peers.FolderId, peers = peers.Peers.Select(PeerDto).ToArray() };
                    if (json) await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
                    else
                    {
                        await output.WriteLineAsync($"Peers for {peers.FolderId}:").ConfigureAwait(false);
                        foreach (Wire.PeerInfo peer in peers.Peers)
                            await output.WriteLineAsync($"{peer.DeviceId}  {peer.Approval} pinned={peer.Pinned} transport={peer.Transport}  {peer.Name}").ConfigureAwait(false);
                    }
                    break;
                }
                case "dump-index":
                {
                    bool first = true;
                    if (json) await output.WriteAsync("[").ConfigureAwait(false);
                    await foreach (Wire.IndexEntry row in client.DumpIndexAsync(values[0], cancellationToken).ConfigureAwait(false))
                    {
                        if (json)
                        {
                            if (!first) await output.WriteAsync(",").ConfigureAwait(false);
                            await output.WriteAsync(JsonSerializer.Serialize(new { path = row.Path, size = row.Size,
                                modifiedUtcTicks = row.ModifiedUtcTicks, deleted = row.Deleted })).ConfigureAwait(false);
                        }
                        else await output.WriteLineAsync($"{(row.Deleted ? "DELETED" : "FILE")}\t{row.Size}\t{row.ModifiedUtcTicks}\t{row.Path}").ConfigureAwait(false);
                        first = false;
                    }
                    if (json) await output.WriteLineAsync("]").ConfigureAwait(false);
                    break;
                }
            }
            return 0;
        }
        catch (RpcException exception)
        {
            string detail = exception.Status.Detail;
            await error.WriteLineAsync($"Service request failed: {exception.StatusCode}"
                + (string.IsNullOrWhiteSpace(detail) ? "" : $". {detail}")).ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or TimeoutException or System.Security.SecurityException)
        {
            await error.WriteLineAsync($"Service unavailable: {exception.GetType().Name}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<bool> GrantFolderAccessAsync(string path, TextWriter error)
    {
        try { FolderAccessGrant.Grant(path); return true; }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or
            System.Security.SecurityException or IdentityNotMappedException)
        {
            await error.WriteLineAsync($"Could not grant PeerSync service access to the selected folder: {exception.GetType().Name}")
                .ConfigureAwait(false);
            return false;
        }
    }

    private static object FolderDto(Wire.FolderInfo folder) => new
    { folderId = folder.FolderId, path = folder.Path, access = folder.Access, paused = folder.Paused };
    private static object PeerDto(Wire.PeerInfo peer) => new
    { deviceId = peer.DeviceId, name = peer.Name, approval = peer.Approval, pinned = peer.Pinned,
        transport = peer.Transport, lastSeenUtcTicks = peer.HasLastSeenUtcTicks ? peer.LastSeenUtcTicks : (long?)null };
}
