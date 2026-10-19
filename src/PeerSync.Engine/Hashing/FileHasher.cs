using PeerSync.Crypto.Chunking;

namespace PeerSync.Engine.Hashing;

public sealed record ChunkInfo(long Offset, int Length, byte[] Hash);
public sealed record FileHash(long Length, DateTime MtimeUtc, uint ChunkClass,
    IReadOnlyList<ChunkInfo> Chunks, byte[] WholeHash);
public readonly record struct FileHashProgress(long BytesHashed, long ExpectedLength, int ChunksHashed);

public static class FileHasher
{
    /// <summary>Null means the file changed and should be retried. I/O/access errors propagate to the scheduler.</summary>
    public static async Task<FileHash?> HashAsync(string path, IProgress<FileHashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Reparse points cannot be hashed.");
        await using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan | FileOptions.Asynchronous, BufferSize = 0,
        });
        var before = FileSnapshot.Read(file.SafeFileHandle);
        if ((before.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Only regular files can be hashed.");
        uint chunkClass = ChunkParameters.ClassFor(before.Length);
        var chunks = new List<ChunkInfo>();
        using var whole = Blake3.Hasher.New();
        using var bounded = new BoundedReadStream(file, before.Length);
        long offset = 0;
        progress?.Report(new(0, before.Length, 0));
        long total = await ChunkReader.ReadAsync(bounded, chunkClass, chunk =>
        {
            whole.Update(chunk);
            var digest = Blake3.Hasher.Hash(chunk);
            chunks.Add(new(offset, chunk.Length, digest.AsSpan().ToArray()));
            offset = checked(offset + chunk.Length);
            progress?.Report(new(offset, before.Length, chunks.Count));
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var after = FileSnapshot.Read(file.SafeFileHandle);
        if (total != before.Length || before != after) return null;
        try
        {
            // A path can be replaced while its old file remains open under Share.Delete.
            using var current = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || FileSnapshot.Read(current) != before) return null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var finalDigest = whole.Finalize();
        return new(total, DateTime.FromFileTimeUtc(before.LastWrite), chunkClass,
            chunks.AsReadOnly(), finalDigest.AsSpan().ToArray());
    }

    // Never chase a continuously growing file to EOF. The underlying FileStream
    // remains owned by HashAsync and is disposed separately.
    private sealed class BoundedReadStream(Stream input, long remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = input.Read(buffer, offset, (int)Math.Min(count, remaining));
            remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining == 0) return 0;
            int read = await input.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken).ConfigureAwait(false);
            remaining -= read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
