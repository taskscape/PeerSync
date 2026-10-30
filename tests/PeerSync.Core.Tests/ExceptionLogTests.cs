using System.Net;
using System.Net.Sockets;
using PeerSync.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed class ExceptionLogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PeerSync-exceptions-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WarningExceptionsKeepStackSocketDataAndStructuredFieldsOutOfTheStructuredJournal()
    {
        using var exceptions = new ExceptionLog(directory);
        using var journal = new DiagnosticJournal(directory, "service", exceptions: exceptions);
        var error = ThrowWrapped();
        error.Data["attempt-id"] = "abc-123";
        error.Data["apiToken"] = "secret-token";
        journal.Write(new("transport.stream_failed", "tcp_session")
        {
            Level = DiagnosticLevel.Error, ErrorCode = "socket_ConnectionRefused", Exception = error,
            SessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Transport = DiagnosticTransport.Tcp, HResult = error.HResult,
            RemoteAddress = IPAddress.Parse("203.0.113.8"), RemotePort = 47821,
            NativeError = 10061, RetryInMilliseconds = 5000,
        });
        journal.Write(new("transport.debug_failed", "tcp_session")
        { Level = DiagnosticLevel.Debug, Exception = new InvalidOperationException("debug-marker-should-not-appear") });

        string structured = File.ReadAllText(Path.Combine(directory, "debug.jsonl"));
        structured.ShouldContain("transport.stream_failed");
        structured.ShouldContain("203.0.113.8");
        structured.ShouldNotContain("exception-marker-9f3c");
        structured.ShouldNotContain("secret-token");
        structured.ShouldNotContain("stackTrace");

        string text = File.ReadAllText(Directory.GetFiles(directory, "exceptions-*.log").Single());
        text.ShouldContain("transport.stream_failed");
        text.ShouldContain("tcp_session");
        text.ShouldContain("socket_ConnectionRefused");
        text.ShouldContain("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        text.ShouldContain("Tcp");
        text.ShouldContain("203.0.113.8");
        text.ShouldContain("47821");
        text.ShouldContain("retryInMs: 5000");
        text.ShouldContain("nativeError: 10061");
        text.ShouldContain("exception-marker-9f3c");
        text.ShouldContain("System.Net.Sockets.SocketException");
        text.ShouldContain("SocketErrorCode: ConnectionRefused");
        var socket = (SocketException)error.InnerException!;
        text.ShouldContain("NativeErrorCode: " + socket.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        text.ShouldContain("ErrorCode: " + socket.ErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        text.ShouldContain("stackTrace:");
        text.ShouldContain(nameof(ThrowConnectionRefused));
        text.ShouldContain("data.attempt-id: abc-123");
        text.ShouldNotContain("secret-token");
        text.ShouldNotContain("debug-marker-should-not-appear");
        text.ShouldContain("component: service");
    }

    [Fact]
    public void TextLogRedactsFolderKeysPrivateKeysAndSensitiveProperties()
    {
        using var exceptions = new ExceptionLog(directory, new FixedClock(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero)));
        exceptions.Append(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), "app", "run", 10, null,
            new("app.action_failed", "dashboard") { Level = DiagnosticLevel.Warning, ErrorCode = "LoginFailedException", Exception = ThrowSensitive() });
        string text = File.ReadAllText(Path.Combine(directory, "exceptions-2026-10-30.log"));
        text.ShouldContain("[redacted-folder-key]");
        text.ShouldContain("[redacted-private-key]");
        text.ShouldContain("Password: [redacted]");
        text.ShouldContain("Attempt: 4");
        text.ShouldNotContain("hunter2-secret");
        text.ShouldNotContain("MIISECRET");
        text.ShouldNotContain(new string('A', 55));
    }

    [Fact]
    public void FilesOlderThanThirtyDaysAreRemovedAndUnknownFilesStay()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "exceptions-2026-09-30.log"), "expired");
        File.WriteAllText(Path.Combine(directory, "exceptions-2026-09-30.2.log"), "expired-segment");
        File.WriteAllText(Path.Combine(directory, "exceptions-2026-10-01.log"), "kept");
        File.WriteAllText(Path.Combine(directory, "exceptions-bogus.log"), "unknown");
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero));
        using var exceptions = new ExceptionLog(directory, clock);
        exceptions.Append(new DateTimeOffset(2026, 10, 30, 1, 0, 0, TimeSpan.Zero), "service", "run", 4, "trace",
            new("service.peer_accept_failed", "peer_network") { Level = DiagnosticLevel.Error, ErrorCode = "SocketException", Exception = ThrowConnectionRefused() });
        File.Exists(Path.Combine(directory, "exceptions-2026-09-30.log")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "exceptions-2026-09-30.2.log")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(directory, "exceptions-2026-10-01.log")).ShouldBe("kept");
        File.ReadAllText(Path.Combine(directory, "exceptions-bogus.log")).ShouldBe("unknown");
        File.ReadAllText(Path.Combine(directory, "exceptions-2026-10-30.log")).ShouldContain(nameof(ThrowConnectionRefused));
    }

    [Fact]
    public void DailyLogRollsAndStopsAtTheSegmentLimit()
    {
        var stamp = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
        using var exceptions = new ExceptionLog(directory, new FixedClock(stamp), maxFileBytes: 1024);
        for (int i = 0; i < 30; i++)
            exceptions.Append(stamp, "service", "run", 1, null, new("transport.stream_failed", "tcp_session")
            { Level = DiagnosticLevel.Warning, ErrorCode = "SocketException", Exception = ThrowBulky() });
        Directory.GetFiles(directory, "exceptions-2026-10-30*.log").Length.ShouldBe(16);
        File.Exists(Path.Combine(directory, "exceptions-2026-10-30.16.log")).ShouldBeFalse();
        string last = File.ReadAllText(Path.Combine(directory, "exceptions-2026-10-30.15.log"));
        last.ShouldContain("size limit");
        File.ReadAllText(Path.Combine(directory, "exceptions-2026-10-30.log")).ShouldContain("SocketErrorCode: ConnectionRefused");
    }

    [Fact]
    public void StructuredWriteSurvivesAnUnavailableExceptionLog()
    {
        string blocked = Path.Combine(directory, "blocked.log");
        Directory.CreateDirectory(directory);
        File.WriteAllText(blocked, "not-a-directory");
        using var exceptions = new ExceptionLog(blocked);
        using var journal = new DiagnosticJournal(directory, "test", exceptions: exceptions);
        journal.Write(new("transfer.failed", "dial")
        { Level = DiagnosticLevel.Error, ErrorCode = "IOException", Exception = ThrowConnectionRefused() });
        File.ReadAllText(Path.Combine(directory, "debug.jsonl")).ShouldContain("transfer.failed");
        journal.DroppedEvents.ShouldBe(0);
    }

    private static Exception ThrowWrapped()
    {
        try { throw new InvalidOperationException("exception-marker-9f3c", ThrowConnectionRefused()); }
        catch (Exception error) { return error; }
    }

    private static SocketException ThrowConnectionRefused()
    {
        try { throw new SocketException((int)SocketError.ConnectionRefused); }
        catch (SocketException error) { return error; }
    }

    private static Exception ThrowBulky()
    {
        try { throw new InvalidOperationException(new string('x', 2000), ThrowConnectionRefused()); }
        catch (Exception error) { return error; }
    }

    private static Exception ThrowSensitive()
    {
        string key = new('A', 55);
        string pem = "-----BEGIN PRIVATE KEY-----\nMIISECRET\n-----END PRIVATE KEY-----";
        try { throw new LoginFailedException("see " + key + " " + pem); }
        catch (Exception error) { return error; }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class LoginFailedException(string message) : Exception(message)
    {
        public string Password { get; } = "hunter2-secret";
        public int Attempt { get; } = 4;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
