using System.IO.Compression;
using PeerSync.Core.Diagnostics;
using PeerSync.Rendezvous.Hosting;
using Shouldly;
using Xunit;

namespace PeerSync.Rendezvous.Tests;

public sealed class RendezvousDiagnosticsCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "PeerSync-rendezvous-diagnostics-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CustomLogDirectoryProducesASanitizedBundle()
    {
        string logs = Path.Combine(directory, "logs");
        using (var journal = new DiagnosticJournal(logs, "rendezvous"))
            journal.Write(new("rendezvous.started", "startup"));
        string target = Path.Combine(directory, "support.zip");
        RendezvousDiagnosticsCommand.Run(["--logs", logs, "--output", target]).ShouldBe(0);
        using ZipArchive archive = ZipFile.OpenRead(target);
        archive.GetEntry("debug-000.jsonl").ShouldNotBeNull();
        archive.GetEntry("environment.json").ShouldNotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
