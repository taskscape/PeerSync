using System.Net;
using System.Security.Cryptography;

namespace PeerSync.Rendezvous.Hosting;

/// <summary>Limits IP correlation in ASP.NET Core's long-lived rate-limiter partition cache.</summary>
internal sealed class EphemeralIpRateKey(TimeProvider? clock = null) : IDisposable
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private byte[] secret = RandomNumberGenerator.GetBytes(32);
    private DateTimeOffset secretCreated;

    internal string For(IPAddress? address)
    {
        if (address is null) return "unknown";
        lock (gate)
        {
            DateTimeOffset now = time.GetUtcNow();
            if (secretCreated == default) secretCreated = now;
            if (now - secretCreated >= TimeSpan.FromHours(24))
            {
                CryptographicOperations.ZeroMemory(secret);
                secret = RandomNumberGenerator.GetBytes(32);
                secretCreated = now;
            }
            using var hmac = new HMACSHA256(secret);
            return Convert.ToHexString(hmac.ComputeHash(address.GetAddressBytes()));
        }
    }

    public void Dispose()
    {
        lock (gate) CryptographicOperations.ZeroMemory(secret);
    }
}
