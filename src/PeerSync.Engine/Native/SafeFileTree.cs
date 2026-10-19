using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PeerSync.Core.Paths;
using PeerSync.Engine.Hashing;

namespace PeerSync.Engine.Native;

/// <summary>Mutation primitives on a pinned root. Dispose leases before the tree.</summary>
internal sealed class SafeFileTree : IDisposable
{
    private readonly NativeRoot root;
    public SafeFileTree(string rootPath) => root = new NativeRoot(rootPath, preventRename: true);
    public ReadOnlySpan<byte> Identity => root.Identity;

    public DirectoryLease Directory(string path, bool create = false, bool privatePath = false)
    {
        if (path.Length > (privatePath ? 4109 : 4096)) throw new IOException("Directory path exceeds the internal bound.");
        string[] parts = path.Length == 0 ? [] : path.Split('/');
        for (int i = 0; i < parts.Length; i++)
            if (!(privatePath && i == 0 && parts[i] == ".psp") && !ComponentValid(parts[i], allowTemp: false))
                throw new IOException("Invalid directory component.");
        var handles = new List<SafeFileHandle>();
        try
        {
            var current = root.Handle;
            foreach (string part in parts)
            {
                var next = Open(current, part, directory: true, access: 0x00100081, share: 3,
                    disposition: create ? 3u : 1u, attributes: 0x10); // FILE_OPEN_IF or FILE_OPEN.
                handles.Add(next); current = next;
            }
            return new(current, handles);
        }
        catch { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); throw; }
    }

    public void Dispose() => root.Dispose();

    internal static bool ComponentValid(string name, bool allowTemp) => name.Length <= 255 && !name.Contains('/')
        && (PathRules.IsValid(name) || allowTemp && name.EndsWith(".!psp", StringComparison.OrdinalIgnoreCase)
            && PathRules.IsValid(name[..^5]));

    internal static unsafe SafeFileHandle Open(SafeFileHandle parent, string name, bool directory,
        uint access, uint share, uint disposition, uint attributes = 0)
    {
        fixed (char* pointer = name)
        {
            var unicode = new NativeFiles.UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = pointer };
            bool retained = false;
            try
            {
                parent.DangerousAddRef(ref retained);
                var objectAttributes = new NativeFiles.ObjectAttributes
                {
                    Length = (uint)sizeof(NativeFiles.ObjectAttributes), RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = &unicode, Attributes = 0x1040,
                };
                int status = NativeFiles.NtCreateFile(out var handle, access, in objectAttributes, out _, 0, attributes,
                    share, disposition, 0x00200020u | (directory ? 1u : 0x44u), 0, 0);
                if (status < 0) { handle.Dispose(); throw new Win32Exception((int)NativeFiles.RtlNtStatusToDosError(status)); }
                try
                {
                    if (!NativeFileInfo.GetBasic(handle, 0, out var basic, (uint)Marshal.SizeOf<NativeFileInfo.BasicInfo>()))
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    if (((FileAttributes)basic.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Reparse points cannot be traversed.");
                }
                catch { handle.Dispose(); throw; }
                return handle;
            }
            finally { if (retained) parent.DangerousRelease(); }
        }
    }
}

internal sealed class DirectoryLease : IDisposable
{
    private readonly SafeFileHandle parent;
    private readonly List<SafeFileHandle> owned;
    private bool disposed;
    internal DirectoryLease(SafeFileHandle parent, List<SafeFileHandle> owned) { this.parent = parent; this.owned = owned; }

    public SafeFileHandle CreateFile(string name, bool temporary = false)
    {
        Check(name, temporary);
        // Read/write, DELETE, READ_CONTROL and WRITE_DAC. Created files inherit this directory's DACL.
        return SafeFileTree.Open(parent, name, false, 0x00170187, 0, 2, temporary ? 2u : 0x80u);
    }

    public SafeFileHandle OpenFile(string name, bool mutation = false, bool temporary = false)
    {
        Check(name, temporary);
        // A mutation guard excludes other writers/deleters. Its own DELETE access supports rename by handle.
        return SafeFileTree.Open(parent, name, false, mutation ? 0x00130181u : 0x00100081u, mutation ? 1u : 7u, 1);
    }

    public void MoveHere(SafeFileHandle file, string name, bool temporary = false)
    {
        Check(name, temporary);
        Rename(file, parent, name);
    }

    private void Check(string name, bool temporary)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!SafeFileTree.ComponentValid(name, temporary)) throw new IOException("Invalid file component.");
    }

    private static unsafe void Rename(SafeFileHandle source, SafeFileHandle destination, string name)
    {
        // FILE_RENAME_INFORMATION (class 10): BOOLEAN at 0, HANDLE at 8,
        // ULONG filename bytes at 16, WCHAR name at 20, minimum size 24 on x64/Arm64.
        byte[] buffer = new byte[Math.Max(24, 20 + name.Length * 2)];
        bool retained = false;
        try
        {
            destination.DangerousAddRef(ref retained);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), destination.DangerousGetHandle());
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), checked((uint)(name.Length * 2)));
            MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(buffer.AsSpan(20));
            fixed (byte* pointer = buffer)
            {
                int status = NativeMutations.NtSetInformationFile(source, out _, pointer, (uint)buffer.Length, 10);
                if (status < 0) throw new Win32Exception((int)NativeFiles.RtlNtStatusToDosError(status));
            }
        }
        finally { if (retained) destination.DangerousRelease(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var handle in owned.AsEnumerable().Reverse()) handle.Dispose();
    }
}

internal static partial class NativeMutations
{
    [LibraryImport("ntdll.dll")]
    internal static unsafe partial int NtSetInformationFile(SafeFileHandle file, out NativeFiles.IoStatusBlock status,
        void* information, uint size, int infoClass);
}
