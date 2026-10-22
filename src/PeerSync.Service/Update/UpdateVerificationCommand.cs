using System.Reflection;
using System.Security.Cryptography;

namespace PeerSync.Service.Update;

/// <summary>Short-lived helper preflight; it never downloads or launches Setup.</summary>
internal static class UpdateVerificationCommand
{
    internal static int Run(string[] args, string dataRoot, TextWriter output, TextWriter error,
        DateTimeOffset? now = null)
    {
        if (args is not ["--verify-update", var directory] || string.IsNullOrWhiteSpace(directory))
        {
            error.WriteLine("Usage: PeerSync.Service.exe --verify-update <staged-directory>");
            return 2;
        }
        try
        {
            UpdatePolicy? policy = new UpdatePolicyStore(Path.Combine(dataRoot, "update-policy", "update-policy.json"))
                .ReadIfPresent();
            if (policy is not { Enabled: true }) throw new InvalidDataException("Automatic updates are disabled.");
            string full = Path.GetFullPath(directory);
            var envelopeFile = new FileInfo(Path.Combine(full, "manifest.json"));
            var setupFile = new FileInfo(Path.Combine(full, "setup.exe"));
            if (!envelopeFile.Exists || envelopeFile.Length is 0 or > 32 * 1024 || !setupFile.Exists)
                throw new InvalidDataException("Incomplete update stage.");
            using var signer = policy.OpenPublisher();
            Version current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            VerifiedUpdate offer = UpdateManifestVerifier.Verify(File.ReadAllBytes(envelopeFile.FullName), signer,
                policy.PublisherCertificateSha256, current, now ?? DateTimeOffset.UtcNow);
            if (setupFile.Length != offer.Size) throw new InvalidDataException("Update setup size differs from manifest.");
            using var file = new FileStream(setupFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), offer.Sha256))
                throw new InvalidDataException("Update setup hash differs from manifest.");
            output.WriteLine("Verified signed update " + offer.Version);
            return 0;
        }
        catch (Exception problem) when (problem is IOException or InvalidDataException or UnauthorizedAccessException or
            CryptographicException or ArgumentException or InvalidOperationException)
        {
            error.WriteLine("Update verification failed: " + problem.GetType().Name);
            return 1;
        }
    }
}
