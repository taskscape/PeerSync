using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using PeerSync.Core.Settings;
using PeerSync.Service.Settings;

namespace PeerSync.Service.Init;

internal interface IInitPlatform
{
    bool IsWindows { get; }
    bool IsAdministrator { get; }
    string DataRoot { get; }
    string CurrentUserSid { get; }
    SecurityIdentifier ServiceSid { get; }
    SecurityIdentifier PeerSyncUsersSid { get; }
    int FindAvailablePort();
    void EnsureDirectories(string root, SecurityIdentifier serviceSid, SecurityIdentifier readersSid);
    string EnsureDeviceCertificate(SecurityIdentifier serviceSid, string? knownThumbprint);
    void EnsureEventSource();
}

internal static class InitCommand
{
    internal static int Run(string[] args) => Run(args, new WindowsInitPlatform(), Console.Out, Console.Error);

    internal static int Run(string[] args, IInitPlatform platform, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(platform);
        if (args.Length != 1 || !string.Equals(args[0], "--init", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("Usage: PeerSync.Service.exe --init");
            return 2;
        }
        if (!platform.IsWindows)
        {
            error.WriteLine("--init is supported only on Windows.");
            return 2;
        }
        if (!platform.IsAdministrator)
        {
            error.WriteLine("Run --init from an elevated administrator process.");
            return 5;
        }

        try
        {
            var settingsPath = Path.Combine(platform.DataRoot, "config", "settings.json");
            var store = new SettingsStore(settingsPath);
            var settings = store.ReadIfPresent(error);
            var originalThumbprint = settings?.DeviceCertificateThumbprint;
            platform.EnsureDirectories(platform.DataRoot, platform.ServiceSid, platform.PeerSyncUsersSid);
            var thumbprint = platform.EnsureDeviceCertificate(platform.ServiceSid, settings?.DeviceCertificateThumbprint);
            settings ??= new DeviceSettings
            {
                ListenPort = platform.FindAvailablePort(),
                DeviceCertificateThumbprint = thumbprint,
                ControlUserSids = [platform.CurrentUserSid],
            };
            if (!string.Equals(settings.DeviceCertificateThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                settings = settings with { DeviceCertificateThumbprint = thumbprint };
            platform.EnsureEventSource();
            if (originalThumbprint is null || !string.Equals(originalThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                store.Save(settings);
            output.WriteLine("PeerSync initialization complete.");
            return 0;
        }
        catch (Exception exception)
        {
            error.WriteLine($"PeerSync initialization failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }
}

internal sealed class WindowsInitPlatform : IInitPlatform
{
    private const string EventSource = "PeerSync";
    private const string EventLogName = "Application";
    // NCryptSetProperty requires DACL_SECURITY_INFORMATION (0x4) when updating
    // NCRYPT_SECURITY_DESCR_PROPERTY, in addition to CngPropertyOptions.Persist.
    private static readonly CngPropertyOptions PersistDaclSecurityDescriptor =
        CngPropertyOptions.Persist | (CngPropertyOptions)0x4;
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsAdministrator => IsCurrentUserAdministrator();
    public string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeerSync");
    public string CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return (identity.User ?? throw new InvalidOperationException("The setup process has no Windows SID.")).Value;
        }
    }
    public SecurityIdentifier ServiceSid => ResolveSid(new NTAccount("NT SERVICE", "PeerSync"));
    public SecurityIdentifier PeerSyncUsersSid => ResolveSid(new NTAccount(Environment.MachineName, "PeerSync Users"));

    public int FindAvailablePort()
    {
        var activeTcp = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet();
        var activeUdp = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Select(endpoint => endpoint.Port).ToHashSet();
        var random = RandomNumberGenerator.GetInt32(20000, 60001);
        for (var attempt = 0; attempt < 40001; attempt++)
        {
            var port = 20000 + ((random - 20000 + attempt) % 40001);
            if (activeTcp.Contains(port) || activeUdp.Contains(port)) continue;
            try
            {
                using var tcp = new TcpListener(IPAddress.Any, port);
                tcp.Start();
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                return port;
            }
            catch (SocketException) { }
        }
        throw new IOException("No free TCP and UDP listener port was found in 20000-60000.");
    }

    public void EnsureDirectories(string root, SecurityIdentifier serviceSid, SecurityIdentifier readersSid)
    {
        CreateSecureDirectory(Path.Combine(root, "config"), serviceSid, FileSystemRights.Modify);
        CreateSecureDirectory(Path.Combine(root, "state"), serviceSid, FileSystemRights.Modify);
        CreateSecureDirectory(Path.Combine(root, "logs"), serviceSid, FileSystemRights.Modify, readersSid);
        // The service may stage untrusted downloads, but cannot change the
        // administrator's publisher pin or update enablement policy.
        CreateSecureDirectory(Path.Combine(root, "updates"), serviceSid, FileSystemRights.Modify);
        CreateSecureDirectory(Path.Combine(root, "update-policy"), serviceSid, FileSystemRights.ReadAndExecute);
        CreateSecureDirectory(Path.Combine(root, "update-trusted"), serviceSid, null);
    }

    public string EnsureDeviceCertificate(SecurityIdentifier serviceSid, string? knownThumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        if (!string.IsNullOrWhiteSpace(knownThumbprint))
        {
            using var existing = store.Certificates.Find(X509FindType.FindByThumbprint, knownThumbprint, validOnly: false)
                .OfType<X509Certificate2>().FirstOrDefault(certificate => certificate.HasPrivateKey);
            if (existing is not null && existing.GetECDsaPrivateKey() is ECDsaCng existingKey)
            {
                using (existingKey) existingKey.Key.SetProperty(new CngProperty("Security Descr", CreateKeySecurityDescriptor(serviceSid), PersistDaclSecurityDescriptor));
                return existing.Thumbprint!;
            }
        }

        var keyName = "PeerSync-Device-" + Guid.NewGuid().ToString("N");
        using var key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyCreationOptions = CngKeyCreationOptions.MachineKey,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
        });
        key.SetProperty(new CngProperty("Security Descr", CreateKeySecurityDescriptor(serviceSid), PersistDaclSecurityDescriptor));
        using var ecdsa = new ECDsaCng(key);
        var request = new CertificateRequest("CN=PeerSync Device", ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        certificate.FriendlyName = "PeerSync Device";
        store.Add(certificate);
        return certificate.Thumbprint ?? throw new CryptographicException("The device certificate has no thumbprint.");
    }

    public void EnsureEventSource()
    {
        if (EventLog.SourceExists(EventSource))
        {
            if (!string.Equals(EventLog.LogNameFromSourceName(EventSource, "."), EventLogName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The PeerSync event source is registered to a different event log.");
            return;
        }
        EventLog.CreateEventSource(EventSource, EventLogName);
    }

    private static SecurityIdentifier ResolveSid(NTAccount account) => (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));

    private static bool IsCurrentUserAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static byte[] CreateKeySecurityDescriptor(SecurityIdentifier serviceSid)
    {
        var descriptor = new RawSecurityDescriptor($"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;{serviceSid.Value})");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static void CreateSecureDirectory(string path, SecurityIdentifier serviceSid, FileSystemRights? serviceRights, SecurityIdentifier? readersSid = null)
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (serviceRights is { } rights)
            security.AddAccessRule(new FileSystemAccessRule(serviceSid, rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (readersSid is not null)
            security.AddAccessRule(new FileSystemAccessRule(readersSid, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = Directory.CreateDirectory(path);
        directory.SetAccessControl(security);
    }
}
