using System.Buffers;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace PeerSync.Engine.Native;

internal static class GuardedFile
{
    // Caller holds the no-write/no-delete sharing guard and ancestor leases.
    internal static byte[] Hash(SafeFileHandle file, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var hasher = Blake3.Hasher.New(); long offset = 0, length = RandomAccess.GetLength(file);
            while (offset < length)
            {
                token.ThrowIfCancellationRequested();
                int read = RandomAccess.Read(file, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
                if (read == 0) throw new EndOfStreamException();
                hasher.Update(buffer.AsSpan(0, read)); offset += read;
            }
            return hasher.Finalize().AsSpan().ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    internal static unsafe void DeleteOnClose(SafeFileHandle file)
    {
        var attributes = FileMetadata.ClearReadOnly(file);
        byte delete = 1; // FILE_DISPOSITION_INFORMATION.DeleteFile, not a recursive/path-based delete.
        int status = NativeMutations.NtSetInformationFile(file, out _, &delete, 1, 13);
        if (status >= 0) return;
        FileMetadata.RestoreAttributes(file, attributes);
        throw new Win32Exception((int)NativeFiles.RtlNtStatusToDosError(status));
    }
}
