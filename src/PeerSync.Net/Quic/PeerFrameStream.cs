using System.Buffers;
using System.IO.Pipelines;
using Google.Protobuf;
using PeerSync.Protocol;
using PeerSync.Protocol.Framing;

namespace PeerSync.Net.Quic;

// One reader, serialized writers. Owner cancels/drains readers before disposal.
internal sealed class PeerFrameStream(Stream stream) : IAsyncDisposable
{
    private readonly PipeReader reader = PipeReader.Create(stream, new(leaveOpen: true));
    private readonly SemaphoreSlim writes = new(1);
    internal Stream Stream { get; } = stream;

    internal async ValueTask<ProtocolFrame?> ReadAsync(CancellationToken token)
    {
        while (true)
        {
            var result = await reader.ReadAsync(token).ConfigureAwait(false);
            var buffer = result.Buffer; var remaining = buffer; var examined = buffer.End;
            try
            {
                if (result.IsCanceled) throw new OperationCanceledException(token);
                if (FrameCodec.TryRead(ref remaining, out var frame))
                {
                    examined = remaining.Start;
                    return new ProtocolFrame(frame.Type, new ReadOnlySequence<byte>(frame.Body.ToArray()));
                }
                if (result.IsCompleted)
                {
                    if (!remaining.IsEmpty) throw new ProtocolException(ProtocolError.TruncatedFrame);
                    return null;
                }
            }
            finally { reader.AdvanceTo(remaining.Start, examined); }
        }
    }

    internal async ValueTask WriteAsync(IMessage message, CancellationToken token)
    {
        var (type, body) = WireMessageCodec.Encode(message);
        var bytes = new ArrayBufferWriter<byte>(); FrameCodec.Write(bytes, (byte)type, body);
        await writes.WaitAsync(token).ConfigureAwait(false);
        try { await Stream.WriteAsync(bytes.WrittenMemory, token).ConfigureAwait(false); }
        finally { writes.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await reader.CompleteAsync().ConfigureAwait(false);
        await Stream.DisposeAsync().ConfigureAwait(false);
        writes.Dispose();
    }
}
