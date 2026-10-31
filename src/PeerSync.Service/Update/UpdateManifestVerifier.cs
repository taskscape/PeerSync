using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace PeerSync.Service.Update;

internal sealed record VerifiedUpdate(Version Version, Uri SetupUrl, byte[] Sha256,
    long Size, DateTimeOffset ExpiresUtc, byte[] SignedPayload, byte[] Signature);

/// <summary>Validates the signed release offer before any URL or file is used.</summary>
internal static class UpdateManifestVerifier
{
    private const int MaximumEnvelopeBytes = 32 * 1024;
    private const int MaximumPayloadBytes = 4 * 1024;
    internal const long MaximumSetupBytes = 1024L * 1024 * 1024;

    internal static VerifiedUpdate Verify(ReadOnlySpan<byte> envelopeBytes, X509Certificate2 publisher,
        ReadOnlySpan<byte> pinnedCertificateSha256, Version installedVersion, DateTimeOffset now,
        bool allowCurrentVersion = false)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(installedVersion);
        if (pinnedCertificateSha256.Length != 32 || envelopeBytes.Length is 0 or > MaximumEnvelopeBytes)
            throw new InvalidDataException("Invalid update manifest bounds or publisher pin.");
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(publisher.RawData), pinnedCertificateSha256))
            throw new InvalidDataException("Update publisher does not match the administrator pin.");

        byte[] payload, signature;
        try
        {
            using var envelope = JsonDocument.Parse(envelopeBytes.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            RequireFields(envelope.RootElement, "payload", "signature");
            payload = Convert.FromBase64String(RequiredString(envelope.RootElement, "payload"));
            signature = Convert.FromBase64String(RequiredString(envelope.RootElement, "signature"));
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        { throw new InvalidDataException("Invalid update manifest envelope.", error); }
        if (payload.Length is 0 or > MaximumPayloadBytes || signature.Length is 0 or > 1024)
            throw new InvalidDataException("Invalid update manifest signature bounds.");
        using (RSA? key = publisher.GetRSAPublicKey())
            if (key is null || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("Update manifest signature is invalid.");

        try
        {
            using var content = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
            JsonElement root = content.RootElement;
            RequireFields(root, "version", "url", "sha256", "size", "expiresUtcTicks");
            string versionText = RequiredString(root, "version");
            if (!Version.TryParse(versionText, out Version? version) || version.Build < 0
                || version < installedVersion || (!allowCurrentVersion && version == installedVersion)
                || versionText.Length > 32)
                throw new InvalidDataException("Update version is invalid or not newer.");
            string urlText = RequiredString(root, "url");
            if (urlText.Length > 2048 || !Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url)
                || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length != 0 || url.Fragment.Length != 0)
                throw new InvalidDataException("Update URL is invalid.");
            string hashText = RequiredString(root, "sha256");
            if (hashText.Length != 64)
                throw new InvalidDataException("Update hash is invalid.");
            byte[] hash = Convert.FromHexString(hashText);
            long size = root.GetProperty("size").GetInt64();
            if (size is <= 0 or > MaximumSetupBytes)
                throw new InvalidDataException("Update size is invalid.");
            long expiryTicks = root.GetProperty("expiresUtcTicks").GetInt64();
            if (expiryTicks <= now.UtcTicks || expiryTicks > now.AddDays(30).UtcTicks)
                throw new InvalidDataException("Update manifest has expired or exceeds its validity limit.");
            return new(version, url, hash, size, new DateTimeOffset(expiryTicks, TimeSpan.Zero), payload, signature);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException
            or ArgumentOutOfRangeException or OverflowException)
        { throw new InvalidDataException("Invalid signed update manifest payload.", error); }
    }

    private static string RequiredString(JsonElement root, string name)
        => root.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing update manifest field.");

    private static void RequireFields(JsonElement root, params string[] expected)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Update manifest must be an object.");
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty field in root.EnumerateObject())
            if (!found.Add(field.Name) || !expected.Contains(field.Name, StringComparer.Ordinal))
                throw new InvalidDataException("Update manifest contains an unexpected or duplicate field.");
        if (found.Count != expected.Length) throw new InvalidDataException("Update manifest is missing a field.");
    }
}
