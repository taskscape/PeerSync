using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace PeerSync.Service.Update;

/// <summary>Downloads an authenticated offer into a service-writable pending slot.</summary>
internal sealed class UpdateDownloader(HttpClient client, string updatesRoot, Version installedVersion,
    TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    internal async Task<bool> CheckOnceAsync(UpdatePolicy policy, CancellationToken cancellationToken)
    {
        if (!policy.Enabled) return false;
        string root = Path.GetFullPath(updatesRoot);
        Directory.CreateDirectory(root);
        if (Directory.EnumerateDirectories(root, "*.ready", SearchOption.TopDirectoryOnly).Any()) return false;
        byte[] envelope = await ReadBoundedAsync(policy.ManifestUrl, 32 * 1024, cancellationToken).ConfigureAwait(false);
        using var publisher = policy.OpenPublisher();
        VerifiedUpdate offer = UpdateManifestVerifier.Verify(envelope, publisher,
            policy.PublisherCertificateSha256, installedVersion, clock.GetUtcNow(), allowCurrentVersion: true);
        if (offer.Version == installedVersion) return false;
        string id = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(root, id + ".staging");
        string ready = Path.Combine(root, id + ".ready");
        Directory.CreateDirectory(stage);
        try
        {
            string setup = Path.Combine(stage, "setup.exe");
            using var response = await client.GetAsync(offer.SetupUrl, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            RequireDirectHttps(response, offer.SetupUrl);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } announced && announced != offer.Size)
                throw new InvalidDataException("Update setup length differs from the signed manifest.");
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(setup, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        total = checked(total + read);
                        if (total > offer.Size) throw new InvalidDataException("Update setup exceeds the signed size.");
                        digest.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    if (total != offer.Size || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), offer.Sha256))
                        throw new InvalidDataException("Update setup hash or size differs from the signed manifest.");
                    output.Flush(flushToDisk: true);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            }
            await WriteDurableAsync(Path.Combine(stage, "manifest.json"), envelope, cancellationToken).ConfigureAwait(false);
            byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new
            { version = offer.Version.ToString(), stagedUtc = clock.GetUtcNow().UtcTicks });
            await WriteDurableAsync(Path.Combine(stage, "receipt.json"), receipt, cancellationToken).ConfigureAwait(false);
            Directory.Move(stage, ready);
            return true;
        }
        catch
        {
            // Only the newly created, GUID-named stage owned by this call is removed.
            if (Directory.Exists(stage) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(stage)), root,
                StringComparison.OrdinalIgnoreCase) && Path.GetFileName(stage) == id + ".staging")
                Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private async Task<byte[]> ReadBoundedAsync(Uri uri, int limit, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        RequireDirectHttps(response, uri);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } announced && announced > limit)
            throw new InvalidDataException("Update manifest exceeds the size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (destination.Length + read > limit) throw new InvalidDataException("Update manifest exceeds the size limit.");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static void RequireDirectHttps(HttpResponseMessage response, Uri expected)
    {
        if (expected.Scheme != Uri.UriSchemeHttps || response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
            throw new InvalidDataException("Update response redirected or used an insecure URL.");
        if (response.RequestMessage?.RequestUri is { } actual && actual != expected)
            throw new InvalidDataException("Update response URL changed unexpectedly.");
    }

    private static async Task WriteDurableAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
        await file.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }
}
