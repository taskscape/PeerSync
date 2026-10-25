using System.Security.Cryptography;
using System.Text.Json;
using Grpc.Core;
using PeerSync.Control.Contracts;
using PeerSync.Core.Settings;
using Wire = PeerSync.Control.Contracts.V1;

if (args.Length is < 2 or > 4 || args[0] switch
    {
        "inspect" => args.Length != 2,
        "set-approval" => args.Length != 3,
        "decide" => args.Length != 4,
        _ => true,
    })
{
    Console.Error.WriteLine("Usage: PeerSync.ApprovalField inspect <folder> | set-approval <folder> true|false | decide <folder> <64-hex-device-id> approve|deny|block");
    return 2;
}

try
{
    using var client = ControlStatusClient.CreateForInstalledService();
    string folder = args[1];
    if (args[0] == "set-approval")
    {
        if (!bool.TryParse(args[2], out bool required)) throw new ArgumentException("Expected true or false.");
        Wire.SettingsResponse current = await client.GetFolderSettingsAsync(folder).ConfigureAwait(false);
        var updated = FolderSettings.FromJson(current.Json) with { RequireApproval = required };
        Wire.SettingsResponse result = await client.UpdateFolderSettingsAsync(folder, updated.ToJson()).ConfigureAwait(false);
        if (FolderSettings.FromJson(result.Json).RequireApproval != required)
            throw new InvalidDataException("Approval setting did not persist.");
        Console.WriteLine(JsonSerializer.Serialize(new { requireApproval = required, result.RestartRequired }));
        return 0;
    }

    if (args[0] == "decide")
    {
        string deviceId = args[2];
        if (deviceId.Length != 64 || !deviceId.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected a 64-digit hexadecimal device ID.");
        string decision = args[3];
        if (decision is not ("approve" or "deny" or "block"))
            throw new ArgumentException("Expected approve, deny or block.");
        Wire.OperationResult result = await client.DecideDeviceAsync(folder, deviceId, decision).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new { result.Completed, decision, deviceId }));
        return result.Completed ? 0 : 1;
    }

    Wire.SettingsResponse settings = await client.GetFolderSettingsAsync(folder).ConfigureAwait(false);
    Wire.ListPeersResponse peers = await client.ListPeersAsync(folder).ConfigureAwait(false);
    Wire.ListPendingDevicesResponse pending = await client.ListPendingDevicesAsync().ConfigureAwait(false);
    ControlStatusSnapshot status = await client.GetStatusAsync().ConfigureAwait(false);
    byte[] folderId = Convert.FromHexString(peers.FolderId);
    string fingerprint = Convert.ToHexString(SHA256.HashData(folderId))[..16];
    var payload = new
    {
        folderFingerprint = fingerprint,
        requireApproval = FolderSettings.FromJson(settings.Json).RequireApproval,
        pendingTruncated = pending.Truncated,
        pending = pending.Devices.Where(row => row.FolderId == peers.FolderId)
            .Select(row => new { deviceId = row.DeviceId, row.Name, row.RequestedUtcTicks }).ToArray(),
        peers = peers.Peers.Select(row => new { deviceId = row.DeviceId, row.Approval, row.Pinned, row.Transport }).ToArray(),
        sessions = status.Clocks.Where(row => row.FolderId.AsSpan().SequenceEqual(folderId))
            .Select(row => new { deviceId = Convert.ToHexString(row.Session.RemoteDeviceId),
                row.Session.IsConnected, clockState = row.Session.Clock.State.ToString() }).ToArray(),
    };
    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (RpcException error)
{
    Console.Error.WriteLine($"Service request failed: {error.StatusCode}");
    return 1;
}
catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException or
    UnauthorizedAccessException or TimeoutException or System.Security.SecurityException)
{
    Console.Error.WriteLine($"Field control failed: {error.GetType().Name}: {error.Message}");
    return 1;
}
