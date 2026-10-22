using PeerSync.Core.Sync;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Wire;
using System.Threading.Channels;

namespace PeerSync.Net;

/// <summary>One authenticated device/folder connection. Implementations must
/// permit independent requests, freeze handed-off buffers, bound outstanding
/// work and cancel/drain requests before disposing their handler.</summary>
public interface IPeerTransport : IAsyncDisposable
{
    byte[] RemoteDeviceId { get; }
    ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken = default);
    ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken = default);
    ValueTask<PeerIndexPage> ReadIndexAsync(long afterSequence, int limit = 1000, CancellationToken cancellationToken = default);
    ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellationToken = default);

    async ValueTask<IReadOnlyList<PeerBlockResult>> ReadBlockBatchAsync(
        IReadOnlyList<PeerBlockRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(requests));
        var results = new PeerBlockResult[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PeerBlockRequest request = requests[i];
            try
            {
                ReadOnlyMemory<byte> data = await ReadBlockAsync(request.ChunkHash, request.Length, cancellationToken).ConfigureAwait(false);
                results[i] = new(data, null);
            }
            catch (PeerBlockException error)
            {
                results[i] = new(ReadOnlyMemory<byte>.Empty, error.ErrorCode);
            }
        }
        return results;
    }
}

/// <summary>Optional authenticated transport capability for unsolicited, coalesced
/// index high-water notifications. The receiver still fetches and validates the
/// signed index pages through <see cref="IPeerTransport.ReadIndexAsync"/>.</summary>
public interface IPeerIndexPushTransport
{
    ChannelReader<long> IndexTargets { get; }
    ValueTask PublishIndexTargetAsync(long highestSequence, CancellationToken cancellationToken = default);
}

public sealed record PeerBlockRequest(ReadOnlyMemory<byte> ChunkHash, int Length);
public sealed record PeerBlockResult(ReadOnlyMemory<byte> Data, BlockError? ErrorCode);

/// <summary>Identity shared by both endpoints for one authenticated peer session.</summary>
public interface IPeerSessionIdentity
{
    Guid SessionId { get; }
}

/// <summary>Connection-scoped server callbacks. The transport supplies the
/// authenticated remote identity when constructing this handler, never in RPC
/// bodies. Implementations honor cancellation and must allow concurrent calls.</summary>
public interface IPeerRequestHandler : IAsyncDisposable
{
    ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken);
    ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken);
    ValueTask<PeerIndexPage> ReadIndexAsync(long afterSequence, int limit, CancellationToken cancellationToken);
    ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellationToken);
}
