using System.Security.Cryptography;

namespace PeerSync.Net.Security;

/// <summary>Frozen certificate identities and connection nonces; no folder keys.</summary>
public sealed class PeerAuthenticationContext
{
    private readonly byte[] local, remote, localNonce, remoteNonce;
    public PeerAuthenticationContext(ReadOnlySpan<byte> localCertificateId, ReadOnlySpan<byte> remoteCertificateId,
        ReadOnlySpan<byte> ownNonce, ReadOnlySpan<byte> peerNonce)
    {
        if (localCertificateId.Length != 32 || remoteCertificateId.Length != 32 || ownNonce.Length != 32 || peerNonce.Length != 32)
            throw new ArgumentException("Peer transcript fields must contain 32 bytes.");
        if (localCertificateId.SequenceEqual(remoteCertificateId)) throw new ArgumentException("A peer cannot authenticate itself.");
        local = localCertificateId.ToArray(); remote = remoteCertificateId.ToArray(); localNonce = ownNonce.ToArray(); remoteNonce = peerNonce.ToArray();
        bool first = local.AsSpan().SequenceCompareTo(remote) < 0;
        byte[] hash = SHA256.HashData([.. "psp1 session"u8, .. (first ? local : remote), .. (first ? localNonce : remoteNonce),
            .. (first ? remote : local), .. (first ? remoteNonce : localNonce)]);
        SessionId = new(hash.AsSpan(0, 16));
    }
    public Guid SessionId { get; }
    public byte[] LocalDeviceId => local.ToArray();
    public byte[] RemoteDeviceId => remote.ToArray();
    internal ReadOnlySpan<byte> Local => local;
    internal ReadOnlySpan<byte> Remote => remote;
    internal ReadOnlySpan<byte> OwnNonce => localNonce;
    internal ReadOnlySpan<byte> PeerNonce => remoteNonce;
    public override string ToString() => "Peer authentication context (redacted)";
}
