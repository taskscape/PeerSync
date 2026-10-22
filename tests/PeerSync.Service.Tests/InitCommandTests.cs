using System.Security.Principal;
using System.Net;
using System.Net.Sockets;
using PeerSync.Service.Init;
using PeerSync.Service.Settings;
using Shouldly;
using Xunit;

namespace PeerSync.Service.Tests;

public sealed class InitCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PeerSync.Init.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void InitCreatesDefaultSettingsAndSecondRunIsNoOp()
    {
        var platform = new FakePlatform(root);
        using var output = new StringWriter();
        using var error = new StringWriter();

        InitCommand.Run(["--init"], platform, output, error).ShouldBe(0);
        var path = Path.Combine(root, "config", "settings.json");
        var initial = File.ReadAllBytes(path);
        var settings = System.Text.Json.JsonDocument.Parse(initial).RootElement;
        settings.GetProperty("listenPort").GetInt32().ShouldBeInRange(20000, 60000);
        settings.GetProperty("deviceCertificateThumbprint").GetString().ShouldBe(FakePlatform.Thumbprint);
        settings.GetProperty("controlUserSids")[0].GetString().ShouldBe(FakePlatform.UserSid.Value);

        InitCommand.Run(["--init"], platform, output, error).ShouldBe(0);
        File.ReadAllBytes(path).ShouldBe(initial);
        platform.CertificateCalls.ShouldBe(2);
        platform.PortCalls.ShouldBe(1);
        platform.DirectoryCalls.ShouldBe(2);
        platform.EventCalls.ShouldBe(2);
    }

    [Fact]
    public void InitPreservesSettingsAndRepairsMissingCertificateReference()
    {
        var platform = new FakePlatform(root);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var original = """{"schemaVersion":1,"deviceName":"Saved Device","listenPort":12345,"usePortMapping":false,"controlUserSids":["S-1-5-21-1-2-3-1001"],"deviceCertificateThumbprint":"OLD"}""";
        File.WriteAllText(Path.Combine(root, "config", "settings.json"), original);
        using var output = new StringWriter();
        using var error = new StringWriter();

        InitCommand.Run(["--init"], platform, output, error).ShouldBe(0);
        var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config", "settings.json"))).RootElement;
        settings.GetProperty("deviceName").GetString().ShouldBe("Saved Device");
        settings.GetProperty("listenPort").GetInt32().ShouldBe(12345);
        settings.GetProperty("usePortMapping").GetBoolean().ShouldBeFalse();
        settings.GetProperty("deviceCertificateThumbprint").GetString().ShouldBe(FakePlatform.Thumbprint);
        platform.PortCalls.ShouldBe(0);
    }

    [Fact]
    public void InitRequiresElevationAndValidArgumentsBeforeMachineChanges()
    {
        var platform = new FakePlatform(root) { IsAdministrator = false };
        using var output = new StringWriter();
        using var error = new StringWriter();

        InitCommand.Run(["--init"], platform, output, error).ShouldBe(5);
        InitCommand.Run(["--init", "extra"], platform, output, error).ShouldBe(2);
        platform.DirectoryCalls.ShouldBe(0);
        platform.CertificateCalls.ShouldBe(0);
    }

    [Fact]
    public void SettingsStoreReplacesAtomicallyAndKeepsBackup()
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var path = Path.Combine(root, "config", "settings.json");
        var store = new SettingsStore(path);
        store.Save(new PeerSync.Core.Settings.DeviceSettings { ListenPort = 20001, DeviceCertificateThumbprint = FakePlatform.Thumbprint, ControlUserSids = [FakePlatform.UserSid.Value] });
        store.Save(new PeerSync.Core.Settings.DeviceSettings { ListenPort = 20002, DeviceCertificateThumbprint = FakePlatform.Thumbprint, ControlUserSids = [FakePlatform.UserSid.Value] });

        File.Exists(path + ".bak").ShouldBeTrue();
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("listenPort").GetInt32().ShouldBe(20002);
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(path + ".bak")).RootElement.GetProperty("listenPort").GetInt32().ShouldBe(20001);
    }

    [Fact]
    public void SettingsStoreRecoversCorruptSettingsFromBackup()
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var path = Path.Combine(root, "config", "settings.json");
        var store = new SettingsStore(path);
        store.Save(new PeerSync.Core.Settings.DeviceSettings { ListenPort = 20002, DeviceCertificateThumbprint = FakePlatform.Thumbprint, ControlUserSids = [FakePlatform.UserSid.Value] });
        File.Copy(path, path + ".bak");
        File.WriteAllText(path, "{broken-json");
        using var warnings = new StringWriter();

        var recovered = store.ReadIfPresent(out var wasRecovered, warnings);

        recovered!.ListenPort.ShouldBe(20002);
        wasRecovered.ShouldBeTrue();
        warnings.ToString().ShouldContain("Recovered settings.json from settings.json.bak.");
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("listenPort").GetInt32().ShouldBe(20002);
    }

    [Fact]
    public void SettingsStoreReportsMissingPrimaryAndBackup()
    {
        var path = Path.Combine(root, "config", "settings.json");
        var settings = new SettingsStore(path).ReadIfPresent(out var wasRecovered);

        settings.ShouldBeNull();
        wasRecovered.ShouldBeFalse();
    }

    [Fact]
    public void DeviceSettingsValidatorRejectsInvalidPortAndSchema()
    {
        var validator = new DeviceSettingsValidator();
        validator.Validate(null, new PeerSync.Core.Settings.DeviceSettings
        {
            ListenPort = 0,
            DeviceCertificateThumbprint = FakePlatform.Thumbprint,
            ControlUserSids = [FakePlatform.UserSid.Value],
        }).Failed.ShouldBeTrue();
        validator.Validate(null, new PeerSync.Core.Settings.DeviceSettings
        {
            SchemaVersion = 2,
            ListenPort = 47821,
            DeviceCertificateThumbprint = FakePlatform.Thumbprint,
            ControlUserSids = [FakePlatform.UserSid.Value],
        }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void WindowsPortSelectionReturnsPortBindableForBothTransports()
    {
        var port = new WindowsInitPlatform().FindAvailablePort();
        port.ShouldBeInRange(20000, 60000);
        using var tcp = new TcpListener(IPAddress.Any, port);
        tcp.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
    }

    [Fact]
    public void RemoveDataStopsServiceAndRemovesOnlyConfiguredPeerSyncData()
    {
        var dataRoot = Path.Combine(root, "PeerSync");
        var syncedFolder = Path.Combine(root, "Synced");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(syncedFolder);
        File.WriteAllText(Path.Combine(dataRoot, "marker.txt"), "service state");
        var syncedFile = Path.Combine(syncedFolder, "keep.txt");
        File.WriteAllText(syncedFile, "synced content");
        var platform = new FakeRemoveDataPlatform(dataRoot);
        using var output = new StringWriter();
        using var error = new StringWriter();

        RemoveDataCommand.Run(["--remove-data"], platform, output, error).ShouldBe(0);

        platform.Calls.ShouldBe(["stop", "read-certificate", "validate-tree", "remove-certificate", "delete-tree"]);
        platform.StopTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        Directory.Exists(dataRoot).ShouldBeTrue(); // fake records the destructive step without performing it
        File.Exists(syncedFile).ShouldBeTrue();
        output.ToString().ShouldContain("Synced folders were not changed.");
    }

    [Fact]
    public void WindowsRemoveDataDeletesConfiguredTreeAndLeavesSiblingFolder()
    {
        var dataRoot = Path.Combine(root, "PeerSync");
        var syncedFolder = Path.Combine(root, "Synced");
        Directory.CreateDirectory(Path.Combine(dataRoot, "state"));
        Directory.CreateDirectory(syncedFolder);
        File.WriteAllText(Path.Combine(dataRoot, "state", "marker.db"), "fixture");
        var syncedFile = Path.Combine(syncedFolder, "keep.txt");
        File.WriteAllText(syncedFile, "synced content");
        var platform = new WindowsRemoveDataPlatform(dataRoot);

        platform.ValidateDataTree();
        platform.DeleteDataTree();

        Directory.Exists(dataRoot).ShouldBeFalse();
        File.ReadAllText(syncedFile).ShouldBe("synced content");
    }

    [Fact]
    public void RemoveDataRequiresElevationAndExactArguments()
    {
        var platform = new FakeRemoveDataPlatform(root) { IsAdministrator = false };
        using var output = new StringWriter();
        using var error = new StringWriter();

        RemoveDataCommand.Run(["--remove-data"], platform, output, error).ShouldBe(5);
        RemoveDataCommand.Run(["--remove-data", "extra"], platform, output, error).ShouldBe(2);
        platform.Calls.ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class FakePlatform(string dataRoot) : IInitPlatform
    {
        public static readonly SecurityIdentifier UserSid = new("S-1-5-21-1-2-3-1001");
        public static readonly SecurityIdentifier ServiceAccountSid = new("S-1-5-80-1-2-3-4-5");
        public const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";
        public bool IsWindows => true;
        public bool IsAdministrator { get; set; } = true;
        public string DataRoot => dataRoot;
        public string CurrentUserSid => UserSid.Value;
        public SecurityIdentifier ServiceSid => ServiceAccountSid;
        public SecurityIdentifier PeerSyncUsersSid => new("S-1-5-32-545");
        public int DirectoryCalls { get; private set; }
        public int CertificateCalls { get; private set; }
        public int PortCalls { get; private set; }
        public int EventCalls { get; private set; }
        public int FindAvailablePort() { PortCalls++; return 30001; }
        public void EnsureDirectories(string root, SecurityIdentifier serviceSid, SecurityIdentifier readersSid) => DirectoryCalls++;
        public string EnsureDeviceCertificate(SecurityIdentifier serviceSid, string? knownThumbprint) { CertificateCalls++; return Thumbprint; }
        public void EnsureEventSource() => EventCalls++;
    }

    private sealed class FakeRemoveDataPlatform(string root) : IRemoveDataPlatform
    {
        public bool IsWindows => true;
        public bool IsAdministrator { get; set; } = true;
        public string DataRoot => root;
        public string ExpectedDataRoot => root;
        public List<string> Calls { get; } = [];
        public TimeSpan StopTimeout { get; private set; }
        public void StopService(TimeSpan timeout) { Calls.Add("stop"); StopTimeout = timeout; }
        public string? ReadDeviceCertificateThumbprint() { Calls.Add("read-certificate"); return "test-thumbprint"; }
        public void ValidateDataTree() => Calls.Add("validate-tree");
        public void RemoveDeviceCertificates(string? knownThumbprint) { knownThumbprint.ShouldBe("test-thumbprint"); Calls.Add("remove-certificate"); }
        public void DeleteDataTree() => Calls.Add("delete-tree");
    }
}
