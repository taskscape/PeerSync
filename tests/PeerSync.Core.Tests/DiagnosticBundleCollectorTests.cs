using System.IO.Compression;
using System.Net;
using System.Text.Json;
using PeerSync.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed class DiagnosticBundleCollectorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PeerSync-bundle-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CollectsRecentStructuredEventsAndOmitsPrivateOrUnknownFieldsByDefault()
    {
        string logs = Path.Combine(directory, "logs"); Directory.CreateDirectory(logs);
        using (var journal = new DiagnosticJournal(logs, "net", debugEnabled: true))
            journal.Write(new("transport.request_complete", "quic_block")
            {
                SessionId = Guid.Parse("479407df-d4d0-4cae-9e9b-454add07b31a"),
                Transport = DiagnosticTransport.Quic, Bytes = 4096, ElapsedMilliseconds = 125.5,
                RemoteAddress = IPAddress.Parse("203.0.113.8"), RemotePort = 47821,
            });
        File.AppendAllText(Path.Combine(logs, "debug.jsonl"),
            "{\"timestampUtc\":\"" + DateTimeOffset.UtcNow.ToString("O")
            + "\",\"component\":\"net\",\"level\":\"Debug\",\"eventName\":\"transport.safe\",\"operation\":\"send\",\"FolderId\":\"folder-secret\",\"path\":\"private.txt\",\"recordBody\":\"private-body\"}\nnot-json\n");

        string destination = Path.Combine(directory, "bundle.zip");
        DiagnosticBundleResult result = DiagnosticBundleCollector.Collect(logs, destination);
        result.FilesIncluded.ShouldBe(1); result.EventsIncluded.ShouldBe(2); result.InvalidEvents.ShouldBe(1);
        using ZipArchive archive = ZipFile.OpenRead(destination);
        archive.Entries.Select(entry => entry.FullName).ShouldBe(["debug-000.jsonl", "environment.json"]);
        using var reader = new StreamReader(archive.GetEntry("debug-000.jsonl")!.Open());
        string bundleLog = reader.ReadToEnd();
        bundleLog.ShouldContain("transport.request_complete"); bundleLog.ShouldContain("479407df-d4d0-4cae-9e9b-454add07b31a");
        bundleLog.ShouldNotContain("203.0.113.8"); bundleLog.ShouldNotContain("47821"); bundleLog.ShouldNotContain("folder-secret");
        bundleLog.ShouldNotContain("private.txt"); bundleLog.ShouldNotContain("private-body");
        using var environment = JsonDocument.Parse(new StreamReader(archive.GetEntry("environment.json")!.Open()).ReadToEnd());
        environment.RootElement.GetProperty("networkAddressesIncluded").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void IncludesValidatedAddressesOnlyWhenRequestedAndRejectsEmptyRecentData()
    {
        string logs = Path.Combine(directory, "logs"); Directory.CreateDirectory(logs);
        using (var journal = new DiagnosticJournal(logs, "service"))
            journal.Write(new("service.ready", "startup") { RemoteAddress = IPAddress.Parse("192.0.2.40"), RemotePort = 47822 });
        string included = Path.Combine(directory, "addresses.zip");
        DiagnosticBundleCollector.Collect(logs, included, includeNetworkAddresses: true);
        using (ZipArchive archive = ZipFile.OpenRead(included))
        using (var reader = new StreamReader(archive.GetEntry("debug-000.jsonl")!.Open()))
        {
            string contents = reader.ReadToEnd();
            contents.ShouldContain("192.0.2.40");
            contents.ShouldContain("47822");
        }

        string staleLogs = Path.Combine(directory, "stale"); Directory.CreateDirectory(staleLogs);
        File.WriteAllText(Path.Combine(staleLogs, "debug.jsonl"),
            "{\"timestampUtc\":\"" + DateTimeOffset.UtcNow.AddDays(-2).ToString("O")
            + "\",\"component\":\"service\",\"level\":\"Information\",\"eventName\":\"service.ready\",\"operation\":\"startup\"}\n");
        string missing = Path.Combine(directory, "empty.zip");
        Should.Throw<InvalidDataException>(() => DiagnosticBundleCollector.Collect(staleLogs, missing));
        File.Exists(missing).ShouldBeFalse();
    }

    [Fact]
    public void BundleDestinationMustBeOutsideTheSourceAndMustNotOverwrite()
    {
        string logs = Path.Combine(directory, "logs"); Directory.CreateDirectory(logs);
        using (var journal = new DiagnosticJournal(logs, "service")) journal.Write(new("service.ready", "startup"));
        Should.Throw<ArgumentException>(() => DiagnosticBundleCollector.Collect(logs, Path.Combine(logs, "inside.zip")));
        string existing = Path.Combine(directory, "existing.zip"); File.WriteAllText(existing, "keep");
        Should.Throw<IOException>(() => DiagnosticBundleCollector.Collect(logs, existing));
        File.ReadAllText(existing).ShouldBe("keep");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
