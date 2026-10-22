using Google.Protobuf;
using Grpc.Core;
using PeerSync.Rendezvous.Contracts.V1;

namespace PeerSync.Net.Relay;

/// <summary>Byte stream carried by one relay pipe. gRPC sees only opaque TLS records.</summary>
internal sealed class GrpcDuplexStream(AsyncDuplexStreamingCall<RelayChunk, RelayChunk> call) : Stream
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private ReadOnlyMemory<byte> pending;
    private int disposed;

    public override bool CanRead => Volatile.Read(ref disposed) == 0;
    public override bool CanWrite => Volatile.Read(ref disposed) == 0;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (buffer.IsEmpty) return 0;
        while (pending.IsEmpty)
        {
            if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)) return 0;
            pending = call.ResponseStream.Current.Data.Memory;
        }
        int count = Math.Min(buffer.Length, pending.Length);
        pending.Span[..count].CopyTo(buffer.Span);
        pending = pending[count..];
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (!buffer.IsEmpty)
            {
                int count = Math.Min(buffer.Length, 64 * 1024);
                // SslStream reuses its write buffer; ByteString must own a copy.
                await call.RequestStream.WriteAsync(new RelayChunk
                { Data = ByteString.CopyFrom(buffer.Span[..count]) }, cancellationToken).ConfigureAwait(false);
                buffer = buffer[count..];
            }
        }
        finally { writes.Release(); }
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        { call.Dispose(); writes.Dispose(); }
        base.Dispose(disposing);
    }

}
