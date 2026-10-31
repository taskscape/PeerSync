using System.Security.Cryptography;

namespace PeerSync.Core.Diagnostics;

/// <summary>A short, stable correlation value for public folder and device identifiers.</summary>
public static class DiagnosticFingerprint
{
    public static string FromId(ReadOnlySpan<byte> id)
    {
        if (id.Length != 32) throw new ArgumentException("A diagnostic identifier must contain 32 bytes.", nameof(id));
        return Convert.ToHexString(SHA256.HashData(id))[..16];
    }
}
