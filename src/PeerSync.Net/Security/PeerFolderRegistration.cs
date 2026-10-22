using PeerSync.Crypto;
using PeerSync.Protocol.Wire;

namespace PeerSync.Net.Security;

/// <summary>Caller-owned keys and explicit local authorization; no implicit trust on first use.</summary>
public sealed class PeerFolderRegistration
{
    public PeerFolderRegistration(FolderKeys keys, Func<byte[], AuthDecision> authorize,
        Func<byte[], IPeerRequestHandler> createHandler)
    {
        ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(authorize); ArgumentNullException.ThrowIfNull(createHandler);
        Keys = keys; Authorize = authorize; CreateHandler = createHandler;
    }
    public FolderKeys Keys { get; }
    internal Func<byte[], AuthDecision> Authorize { get; }
    internal Func<byte[], IPeerRequestHandler> CreateHandler { get; }
    public override string ToString() => "Peer folder registration (redacted)";
}
