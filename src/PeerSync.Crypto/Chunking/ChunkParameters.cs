namespace PeerSync.Crypto.Chunking;

public readonly record struct ChunkParameters(int Min, int Average, int Max, ulong SmallMask, ulong LargeMask)
{
    public static ChunkParameters ForClass(uint chunkClass) => chunkClass switch
    {
        0 => Create(16),
        1 => Create(19),
        2 => Create(21),
        _ => throw new ArgumentOutOfRangeException(nameof(chunkClass)),
    };

    public static uint ClassFor(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return length < (256L << 20) ? 0u : length < (8L << 30) ? 1u : 2u;
    }

    private static ChunkParameters Create(int bits) => new(
        1 << (bits - 2), 1 << bits, 1 << (bits + 2),
        ulong.MaxValue << (64 - (bits + 2)), ulong.MaxValue << (64 - (bits - 2)));
}
