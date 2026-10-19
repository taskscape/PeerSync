using PeerSync.Core.Sync;
using PeerSync.Protocol.Handshake;

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
