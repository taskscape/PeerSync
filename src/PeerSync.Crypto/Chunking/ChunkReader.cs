using System.Buffers;

namespace PeerSync.Crypto.Chunking;

public delegate void ChunkConsumer(ReadOnlySpan<byte> chunk);

public static class ChunkReader
{
    /// <summary>Retains a full Max-sized window across short reads. The callback must not retain the buffer.</summary>
    public static async Task<long> ReadAsync(Stream input, uint chunkClass, ChunkConsumer consume,
        int readSize = int.MaxValue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(consume);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readSize);
        var parameters = ChunkParameters.ForClass(chunkClass);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(parameters.Max * 2);
        long total = 0;
        int start = 0, end = 0;
        bool eof = false;
        try
        {
            while (true)
            {
                while (!eof && end - start < parameters.Max)
                {
                    if (start > 0)
                    {
                        buffer.AsSpan(start, end - start).CopyTo(buffer);
                        end -= start;
                        start = 0;
                    }
                    int read = await input.ReadAsync(buffer.AsMemory(end, Math.Min(readSize, buffer.Length - end)), cancellationToken).ConfigureAwait(false);
                    if (read == 0) eof = true;
                    else end += read;
                }
                if (start == end) return total;
                cancellationToken.ThrowIfCancellationRequested();
                int length = FastCdc.NextCut(buffer.AsSpan(start, end - start), parameters);
                consume(buffer.AsSpan(start, length));
                total = checked(total + length);
                start += length;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
