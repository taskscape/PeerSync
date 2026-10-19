using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeerSync.Engine.Native;

internal static partial class FileIoPriority
{
    public static void SetVeryLow(SafeFileHandle file)
    {
        // FILE_IO_PRIORITY_HINT_INFO must be 8-byte aligned. Native scalar
        // storage is an aligned ulong; its low DWORD is IoPriorityHintVeryLow=0.
        ulong hint = 0;
        if (!Set(file, 12, in hint, 8)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Set(SafeFileHandle file, int infoClass, in ulong hint, uint size);
}
