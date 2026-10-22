using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace PeerSync.Protocol.Framing;

/// <summary>Body memory is borrowed until the next reader advance/disposal.</summary>
public readonly record struct ProtocolFrame(byte Type, ReadOnlySequence<byte> Body);

public static class FrameCodec
{
    public const int MaximumBodyBytes = 16 * 1024 * 1024;

    /// <summary>Does not consume any bytes until a complete frame is available.</summary>
    public static bool TryRead(ref ReadOnlySequence<byte> buffer, out ProtocolFrame frame)
    {
        frame = default;
        var reader = new SequenceReader<byte>(buffer);
        uint length = 0;
        for (int index = 0; index < 5; index++)
        {
            if (!reader.TryRead(out byte value)) return false;
            if (index == 4 && (value & 0xf0) != 0) throw new ProtocolException(ProtocolError.InvalidLengthPrefix);
            length |= (uint)(value & 0x7f) << (index * 7);
            if ((value & 0x80) != 0) continue;
            if (index != 0 && value == 0) throw new ProtocolException(ProtocolError.InvalidLengthPrefix);
            if (length > MaximumBodyBytes) throw new ProtocolException(ProtocolError.FrameTooLarge);
            if (reader.Remaining < (long)length + 1) return false;
            reader.TryRead(out byte type);
            var body = buffer.Slice(reader.Position, length);
            frame = new(type, body);
            buffer = buffer.Slice(body.End);
            return true;
        }
        throw new ProtocolException(ProtocolError.InvalidLengthPrefix);
    }

    public static void Write(IBufferWriter<byte> writer, byte type, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (body.Length > MaximumBodyBytes) throw new ProtocolException(ProtocolError.FrameTooLarge);
        Span<byte> prefix = stackalloc byte[6];
        uint length = (uint)body.Length;
        int count = 0;
        do
        {
            byte next = (byte)(length & 0x7f); length >>= 7;
            prefix[count++] = length == 0 ? next : (byte)(next | 0x80);
        } while (length != 0);
        prefix[count++] = type;
        writer.Write(prefix[..count]);
        writer.Write(body);
    }

    public static ValueTask<FlushResult> WriteAsync(PipeWriter writer, byte type,
        ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(writer, type, body.Span);
        return writer.FlushAsync(cancellationToken);
    }

    /// <summary>The caller owns the reader and must decode/copy before MoveNext.</summary>
    public static async IAsyncEnumerable<ProtocolFrame> ReadFramesAsync(PipeReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var remaining = result.Buffer;
            // Keep examined monotonic if parsing throws after an earlier
            // incomplete read examined this same prefix.
            var examined = remaining.End;
            try
            {
                if (result.IsCanceled) throw new OperationCanceledException(cancellationToken);
                while (TryRead(ref remaining, out var frame))
                {
                    examined = remaining.Start;
                    yield return frame;
                }
                examined = remaining.End;
                if (result.IsCompleted)
                {
                    if (!remaining.IsEmpty) throw new ProtocolException(ProtocolError.TruncatedFrame);
                    yield break;
                }
            }
            finally { reader.AdvanceTo(remaining.Start, examined); }
        }
    }
}
