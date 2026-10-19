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
    private readonly bool ownsRoot = true;
    public SafeFileTree(string rootPath) => root = new NativeRoot(rootPath, preventRename: true);
    internal SafeFileTree(NativeRoot borrowedRoot) { root = borrowedRoot; ownsRoot = false; }
    public ReadOnlySpan<byte> Identity => root.Identity;
    public ulong CapacityBytes => NativeVolume.Capacity(root.Handle);

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

    public void Dispose() { if (ownsRoot) root.Dispose(); }

    internal static bool ComponentValid(string name, bool allowTemp) => name.Length <= 255 && !name.Contains('/')
        && (PathRules.IsValid(name) || allowTemp && name.EndsWith(".!psp", StringComparison.OrdinalIgnoreCase)
            && PathRules.IsValid(name[..^5]));

    internal static unsafe SafeFileHandle Open(SafeFileHandle parent, string name, bool? directory,
        uint access, uint share, uint disposition, uint attributes = 0, bool deleteOnClose = false)
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
                    share, disposition, 0x00200020u | (directory is true ? 1u : directory is false ? 0x44u : 0) | (deleteOnClose ? 0x1000u : 0), 0, 0);
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

    internal IEnumerable<NativeEntry> Entries(CancellationToken token) => NativeRoot.EnumerateHandle(parent, token);

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

    internal SafeFileHandle OpenReadGuard(string name)
    {
        Check(name, temporary: false);
        // Multiple readers may coexist. Exclude writers/deleters, without
        // requesting DELETE ourselves (which would exclude another reader).
        return SafeFileTree.Open(parent, name, false, 0x00100081, 1, 1);
    }

    // Generic classification guard. Only mutate directories through it: file
    // content replacement still requires OpenFile's no-write-sharing guard.
    internal SafeFileHandle OpenNode(string name, bool temporary = false)
    {
        Check(name, temporary);
        return SafeFileTree.Open(parent, name, null, 0x00130181, 3, 1);
    }

    internal SafeFileHandle OpenMetadataNode(string name)
    {
        Check(name, temporary: false);
        // Classification only: do not request data/write/delete access, so a
        // locked unknown collision need not block another no-overwrite slot.
        return SafeFileTree.Open(parent, name, null, 0x00100080, 7, 1);
    }

    internal SafeFileHandle CreateDirectory(string name)
    {
        Check(name, temporary: false);
        return SafeFileTree.Open(parent, name, true, 0x00130181, 3, 2, 0x10);
    }

    internal SafeFileHandle OwnedTemporaryDirectory(string name, bool create)
    {
        Check(name, temporary: true);
        // Exclusive owned staging directory, including WRITE_DAC for metadata preparation.
        return SafeFileTree.Open(parent, name, true, 0x00170181, 0, create ? 2u : 1u, 0x12);
    }

    // Only call after reading a persisted ownership identity; verify that identity
    // before writing. Opening never truncates an existing temporary file.
    public SafeFileHandle OpenOwnedTemporary(string name)
    {
        Check(name, temporary: true);
        return SafeFileTree.Open(parent, name, false, 0x00170187, 0, 1);
    }

    internal SafeFileHandle CreateInheritedMetadataReference()
    {
        string name = Guid.NewGuid().ToString("N") + ".!psp"; Check(name, temporary: true);
        return SafeFileTree.Open(parent, name, false, 0x00170187, 0, 2, 2, deleteOnClose: true);
    }

    public void MoveHere(SafeFileHandle file, string name, bool temporary = false)
    {
        Check(name, temporary);
        SetName(file, parent, name, infoClass: 10, flags: 0);
    }

    public void LinkHere(SafeFileHandle file, string name)
    {
        Check(name, temporary: false);
        SetName(file, parent, name, infoClass: 11, flags: 0);
    }

    /// <summary>Attempt atomic replacement while the verified original guard remains held.
    /// A no-delete-sharing guard currently causes a sharing violation even with POSIX flags;
    /// this is a capability check, not the coordinator's solved installation primitive.</summary>
    public void ReplaceHere(SafeFileHandle replacement, SafeFileHandle expectedTarget, string name)
    {
        Check(name, temporary: false);
        bool retained = false;
        try
        {
            expectedTarget.DangerousAddRef(ref retained);
            var expected = FileSnapshot.Read(expectedTarget);
            using (var current = OpenFile(name))
            {
                var actual = FileSnapshot.Read(current);
                if (expected.Volume != actual.Volume || expected.IdLow != actual.IdLow || expected.IdHigh != actual.IdHigh)
                    throw new IOException("Replacement target identity changed.");
            }
            // FileRenameInformationEx: REPLACE_IF_EXISTS | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE.
            SetName(replacement, parent, name, infoClass: 65, flags: 0x43);
        }
        finally { if (retained) expectedTarget.DangerousRelease(); }
    }

    private void Check(string name, bool temporary)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!SafeFileTree.ComponentValid(name, temporary)) throw new IOException("Invalid file component.");
    }

    private static unsafe void SetName(SafeFileHandle source, SafeFileHandle destination, string name, int infoClass, uint flags)
    {
        // FILE_RENAME/LINK_INFORMATION: BOOLEAN/Flags at 0, HANDLE at 8,
        // ULONG filename bytes at 16, WCHAR name at 20, minimum size 24 on x64/Arm64.
        byte[] buffer = new byte[Math.Max(24, 20 + name.Length * 2)];
        bool retained = false;
        try
        {
            destination.DangerousAddRef(ref retained);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, flags);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), destination.DangerousGetHandle());
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), checked((uint)(name.Length * 2)));
            MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(buffer.AsSpan(20));
            fixed (byte* pointer = buffer)
            {
                int status = NativeMutations.NtSetInformationFile(source, out _, pointer, (uint)buffer.Length, infoClass);
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
