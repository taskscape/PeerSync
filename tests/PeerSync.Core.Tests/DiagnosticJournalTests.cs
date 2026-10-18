using System.Text.Json;
using PeerSync.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed class DiagnosticJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PeerSync-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StructuredEventsKeepSessionCorrelationAndGateDebug()
    {
        Guid session = Guid.NewGuid();
        using (var log = new DiagnosticJournal(directory, "test"))
        {
            log.Write(new("connection.started", "dial") { SessionId = session, Transport = DiagnosticTransport.Quic });
            log.Write(new("connection.debug", "dial") { Level = DiagnosticLevel.Debug });
        }
        string[] lines = File.ReadAllLines(Path.Combine(directory, "debug.jsonl"));
        lines.Length.ShouldBe(1);
        using var json = JsonDocument.Parse(lines[0]);
        json.RootElement.GetProperty("sessionId").GetGuid().ShouldBe(session);
        json.RootElement.GetProperty("transport").GetString().ShouldBe("Quic");
        json.RootElement.TryGetProperty("timestampUtc", out _).ShouldBeTrue();
        json.RootElement.TryGetProperty("runId", out _).ShouldBeTrue();
    }

    [Fact]
    public void RotatesAndExpiresOldFiles()
    {
        Directory.CreateDirectory(directory);
        string expired = Path.Combine(directory, "debug.6.jsonl");
        File.WriteAllText(expired, "expired");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-2));
        using var log = new DiagnosticJournal(directory, "test", true, 4096, 3);
        for (int i = 0; i < 200; i++) log.Write(new("transfer.progress", "receive") { Bytes = i });
        File.Exists(expired).ShouldBeFalse();
        var files = Directory.GetFiles(directory);
        files.Length.ShouldBe(3);
        foreach (string file in files) new FileInfo(file).Length.ShouldBeLessThanOrEqualTo(4096);
    }

    [Fact]
    public void RejectsArbitraryErrorMessages()
    {
        using var log = new DiagnosticJournal(directory, "test");
        Should.Throw<ArgumentException>(() => log.Write(new("error", "dial") { ErrorCode = "secret with whitespace" }));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
