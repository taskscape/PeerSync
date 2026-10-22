using System.IO.Compression;
using PeerSync.Cli.Diagnostics;
using PeerSync.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace PeerSync.Cli.Tests;

public sealed class DiagnosticsCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PeerSync-cli-diagnostics-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreatesBundleAtTheRequestedPathFromTheSelectedLogRoot()
    {
        string logs = Path.Combine(directory, "logs");
        using (var journal = new DiagnosticJournal(logs, "service")) journal.Write(new("service.ready", "startup"));
        string target = Path.Combine(directory, "support.zip");
        DiagnosticsCommand.Run(["--logs", logs, "--output", target]).ShouldBe(0);
        File.Exists(target).ShouldBeTrue();
        using ZipArchive archive = ZipFile.OpenRead(target);
        archive.GetEntry("debug-000.jsonl").ShouldNotBeNull();
        archive.GetEntry("environment.json").ShouldNotBeNull();
    }

    [Fact]
    public void RejectsUnknownAndIncompleteOptionsWithoutCreatingAnArchive()
    {
        DiagnosticsCommand.Run(["--unknown"]).ShouldBe(2);
        DiagnosticsCommand.Run(["--logs"]).ShouldBe(2);
        Directory.Exists(directory).ShouldBeFalse();
    }

    [Fact]
    public void ReportsMissingRecentEventsWithoutLeavingAPartialArchive()
    {
        string logs = Path.Combine(directory, "logs"); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "debug.jsonl"), "not-json\n");
        string target = Path.Combine(directory, "failed.zip");
        DiagnosticsCommand.Run(["--logs", logs, "--output", target]).ShouldBe(1);
        File.Exists(target).ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
