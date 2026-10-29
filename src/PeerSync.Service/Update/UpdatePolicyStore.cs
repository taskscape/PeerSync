using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace PeerSync.Service.Update;

internal sealed record UpdatePolicy(bool Enabled, Uri ManifestUrl,
    byte[] PublisherCertificate, byte[] PublisherCertificateSha256)
{
    internal X509Certificate2 OpenPublisher() => X509CertificateLoader.LoadCertificate(PublisherCertificate);
}

/// <summary>Reads the administrator-owned policy. The service has read-only access.</summary>
internal sealed class UpdatePolicyStore(string path)
{
    private const int MaximumPolicyBytes = 16 * 1024;

    internal UpdatePolicy? ReadIfPresent()
    {
        if (!File.Exists(path)) return null;
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length is 0 or > MaximumPolicyBytes)
            throw new InvalidDataException("Update policy size is invalid.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Update policy must be an object.");
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty item in root.EnumerateObject())
                if (!fields.Add(item.Name) || item.Name is not ("enabled" or "manifestUrl" or
                    "publisherCertificate" or "publisherCertificateSha256"))
                    throw new InvalidDataException("Update policy has an unknown or duplicate field.");
            if (fields.Count != 4) throw new InvalidDataException("Update policy is missing a field.");
            bool enabled = root.GetProperty("enabled").GetBoolean();
            string urlText = root.GetProperty("manifestUrl").GetString()!;
            if (urlText is null || urlText.Length > 2048 || !Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url)
                || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length != 0 || url.Fragment.Length != 0)
                throw new InvalidDataException("Update policy URL is invalid.");
            string certificateText = root.GetProperty("publisherCertificate").GetString()!;
            string pinText = root.GetProperty("publisherCertificateSha256").GetString()!;
            if (certificateText is null or { Length: > 8192 } || pinText?.Length != 64)
                throw new InvalidDataException("Update policy publisher is invalid.");
            byte[] certificate = Convert.FromBase64String(certificateText);
            byte[] pin = Convert.FromHexString(pinText);
            if (certificate.Length is 0 or > 4096 || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(certificate), pin))
                throw new InvalidDataException("Update policy publisher pin is invalid.");
            using X509Certificate2 publisher = X509CertificateLoader.LoadCertificate(certificate);
            using RSA? key = publisher.GetRSAPublicKey();
            if (key is null) throw new InvalidDataException("Update publisher must use RSA.");
            return new(enabled, url, certificate, pin);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException
            or CryptographicException)
        { throw new InvalidDataException("Update policy is invalid.", error); }
    }
}
