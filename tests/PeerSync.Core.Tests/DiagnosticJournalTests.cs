using System.Net;
using System.Text.Json;
using PeerSync.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed class DiagnosticJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PeerSync-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LocalObserverReceivesOnlyPersistedMetadataAndCannotBreakTheJournal()
    {
        using var log = new DiagnosticJournal(directory, "test");
        var seen = new List<DiagnosticEvent>();
        log.EntryWritten += (entry, _) => seen.Add(entry);
        log.EntryWritten += (_, _) => throw new InvalidOperationException("observer failure");
        log.Write(new("transfer.failed", "download") { Level = DiagnosticLevel.Error, ErrorCode = "timeout" });
        seen.Count.ShouldBe(1);
        seen[0].ErrorCode.ShouldBe("timeout");
        File.ReadAllLines(Path.Combine(directory, "debug.jsonl")).Length.ShouldBe(1);
    }

    [Fact]
    public void BestEffortWritesCountUnavailableOrDisposedOutputWithoutChangingStrictWrite()
    {
        using var log = new DiagnosticJournal(directory, "test");
        Directory.CreateDirectory(Path.Combine(directory, "debug.jsonl"));
        log.TryWrite(new("operation.complete", "test")).ShouldBeFalse(); log.DroppedEvents.ShouldBe(1);
        Should.Throw<UnauthorizedAccessException>(() => log.Write(new("operation.complete", "test")));
        log.Dispose(); log.TryWrite(new("operation.failed", "test")).ShouldBeFalse(); log.DroppedEvents.ShouldBe(2);
    }

    [Fact]
    public void BestEffortFormattingFailureDoesNotExposeInvalidDataAndLaterWritesStillWork()
    {
        using var log = new DiagnosticJournal(directory, "test");
        log.TryWrite(new("private path/invalid", "test")).ShouldBeFalse(); log.DroppedEvents.ShouldBe(1);
        log.TryWrite(new("operation.complete", "test")).ShouldBeTrue();
        string text = File.ReadAllText(Path.Combine(directory, "debug.jsonl")); text.ShouldNotContain("private path");
        File.ReadAllLines(Path.Combine(directory, "debug.jsonl")).Length.ShouldBe(1);
    }

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
    public void ErrorNumbersKeepHResultNativeAndProtocolCodesDistinct()
    {
        using (var log = new DiagnosticJournal(directory, "test"))
            log.Write(new("transfer.failed", "send") { ErrorCode = "QuicException.TransportError",
                HResult = unchecked((int)0x80131620), NativeError = 10060,
                TransportErrorCode = 0x100000000, ApplicationErrorCode = 0x200000000 });
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "debug.jsonl")));
        json.RootElement.GetProperty("hResult").GetInt32().ShouldBe(unchecked((int)0x80131620));
        json.RootElement.GetProperty("nativeError").GetInt32().ShouldBe(10060);
        json.RootElement.GetProperty("transportErrorCode").GetInt64().ShouldBe(0x100000000);
        json.RootElement.GetProperty("applicationErrorCode").GetInt64().ShouldBe(0x200000000);
    }

    [Fact]
    public void NumericWorkCountersRoundTripWithoutAnArbitraryPayload()
    {
        using (var log = new DiagnosticJournal(directory, "test"))
            log.Write(new("scan.complete", "scan") { ItemsVisited = 100000, ItemsChanged = 5,
                ItemsHashed = 4, ItemsSkipped = 99994, ItemsDeferred = 1 });
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "debug.jsonl")));
        json.RootElement.GetProperty("itemsVisited").GetInt64().ShouldBe(100000);
        json.RootElement.GetProperty("itemsChanged").GetInt64().ShouldBe(5);
        json.RootElement.GetProperty("itemsHashed").GetInt64().ShouldBe(4);
        json.RootElement.GetProperty("itemsSkipped").GetInt64().ShouldBe(99994);
        json.RootElement.GetProperty("itemsDeferred").GetInt64().ShouldBe(1);
        json.RootElement.TryGetProperty("payload", out _).ShouldBeFalse();
    }

    [Fact]
    public void NumericMemorySamplesRoundTripWithoutAPathOrPayload()
    {
        using (var log = new DiagnosticJournal(directory, "scanner", debugEnabled: true))
            log.Write(new("scan.memory_hashed", "scan")
            {
                Level = DiagnosticLevel.Debug, WorkingSetBytes = 375_000_000,
                PrivateBytes = 340_000_000, ManagedHeapBytes = 299_000_000,
            });
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "debug.jsonl")));
        json.RootElement.GetProperty("workingSetBytes").GetInt64().ShouldBe(375_000_000);
        json.RootElement.GetProperty("privateBytes").GetInt64().ShouldBe(340_000_000);
        json.RootElement.GetProperty("managedHeapBytes").GetInt64().ShouldBe(299_000_000);
        json.RootElement.TryGetProperty("path", out _).ShouldBeFalse();
        json.RootElement.TryGetProperty("payload", out _).ShouldBeFalse();
    }

    [Fact]
    public void RotatesAndExpiresOldFiles()
    {
        Directory.CreateDirectory(directory);
        string expired = Path.Combine(directory, "debug.64.jsonl");
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
    public void IdleAddressLogExpiresWithoutAnotherWriteAndUnknownFilesSurvive()
    {
        Directory.CreateDirectory(directory);
        string foreign = Path.Combine(directory, "debug-owner.jsonl");
        File.WriteAllText(foreign, "other application data");
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-2));
        var clock = new ManualClock();
        using var log = new DiagnosticJournal(directory, "test", timeProvider: clock);
        string active = Path.Combine(directory, "debug.jsonl");
        log.Write(new("probe.reached", "probe") { RemoteAddress = IPAddress.Parse("192.0.2.31") });
        File.Exists(active).ShouldBeTrue();

        clock.Advance(TimeSpan.FromHours(23)); clock.Fire();
        File.Exists(active).ShouldBeTrue();
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1)); clock.Fire();
        File.Exists(active).ShouldBeFalse();
        File.ReadAllText(foreign).ShouldBe("other application data");

        log.Write(new("probe.recovered", "probe"));
        string current = File.ReadAllText(active);
        current.ShouldContain("probe.recovered");
        current.ShouldNotContain("192.0.2.31");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan duration) => now += duration;
        internal void Fire() { foreach (var timer in timers.ToArray()) timer.Fire(); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(callback, state); timers.Add(timer); return timer; }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
        internal void Fire() { if (!disposed) callback(state); }
        public void Dispose() => disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
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
