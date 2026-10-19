using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeerSync.Core.Diagnostics;

// Small local-log primitive. Every ancestor is pinned and every child opens
// relative to its guarded parent; no recursive or pathname-based deletion.
internal sealed class DiagnosticDirectory : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    internal SafeFileHandle Handle => handles[^1];
    internal string Path { get; }
    internal DiagnosticDirectory(string path)
    {
        Path = System.IO.Path.GetFullPath(path); string volume = System.IO.Path.GetPathRoot(Path)!;
        if (volume.Length != 3 || volume[1] != ':') throw new ArgumentException("Diagnostic runs require a local drive root.", nameof(path));
        try
        {
            var root = LogNative.CreateFile(volume, 0x81, 3, 0, 3, 0x02200000, 0);
            if (root.IsInvalid) { int error = Marshal.GetLastPInvokeError(); root.Dispose(); throw new Win32Exception(error); }
            handles.Add(root); RequireKind(root, directory: true);
            foreach (string component in Path[volume.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                handles.Add(Open(Handle, component, true, 0x00100081, 3, 3));
        }
        catch { Dispose(); throw; }
    }
    internal static unsafe SafeFileHandle Open(SafeFileHandle parent, string name, bool directory, uint access, uint share, uint disposition = 1)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
            throw new IOException("Invalid diagnostic path component.");
        fixed (char* pointer = name)
        {
            var text = new LogNative.UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = pointer };
            bool retained = false;
            try
            {
                parent.DangerousAddRef(ref retained);
                var attributes = new LogNative.ObjectAttributes { Length = (uint)sizeof(LogNative.ObjectAttributes), RootDirectory = parent.DangerousGetHandle(), ObjectName = &text, Attributes = 0x1040 };
                int status = LogNative.NtCreateFile(out var handle, access, in attributes, out _, 0, directory ? 0x10u : 0x80u, share, disposition,
                    0x00200020u | (directory ? 1u : 0x40u), 0, 0);
                if (status < 0) { handle.Dispose(); throw new Win32Exception((int)LogNative.RtlNtStatusToDosError(status)); }
                try { RequireKind(handle, directory); return handle; }
                catch { handle.Dispose(); throw; }
            }
            finally { if (retained) parent.DangerousRelease(); }
        }
    }
    internal static LogNative.BasicInfo Basic(SafeFileHandle handle)
    {
        if (!LogNative.GetBasic(handle, 0, out var info, (uint)Marshal.SizeOf<LogNative.BasicInfo>())) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return info;
    }
    private static void RequireKind(SafeFileHandle handle, bool directory)
    {
        var attributes = (FileAttributes)Basic(handle).Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("Diagnostic path is not an ordinary expected object.");
        if (!directory)
        {
            if (!LogNative.GetStandard(handle, 1, out var standard, 24)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (standard.NumberOfLinks != 1) throw new IOException("Diagnostic hardlinks are preserved.");
        }
    }
    internal static void Delete(SafeFileHandle handle)
    {
        byte delete = 1;
        if (!LogNative.SetDisposition(handle, 4, ref delete, 1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
}

internal static partial class LogNative
{
    [StructLayout(LayoutKind.Sequential)] internal unsafe struct UnicodeString { public ushort Length, MaximumLength; public char* Buffer; }
    [StructLayout(LayoutKind.Sequential)] internal unsafe struct ObjectAttributes
    { public uint Length; public nint RootDirectory; public UnicodeString* ObjectName; public uint Attributes; public nint SecurityDescriptor, SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] internal struct IoStatusBlock { public nint Status; public nuint Information; }
    [StructLayout(LayoutKind.Sequential)] internal struct BasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint Attributes; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct StandardInfo
    { [FieldOffset(0)] public long AllocationSize; [FieldOffset(8)] public long EndOfFile; [FieldOffset(16)] public uint NumberOfLinks; [FieldOffset(20)] public byte DeletePending; [FieldOffset(21)] public byte Directory; }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("ntdll.dll")] internal static unsafe partial int NtCreateFile(out SafeFileHandle handle, uint access, in ObjectAttributes attributes, out IoStatusBlock status,
        nint allocationSize, uint fileAttributes, uint share, uint disposition, uint options, nint eaBuffer, uint eaLength);
    [LibraryImport("ntdll.dll")] internal static partial uint RtlNtStatusToDosError(int status);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetBasic(SafeFileHandle handle, int informationClass, out BasicInfo info, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetStandard(SafeFileHandle handle, int informationClass, out StandardInfo info, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetDisposition(SafeFileHandle handle, int informationClass, ref byte value, uint size);
}
