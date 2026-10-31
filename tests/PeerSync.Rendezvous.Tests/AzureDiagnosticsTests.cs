using System.Net;
using PeerSync.Core.Diagnostics;
using PeerSync.Rendezvous.Hosting;
using PeerSync.Telemetry;
using Shouldly;
using Xunit;

namespace PeerSync.Rendezvous.Tests;

public sealed class AzureDiagnosticsTests
{
    [Fact]
    public void DefaultsAndExportedFieldsSupportCrossMachineCorrelationWithoutAddresses()
    {
        var options = new RendezvousOptions();
        options.SendDiagnosticsToAzure.ShouldBeTrue();
        options.FullDiagnostics.ShouldBeFalse();
        (options with { SendDiagnosticsToAzure = false }).SendDiagnosticsToAzure.ShouldBeFalse();

        var entry = new DiagnosticEvent("rendezvous.folder_announced", "tracker")
        {
            FolderFingerprint = "0123456789ABCDEF",
            PeerFingerprint = "FEDCBA9876543210",
            RemoteAddress = IPAddress.Parse("203.0.113.42"),
            RemotePort = 443,
        };
        var fields = AzureDiagnosticSink.CreateFields(entry, DateTime.UtcNow,
            "0123456789abcdef0123456789abcdef", "server");
        fields.Single(field => field.Key == "folderFingerprint").Value.ShouldBe(entry.FolderFingerprint);
        fields.Single(field => field.Key == "peerFingerprint").Value.ShouldBe(entry.PeerFingerprint);
        fields.Single(field => field.Key == "remoteAddressClass").Value.ShouldBe("public");
        fields.Select(field => field.Key).ShouldNotContain("remoteAddress");
        fields.Select(field => field.Key).ShouldNotContain("remotePort");

        var full = AzureDiagnosticSink.CreateFields(entry, DateTime.UtcNow,
            "0123456789abcdef0123456789abcdef", "server", includeNetworkAddresses: true);
        full.Single(field => field.Key == "remoteAddress").Value.ShouldBe("203.0.113.42");
        full.Single(field => field.Key == "remotePort").Value.ShouldBe(443);
    }
}
