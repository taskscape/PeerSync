using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using PeerSync.Service.Settings;

namespace PeerSync.Service.Init;

internal interface IRemoveDataPlatform
{
    bool IsWindows { get; }
    bool IsAdministrator { get; }
    string DataRoot { get; }
    string ExpectedDataRoot { get; }
    void StopService(TimeSpan timeout);
    string? ReadDeviceCertificateThumbprint();
    void ValidateDataTree();
    void RemoveDeviceCertificates(string? knownThumbprint);
    void DeleteDataTree();
}

internal static class RemoveDataCommand
{
    internal static int Run(string[] args) => Run(args, new WindowsRemoveDataPlatform(), Console.Out, Console.Error);

    internal static int Run(string[] args, IRemoveDataPlatform platform, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(platform);
        if (args.Length != 1 || !string.Equals(args[0], "--remove-data", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("Usage: PeerSync.Service.exe --remove-data");
            return 2;
        }
        if (!platform.IsWindows)
        {
            error.WriteLine("--remove-data is supported only on Windows.");
            return 2;
        }
        if (!platform.IsAdministrator)
        {
            error.WriteLine("Run --remove-data from an elevated administrator process.");
            return 5;
        }

        try
        {
            platform.StopService(TimeSpan.FromSeconds(30));
            var thumbprint = platform.ReadDeviceCertificateThumbprint();
            platform.ValidateDataTree();
            platform.RemoveDeviceCertificates(thumbprint);
            platform.DeleteDataTree();
            output.WriteLine("PeerSync device data and certificate removed. Synced folders were not changed.");
            return 0;
        }
        catch (Exception exception)
        {
            error.WriteLine($"PeerSync data removal failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }
}

internal sealed class WindowsRemoveDataPlatform : IRemoveDataPlatform
{
    private const string DeviceSubject = "CN=PeerSync Device";
    private readonly string expectedDataRoot;

    internal WindowsRemoveDataPlatform(string? expectedDataRoot = null)
    {
        this.expectedDataRoot = expectedDataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeerSync");
    }

    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
    public string DataRoot => expectedDataRoot;
    public string ExpectedDataRoot => expectedDataRoot;

    public void StopService(TimeSpan timeout)
    {
        var services = ServiceController.GetServices();
        try
        {
            var service = services.FirstOrDefault(candidate => string.Equals(candidate.ServiceName, "PeerSync", StringComparison.OrdinalIgnoreCase));
            if (service is null) return;
            var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            service.Refresh();
            if (service.Status == ServiceControllerStatus.StartPending)
                service.WaitForStatus(ServiceControllerStatus.Running, Remaining(deadline));
            service.Refresh();
            if (service.Status == ServiceControllerStatus.Stopped) return;
            if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, Remaining(deadline));
        }
        finally
        {
            foreach (var service in services) service.Dispose();
        }
    }

    public string? ReadDeviceCertificateThumbprint()
    {
        var path = Path.Combine(DataRoot, "config", "settings.json");
        return new SettingsStore(path).ReadIfPresent()?.DeviceCertificateThumbprint;
    }

    public void ValidateDataTree()
    {
        var expected = Path.GetFullPath(ExpectedDataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var actual = Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete data outside the configured PeerSync ProgramData directory.");
        if (!Directory.Exists(actual)) return;
        ValidateNoReparsePoints(actual);
    }

    public void RemoveDeviceCertificates(string? knownThumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        var matches = store.Certificates.OfType<X509Certificate2>()
            .Where(certificate => string.Equals(certificate.Subject, DeviceSubject, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(knownThumbprint) && string.Equals(certificate.Thumbprint, knownThumbprint, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var candidates = new List<(X509Certificate2 Certificate, ECDsa? PrivateKey)>();
        try
        {
            foreach (var certificate in matches)
            {
                var privateKey = certificate.GetECDsaPrivateKey();
                if (privateKey is not null && privateKey is not ECDsaCng)
                {
                    privateKey.Dispose();
                    throw new InvalidOperationException("The PeerSync device certificate uses an unsupported private-key provider; refusing partial removal.");
                }
                candidates.Add((certificate, privateKey));
            }

            foreach (var (certificate, privateKey) in candidates)
            {
                store.Remove(certificate);
                if (privateKey is ECDsaCng cng) cng.Key.Delete();
            }
        }
        finally
        {
            foreach (var (certificate, privateKey) in candidates)
            {
                privateKey?.Dispose();
                certificate.Dispose();
            }
            foreach (var certificate in matches.Except(candidates.Select(candidate => candidate.Certificate)))
                certificate.Dispose();
        }
    }

    public void DeleteDataTree()
    {
        var path = Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void ValidateNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to remove PeerSync data containing a reparse point.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing to remove PeerSync data containing a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static TimeSpan Remaining(long deadline)
    {
        var ticks = deadline - Stopwatch.GetTimestamp();
        if (ticks <= 0) throw new System.TimeoutException("The PeerSync service did not stop within 30 seconds.");
        return TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }
}
