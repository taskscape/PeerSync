using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PeerSync.Control.Contracts;

internal sealed class VerifiedControlEndpoint
{
    internal string PipeName { get; }
    internal SecurityIdentifier Owner { get; }
    internal uint ProcessId { get; }
    internal VerifiedControlEndpoint(string pipeName, SecurityIdentifier owner, uint processId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrEmpty(pipeName) || pipeName.Length > 128 || pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) || processId == 0)
            throw new ArgumentException("Invalid local control endpoint.");
        PipeName = pipeName; Owner = owner; ProcessId = processId;
    }
    public override string ToString() => "Verified local control endpoint (redacted)";
}

internal static partial class InstalledControlEndpoint
{
    internal const string ServiceName = ControlServiceNames.ServiceName, PipeName = ControlServiceNames.PipeName;
    internal static VerifiedControlEndpoint Resolve()
    {
        var identity = ReadRunningService(ServiceName);
        return new(PipeName, identity.Owner, identity.ProcessId);
    }

    // Read-only native seam for verification against an existing Windows service.
    internal static (SecurityIdentifier Owner, uint ProcessId) ReadRunningService(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (serviceName.Length > 256 || serviceName.IndexOfAny(['/', '\\', '\0']) >= 0) throw new ArgumentException("Invalid service name.", nameof(serviceName));
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var service = OpenService(manager, serviceName, 1 | 4); // QUERY_CONFIG | QUERY_STATUS only.
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (QueryServiceStatusEx(service, 0, out ServiceStatus status, (uint)Marshal.SizeOf<ServiceStatus>(), out _) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (status.CurrentState != 4 || status.ProcessId == 0) throw new IOException("The installed control service is not running.");
        _ = QueryServiceConfig(service, 0, 0, out uint needed);
        if (Marshal.GetLastPInvokeError() != 122 || needed < Marshal.SizeOf<ServiceConfiguration>() || needed > 8192)
            throw new IOException("Cannot read bounded service configuration.");
        nint memory = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (QueryServiceConfig(service, memory, needed, out _) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var configuration = Marshal.PtrToStructure<ServiceConfiguration>(memory);
            string account = ReadAccount(memory, checked((int)needed), configuration.StartName);
            return (ResolveAccount(account), status.ProcessId);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    internal static SecurityIdentifier ResolveAccount(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) return new(WellKnownSidType.LocalSystemSid, null);
        if (account.Equals("NT AUTHORITY\\LocalService", StringComparison.OrdinalIgnoreCase)) return new(WellKnownSidType.LocalServiceSid, null);
        if (account.Equals("NT AUTHORITY\\NetworkService", StringComparison.OrdinalIgnoreCase)) return new(WellKnownSidType.NetworkServiceSid, null);
        if (account.StartsWith(".\\", StringComparison.Ordinal)) account = Environment.MachineName + account[1..];
        return (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
    }

    private static unsafe string ReadAccount(nint memory, int length, nint pointer)
    {
        long offset = checked(pointer.ToInt64() - memory.ToInt64());
        if (offset < 0 || offset > length - 2 || (offset & 1) != 0) throw new InvalidDataException("Invalid service account buffer.");
        var bytes = new ReadOnlySpan<byte>((void*)memory, length);
        var chars = MemoryMarshal.Cast<byte, char>(bytes.Slice((int)offset, (length - (int)offset) & ~1));
        int end = chars.IndexOf('\0');
        if (end <= 0) throw new InvalidDataException("Unterminated service account buffer.");
        return new(chars[..end]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    {
        public uint ServiceType, StartType, ErrorControl;
        public nint BinaryPath, LoadOrderGroup;
        public uint TagId;
        public nint Dependencies, StartName, DisplayName;
    }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle) != 0;
    }
    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    private static partial int QueryServiceConfig(ServiceHandle service, nint buffer, uint length, out uint needed);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, uint length, out uint needed);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int CloseServiceHandle(nint service);
}
