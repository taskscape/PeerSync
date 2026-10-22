using PeerSync.Protocol.Wire;

namespace PeerSync.Net;

/// <summary>A block request completed with a peer-reported availability error.</summary>
public sealed class PeerBlockException(BlockError errorCode)
    : IOException($"Peer block request failed: {errorCode}.")
{
    public BlockError ErrorCode { get; } = errorCode;
}
