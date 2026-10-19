using System.Buffers.Binary;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using PeerSync.Crypto.Chunking;
using PeerSync.Engine.Native;

namespace PeerSync.Engine.Hashing;

internal delegate void CaptureChunk(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> hash);

public sealed record ChunkInfo(long Offset, int Length, byte[] Hash);
public sealed record FileHash(long Length, DateTime MtimeUtc, uint ChunkClass,
    IReadOnlyList<ChunkInfo> Chunks, byte[] WholeHash)
{
    public FileAttributes Attributes { get; init; }
    public byte[]? FileId { get; init; }
    public ulong Volume { get; init; }
    public long ChangeFileTime { get; init; }
}
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
        return await HashOpenedAsync(file, () =>
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points cannot be hashed.");
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }, progress, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<FileHash?> HashRelativeAsync(NativeRoot root, string relativePath,
        IProgress<FileHashProgress>? progress = null, CaptureChunk? capture = null, CancellationToken cancellationToken = default)
    {
        using var handle = root.OpenFile(relativePath);
        FileIoPriority.SetVeryLow(handle);
        await using var file = new FileStream(handle, FileAccess.Read, 0, isAsync: false);
        return await HashOpenedAsync(file, () => root.OpenFile(relativePath), progress, cancellationToken, capture).ConfigureAwait(false);
    }

    private static async Task<FileHash?> HashOpenedAsync(FileStream file, Func<SafeFileHandle> reopen,
        IProgress<FileHashProgress>? progress, CancellationToken cancellationToken, CaptureChunk? capture = null)
    {
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
            capture?.Invoke(chunk, digest.AsSpan());
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
            using var current = reopen();
            if (FileSnapshot.Read(current) != before) return null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3 or 32 or 1920) { return null; }
        var finalDigest = whole.Finalize();
        byte[] identity = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(identity, before.IdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(identity.AsSpan(8), before.IdHigh);
        return new(total, DateTime.FromFileTimeUtc(before.LastWrite), chunkClass,
            chunks.AsReadOnly(), finalDigest.AsSpan().ToArray())
        { Attributes = before.Attributes, FileId = identity, Volume = before.Volume, ChangeFileTime = before.Changed };
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
