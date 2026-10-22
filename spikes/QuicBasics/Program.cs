using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using PeerSync.Core.Diagnostics;
using PeerSync.Spikes;

if (args.Length < 3 || args[0] is not ("listen" or "dial"))
{
    PrintUsage();
    return 2;
}
bool server = args[0] == "listen";
int requiredArgs = server ? 3 : 4;
int transferTimeoutSeconds = 900;
IPAddress? remoteAddress = null;
if ((args.Length != requiredArgs && args.Length != requiredArgs + 2)
    || !int.TryParse(args[server ? 1 : 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
    || port < (server ? 0 : 1) || port > 65535
    || !long.TryParse(args[server ? 2 : 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long expectedBytes)
    || expectedBytes <= 0
    || (!server && !IPAddress.TryParse(args[1], out remoteAddress))
    || (args.Length == requiredArgs + 2 && (args[requiredArgs] != "--timeout-seconds"
        || !int.TryParse(args[requiredArgs + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out transferTimeoutSeconds)
        || transferTimeoutSeconds is < 1 or > 86400)))
{
    PrintUsage();
    return 2;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
using var log = new DiagnosticJournal(logDirectory, "QuicBasics", true);
Console.Error.WriteLine($"LOGS {logDirectory}");
Guid session = Guid.NewGuid();
string stage = "certificate";
long transferred = 0;
var runClock = Stopwatch.StartNew();
var clock = new Stopwatch();
// Stream/session setup already consumes the authenticated transfer deadline.
// Always expose the first completed data unit, then rate-limit later samples.
double nextProgressSeconds = 0;
log.Write(new("transport.start", server ? "listen" : "dial")
    { SessionId = session, Transport = DiagnosticTransport.Quic, RemoteAddress = remoteAddress });
try
{
    using var certificate = new SpikeCertificate();
    stage = server ? "listen" : "connect";
    await using var listener = server ? await QuicProbe.ListenAsync(port, certificate.Certificate, timeout.Token) : null;
    if (listener is not null)
    {
        Console.WriteLine($"READY {listener.LocalEndPoint.Port}");
        Console.Out.Flush();
        log.Write(new("transport.ready", "accept") { SessionId = session, Transport = DiagnosticTransport.Quic });
        stage = "accept";
    }
    await using var connection = server
        ? await listener!.AcceptConnectionAsync(timeout.Token)
        : await QuicProbe.DialAsync(new(remoteAddress!, port), certificate.Certificate, timeout.Token);
    // Give the transfer its full budget, regardless of how long the receiver waited for the sender.
    timeout.CancelAfter(TimeSpan.FromSeconds(transferTimeoutSeconds));
    stage = "tls";
    byte[] remoteId = DeviceIdentity.FromCertificate(connection.RemoteCertificate!);
    if (connection.NegotiatedApplicationProtocol != PeerTls.Alpn) throw new InvalidDataException("Wrong ALPN.");
    log.Write(new("transport.authenticated", "tls") { SessionId = session, Transport = DiagnosticTransport.Quic,
        RemoteAddress = connection.RemoteEndPoint.Address, ElapsedMilliseconds = runClock.Elapsed.TotalMilliseconds });
    Console.Error.WriteLine($"AUTHENTICATED transferTimeoutSeconds={transferTimeoutSeconds}");
    stage = "stream.open";
    await using var stream = server ? await connection.AcceptInboundStreamAsync(timeout.Token)
        : await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token);
    if (server)
    {
        stage = "session.read";
        byte[] sessionBytes = new byte[16];
        await stream.ReadExactlyAsync(sessionBytes, timeout.Token);
        session = new Guid(sessionBytes);
    }
    else { stage = "session.write"; await stream.WriteAsync(session.ToByteArray(), timeout.Token); }
    log.Write(new("session.attached", "transfer") { Level = DiagnosticLevel.Debug, SessionId = session, Transport = DiagnosticTransport.Quic });
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] buffer = new byte[1024 * 1024];
    clock.Start();
    if (server)
    {
        stage = "receive";
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            transferred += count;
            if (transferred > expectedBytes) throw new InvalidDataException("Excess data.");
            hash.AppendData(buffer, 0, count);
            ReportProgress();
        }
        if (transferred != expectedBytes) throw new InvalidDataException("Short transfer.");
        stage = "digest.write";
        await stream.WriteAsync(hash.GetHashAndReset(), timeout.Token);
        stream.CompleteWrites();
        // Wait for the client to finish reading our digest before disposing the connection.
        stage = "digest.ack";
        await stream.WritesClosed.WaitAsync(timeout.Token);
    }
    else
    {
        stage = "send";
        RandomNumberGenerator.Fill(buffer);
        while (transferred < expectedBytes)
        {
            int count = (int)Math.Min(buffer.Length, expectedBytes - transferred);
            await stream.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            hash.AppendData(buffer, 0, count);
            transferred += count;
            ReportProgress();
        }
        stream.CompleteWrites();
        stage = "digest.read";
        byte[] digest = new byte[32];
        await stream.ReadExactlyAsync(digest, timeout.Token);
        if (!CryptographicOperations.FixedTimeEquals(digest, hash.GetHashAndReset())) throw new InvalidDataException("Hash mismatch.");
        // Consume the peer's FIN before disposal. Disposing with an unread
        // read side aborts it, even after the complete digest was received.
        stage = "digest.eof";
        if (await stream.ReadAsync(buffer.AsMemory(0, 1), timeout.Token) != 0)
            throw new InvalidDataException("Trailing data after digest.");
    }
    clock.Stop();
    log.Write(new("transfer.completed", "transfer") { SessionId = session, Bytes = transferred,
        ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds, Transport = DiagnosticTransport.Quic });
    Console.WriteLine(JsonSerializer.Serialize(new { role = args[0], sessionId = session, logDirectory, bytes = transferred, elapsedSeconds = clock.Elapsed.TotalSeconds,
        mebibytesPerSecond = transferred / 1048576.0 / clock.Elapsed.TotalSeconds, alpn = "psp/1",
        localDeviceId = Convert.ToHexString(DeviceIdentity.FromCertificate(certificate.Certificate)), remoteDeviceId = Convert.ToHexString(remoteId),
        localEndpoint = connection.LocalEndPoint.ToString(), remoteEndpoint = connection.RemoteEndPoint.ToString(), verified = true }));
    return 0;
}
catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException
    or OperationCanceledException or QuicException or CryptographicException or PlatformNotSupportedException)
{
    var quic = ex as QuicException;
    var socket = ex as SocketException ?? ex.InnerException as SocketException;
    string errorCode = quic is not null ? $"QuicException.{quic.QuicError}"
        : ex is OperationCanceledException && timeout.IsCancellationRequested ? "ProbeTimeout" : ex.GetType().Name;
    log.Write(new("transfer.failed", stage) { Level = DiagnosticLevel.Error, SessionId = session,
        ErrorCode = errorCode, HResult = ex.HResult, NativeError = socket?.NativeErrorCode,
        TransportErrorCode = quic?.TransportErrorCode, ApplicationErrorCode = quic?.ApplicationErrorCode,
        Bytes = transferred, ElapsedMilliseconds = runClock.Elapsed.TotalMilliseconds, Transport = DiagnosticTransport.Quic });
    Console.Error.WriteLine($"FAILED {errorCode} hresult=0x{ex.HResult:X8} stage={stage} bytes={transferred} elapsedSeconds={runClock.Elapsed.TotalSeconds:F1} transportError={quic?.TransportErrorCode} applicationError={quic?.ApplicationErrorCode} nativeError={socket?.NativeErrorCode}");
    Console.Error.WriteLine($"LOGS {logDirectory}");
    return 1;
}

void ReportProgress()
{
    if (clock.Elapsed.TotalSeconds < nextProgressSeconds) return;
    nextProgressSeconds = clock.Elapsed.TotalSeconds + 5;
    log.Write(new("transfer.progress", stage) { Level = DiagnosticLevel.Debug, SessionId = session,
        Bytes = transferred, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds, Transport = DiagnosticTransport.Quic });
    Console.Error.WriteLine($"PROGRESS stage={stage} bytes={transferred}/{expectedBytes} elapsedSeconds={clock.Elapsed.TotalSeconds:F1}");
}

static void PrintUsage() => Console.Error.WriteLine(
    "listen <port> <bytes> [--timeout-seconds <1..86400>] | dial <IP> <port> <bytes> [--timeout-seconds <1..86400>]");
