using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeerSync.Engine.Native;

internal static partial class NativeVolume
{
    // Resolve the volume from the held root, never from a mutable drive-letter
    // alias. Actual allocation units deliberately ignore per-user disk quotas.
    internal static unsafe ulong Capacity(SafeFileHandle root)
    {
        char[] buffer = ArrayPool<char>.Shared.Rent(32768);
        try
        {
            uint length;
            fixed (char* pointer = buffer) length = FinalPath(root, pointer, 32768, 1); // VOLUME_NAME_GUID
            if (length == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (length >= 32768) throw new PathTooLongException("Native volume path exceeds the bound.");
            string final = new(buffer, 0, checked((int)length));
            int end = final.IndexOf("}\\", StringComparison.Ordinal);
            if (!final.StartsWith("\\\\?\\Volume{", StringComparison.OrdinalIgnoreCase) || end < 11
                || !Guid.TryParse(final.AsSpan(10, end - 9), out _))
                throw new IOException("A local volume identity is required for archive retention.");
            int result = DiskSpace(final[..(end + 2)], out var space);
            Marshal.ThrowExceptionForHR(result);
            if (space.ActualTotalAllocationUnits == 0 || space.SectorsPerAllocationUnit == 0 || space.BytesPerSector == 0)
                throw new IOException("Invalid native volume capacity.");
            return checked((ulong)((UInt128)space.ActualTotalAllocationUnits * space.SectorsPerAllocationUnit * space.BytesPerSector));
        }
        finally { ArrayPool<char>.Shared.Return(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Space
    {
        public ulong ActualTotalAllocationUnits, ActualAvailableAllocationUnits, ActualPoolUnavailableAllocationUnits;
        public ulong CallerTotalAllocationUnits, CallerAvailableAllocationUnits, CallerPoolUnavailableAllocationUnits;
        public ulong UsedAllocationUnits, TotalReservedAllocationUnits, VolumeStorageReserveAllocationUnits;
        public ulong AvailableCommittedAllocationUnits, PoolAvailableAllocationUnits;
        public uint SectorsPerAllocationUnit, BytesPerSector;
    }
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint FinalPath(SafeFileHandle root, char* path, uint length, uint flags);
    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskSpaceInformationW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DiskSpace(string volumeRoot, out Space space);
}
