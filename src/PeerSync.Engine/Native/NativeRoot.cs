using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PeerSync.Core.Paths;
using PeerSync.Engine.Hashing;

namespace PeerSync.Engine.Native;

internal sealed class NativeRoot : IDisposable
{
    private readonly SafeFileHandle root;
    public ulong Volume { get; }
    public byte[] Identity { get; }

    internal SafeFileHandle Handle => root;

    public NativeRoot(string path, bool preventRename = false)
    {
        root = NativeFiles.CreateFile(path, 0x81, preventRename ? 3u : 7u, 0, 3, 0x02200000, 0); // LIST_DIRECTORY | READ_ATTRIBUTES, BACKUP_SEMANTICS | OPEN_REPARSE_POINT.
        if (root.IsInvalid) { int error = Marshal.GetLastPInvokeError(); root.Dispose(); throw new Win32Exception(error); }
        try
        {
            if (!NativeFileInfo.GetBasic(root, 0, out var basic, (uint)Marshal.SizeOf<NativeFileInfo.BasicInfo>()))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var attributes = (FileAttributes)basic.Attributes;
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Scan root must be a regular directory.");
            if (!NativeFileInfo.GetIdentity(root, 18, out var identity, 24)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            Volume = identity.VolumeSerial;
            Identity = new byte[24];
            BinaryPrimitives.WriteUInt64LittleEndian(Identity, identity.VolumeSerial);
            BinaryPrimitives.WriteUInt64LittleEndian(Identity.AsSpan(8), identity.IdLow);
            BinaryPrimitives.WriteUInt64LittleEndian(Identity.AsSpan(16), identity.IdHigh);
        }
        catch { root.Dispose(); throw; }
    }

    public SafeFileHandle OpenFile(string path) => Open(path, directory: false);

    public IEnumerable<NativeEntry> Enumerate(string relativeDirectory, CancellationToken cancellationToken)
    {
        using var directory = relativeDirectory.Length == 0 ? null : Open(relativeDirectory, directory: true);
        SafeFileHandle handle = directory ?? root;
        foreach (var entry in EnumerateHandle(handle, cancellationToken)) yield return entry;
    }

    internal static IEnumerable<NativeEntry> EnumerateHandle(SafeFileHandle handle, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            bool first = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool success = Query(handle, first ? 20 : 19, buffer);
                first = false;
                if (!success)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == 18) yield break; // ERROR_NO_MORE_FILES.
                    throw new Win32Exception(error);
                }
                foreach (var entry in Parse(buffer))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Name is not ("." or "..")) yield return entry;
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static unsafe bool Query(SafeFileHandle handle, int infoClass, byte[] buffer)
    {
        fixed (byte* pointer = buffer) return NativeFiles.GetDirectoryInfo(handle, infoClass, pointer, 65536);
    }

    internal static IReadOnlyList<NativeEntry> Parse(ReadOnlySpan<byte> buffer)
    {
        var result = new List<NativeEntry>();
        int offset = 0;
        while (true)
        {
            if (buffer.Length - offset < 88) throw new InvalidDataException("Truncated directory information.");
            var record = buffer[offset..];
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(record);
            uint nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(record[60..]);
            if (nameBytes == 0 || (nameBytes & 1) != 0 || nameBytes > record.Length - 88
                || (next != 0 && ((next & 7) != 0 || next < 88 + nameBytes || next > record.Length - 88)))
                throw new InvalidDataException("Invalid directory information offsets.");
            string name = new(MemoryMarshal.Cast<byte, char>(record.Slice(88, (int)nameBytes)));
            var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[56..]);
            long length = (attributes & FileAttributes.Directory) != 0 ? 0 : BinaryPrimitives.ReadInt64LittleEndian(record[40..]);
            if (length < 0) throw new InvalidDataException("Invalid directory file length.");
            result.Add(new(name, attributes, length, BinaryPrimitives.ReadInt64LittleEndian(record[24..]),
                BinaryPrimitives.ReadInt64LittleEndian(record[32..]), record.Slice(72, 16).ToArray()));
            if (next == 0) return result;
            offset = checked(offset + (int)next);
        }
    }

    private unsafe SafeFileHandle Open(string path, bool directory)
    {
        if (!PathRules.IsValid(path)) throw new IOException("Invalid relative scan path.");
        string name = path.Replace('/', '\\');
        fixed (char* pointer = name)
        {
            var unicode = new NativeFiles.UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = pointer };
            bool retained = false;
            try
            {
                root.DangerousAddRef(ref retained);
                var attributes = new NativeFiles.ObjectAttributes
                {
                    Length = (uint)sizeof(NativeFiles.ObjectAttributes), RootDirectory = root.DangerousGetHandle(),
                    ObjectName = &unicode, Attributes = 0x1040, // OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE.
                };
                uint options = 0x00200020u | (directory ? 1u : 0x44u); // OPEN_REPARSE_POINT | SYNCHRONOUS_IO_NONALERT; directory or non-directory/sequential.
                int status = NativeFiles.NtCreateFile(out var handle, 0x00100081, in attributes, out _, 0, 0, 7, 1, options, 0, 0);
                if (status < 0)
                {
                    handle.Dispose();
                    throw new Win32Exception((int)NativeFiles.RtlNtStatusToDosError(status));
                }
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
            finally { if (retained) root.DangerousRelease(); }
        }
    }

    public void Dispose() => root.Dispose();
}

internal sealed record NativeEntry(string Name, FileAttributes Attributes, long Length,
    long LastWriteFileTime, long ChangeFileTime, byte[] FileId)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public bool IsReparse => (Attributes & FileAttributes.ReparsePoint) != 0;
    public long MtimeTicks => DateTime.FromFileTimeUtc(LastWriteFileTime).Ticks;
}

internal static partial class NativeFiles
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct UnicodeString { public ushort Length; public ushort MaximumLength; public char* Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ObjectAttributes
    {
        public uint Length; public nint RootDirectory; public UnicodeString* ObjectName;
        public uint Attributes; public nint SecurityDescriptor; public nint SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoStatusBlock { public nint Status; public nuint Information; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetDirectoryInfo(SafeFileHandle handle, int infoClass, void* buffer, uint size);
    [LibraryImport("ntdll.dll")]
    internal static unsafe partial int NtCreateFile(out SafeFileHandle handle, uint access, in ObjectAttributes attributes,
        out IoStatusBlock status, nint allocationSize, uint fileAttributes, uint share, uint disposition, uint options, nint eaBuffer, uint eaLength);
    [LibraryImport("ntdll.dll")]
    internal static partial uint RtlNtStatusToDosError(int status);
}
