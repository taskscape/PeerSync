using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
using PeerSync.Engine.Hashing;

namespace PeerSync.Engine.Native;

internal static class FileMetadata
{
    /// <summary>Prepare the new file before moving the old one: preserve its DACL,
    /// inheritance protection and creation time. Owner/group/audit data are not copied.</summary>
    public static void CopyReplacementMetadata(SafeFileHandle original, SafeFileHandle temporary)
    {
        var basic = Read(original);
        byte[] descriptor = WithStream(original, stream => stream.GetAccessControl().GetSecurityDescriptorBinaryForm());
        var security = new FileSecurity();
        // A fresh object marks Access modified; persisting an unchanged GetAccessControl object can be a no-op.
        security.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
        WithStream(temporary, stream => { stream.SetAccessControl(security); return true; });
        Write(temporary, new() { CreationTime = basic.CreationTime });
    }

    public static long ValidateMtime(long utcTicks)
    {
        long fileTime = new DateTime(utcTicks, DateTimeKind.Utc).ToFileTimeUtc();
        // Zero means "leave unchanged" to FILE_BASIC_INFORMATION, rather than an exact timestamp.
        if (fileTime == 0) throw new ArgumentOutOfRangeException(nameof(utcTicks), "Timestamp cannot be represented by this mutation API.");
        return fileTime;
    }

    public static void SetMtime(SafeFileHandle file, long utcTicks) => Write(file, new() { LastWriteTime = ValidateMtime(utcTicks) });

    /// <summary>Apply the protocol's synchronized attributes to a newly created temporary file.</summary>
    public static void SetSyncedAttributes(SafeFileHandle file, uint attributes)
    {
        uint flags = attributes & (uint)(FileAttributes.ReadOnly | FileAttributes.Hidden);
        Write(file, new() { Attributes = flags == 0 ? (uint)FileAttributes.Normal : flags });
    }

    public static FileAttributes ClearReadOnly(SafeFileHandle file)
    {
        var attributes = (FileAttributes)Read(file).Attributes;
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            uint flags = (uint)(attributes & ~FileAttributes.ReadOnly);
            Write(file, new() { Attributes = flags == 0 ? (uint)FileAttributes.Normal : flags });
        }
        return attributes;
    }

    public static void RestoreAttributes(SafeFileHandle file, FileAttributes attributes) =>
        Write(file, new() { Attributes = attributes == 0 ? (uint)FileAttributes.Normal : (uint)attributes });

    internal static NativeFileInfo.BasicInfo Read(SafeFileHandle file)
    {
        if (!NativeFileInfo.GetBasic(file, 0, out var info, (uint)Marshal.SizeOf<NativeFileInfo.BasicInfo>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return info;
    }

    private static void Write(SafeFileHandle file, NativeFileInfo.BasicInfo info)
    {
        if (!NativeMutations.SetBasic(file, 0, in info, (uint)Marshal.SizeOf<NativeFileInfo.BasicInfo>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static T WithStream<T>(SafeFileHandle file, Func<FileStream, T> operation)
    {
        bool retained = false;
        try
        {
            file.DangerousAddRef(ref retained);
            using var borrowed = new SafeFileHandle(file.DangerousGetHandle(), ownsHandle: false);
            using var stream = new FileStream(borrowed, FileAccess.Read, 0, isAsync: false);
            return operation(stream);
        }
        finally { if (retained) file.DangerousRelease(); }
    }
}

internal static partial class NativeMutations
{
    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetBasic(SafeFileHandle file, int infoClass, in NativeFileInfo.BasicInfo info, uint size);
}
