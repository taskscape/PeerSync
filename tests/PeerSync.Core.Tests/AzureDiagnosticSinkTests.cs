using System.Net;
using System.Text.Json;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Telemetry;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed class AzureDiagnosticSinkTests
{
    [Fact]
    public void AzureIsEnabledByDefaultAndCanBeDisabled()
    {
        var settings = new DeviceSettings();
        settings.SendDiagnosticsToAzure.ShouldBeTrue();
        settings.FullDiagnostics.ShouldBeFalse();
        (settings with { SendDiagnosticsToAzure = false }).SendDiagnosticsToAzure.ShouldBeFalse();
        JsonSerializer.Deserialize<DeviceSettings>("{}").ShouldNotBeNull()
            .SendDiagnosticsToAzure.ShouldBeTrue();
    }

    [Fact]
    public void DefaultCaptureKeepsFailurePathAndDropsRoutineIdleTraffic()
    {
        AzureDiagnosticSink.ShouldSend(new("tracker.session_failed", "rendezvous")
        { Level = DiagnosticLevel.Warning }, fullDiagnosis: false).ShouldBeTrue();
        AzureDiagnosticSink.ShouldSend(new("service.peer_candidate_inventory", "dial"), false).ShouldBeTrue();
        AzureDiagnosticSink.ShouldSend(new("service.folder_sync_pass", "sync"), false).ShouldBeFalse();
        AzureDiagnosticSink.ShouldSend(new("service.folder_sync_pass", "sync")
        { ItemsChanged = 1 }, false).ShouldBeTrue();
        AzureDiagnosticSink.ShouldSend(new("transport.request_start", "request"), false).ShouldBeFalse();
        AzureDiagnosticSink.ShouldSend(new("transport.request_start", "request"), true).ShouldBeTrue();
        AzureDiagnosticSink.ShouldSend(new("azure.exporter_warning", "telemetry")
        { Level = DiagnosticLevel.Warning }, true).ShouldBeFalse();
    }

    [Fact]
    public void ExportFieldsSendOnlyAddressClassByDefaultAndExactAddressInFullMode()
    {
        var entry = new DiagnosticEvent("connection.candidate_failed", "public_tcp")
        {
            Level = DiagnosticLevel.Warning,
            FolderFingerprint = "0123456789ABCDEF",
            PeerFingerprint = "FEDCBA9876543210",
            RemoteAddress = IPAddress.Parse("203.0.113.42"), RemotePort = 443,
            Exception = new IOException("secret-path-C:/private/file.txt and share-key-secret"),
            ErrorCode = "SocketException", NativeError = 10060,
        };
        var fields = AzureDiagnosticSink.CreateFields(entry, DateTime.UtcNow,
            "b831eb836ce4421c8b481e4879da571a", "1A2B3C4D5E6F7890");
        string serialized = string.Join('|', fields.Select(item => item.Key + '=' + item.Value));
        serialized.ShouldContain("folderFingerprint=0123456789ABCDEF");
        serialized.ShouldContain("nativeError=10060");
        serialized.ShouldContain("remoteAddressClass=public");
        serialized.ShouldNotContain("203.0.113.42");
        serialized.ShouldNotContain("secret-path");
        serialized.ShouldNotContain("share-key-secret");
        fields.Select(item => item.Key).ShouldNotContain("remoteAddress");
        fields.Select(item => item.Key).ShouldNotContain("remotePort");
        fields.Select(item => item.Key).ShouldNotContain("exception");
        var shared = AzureDiagnosticSink.CreateFields(entry with
        { RemoteAddress = IPAddress.Parse("100.80.170.34") }, DateTime.UtcNow,
            "b831eb836ce4421c8b481e4879da571a", "1A2B3C4D5E6F7890");
        shared.Single(item => item.Key == "remoteAddressClass").Value
            .ShouldBe("shared_address_space");
        var full = AzureDiagnosticSink.CreateFields(entry, DateTime.UtcNow,
            "b831eb836ce4421c8b481e4879da571a", "1A2B3C4D5E6F7890", includeNetworkAddresses: true);
        full.Single(item => item.Key == "remoteAddress").Value.ShouldBe("203.0.113.42");
        full.Single(item => item.Key == "remotePort").Value.ShouldBe(443);
        string.Join('|', full.Select(item => item.Value)).ShouldNotContain("share-key-secret");
    }
}
