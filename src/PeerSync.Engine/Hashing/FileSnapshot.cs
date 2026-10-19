using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeerSync.Engine.Hashing;

internal readonly record struct FileSnapshot(long Length, long LastWrite, long Changed,
    FileAttributes Attributes, ulong Volume, ulong IdLow, ulong IdHigh)
{
    public static FileSnapshot Read(SafeFileHandle handle)
    {
        if (!NativeFileInfo.GetBasic(handle, 0, out var basic, (uint)Marshal.SizeOf<NativeFileInfo.BasicInfo>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!NativeFileInfo.GetIdentity(handle, 18, out var identity, (uint)Marshal.SizeOf<NativeFileInfo.IdentityInfo>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new(((FileAttributes)basic.Attributes & FileAttributes.Directory) != 0 ? 0 : RandomAccess.GetLength(handle), basic.LastWriteTime, basic.ChangeTime,
            (FileAttributes)basic.Attributes, identity.VolumeSerial, identity.IdLow, identity.IdHigh);
    }
}

internal static partial class NativeFileInfo
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IdentityInfo
    {
        public ulong VolumeSerial;
        public ulong IdLow;
        public ulong IdHigh;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetBasic(SafeFileHandle handle, int informationClass, out BasicInfo info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetIdentity(SafeFileHandle handle, int informationClass, out IdentityInfo info, uint size);
}
