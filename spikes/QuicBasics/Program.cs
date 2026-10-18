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
    Console.Error.WriteLine("listen <port> <bytes> | dial <IP> <port> <bytes>");
    return 2;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
using var certificate = new SpikeCertificate();
string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
using var log = new DiagnosticJournal(logDirectory, "QuicBasics", true);
Guid session = Guid.NewGuid();
bool server = args[0] == "listen";
int port = int.Parse(args[server ? 1 : 2], CultureInfo.InvariantCulture);
long expectedBytes = long.Parse(args[server ? 2 : 3], CultureInfo.InvariantCulture);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedBytes);
log.Write(new("transport.start", server ? "listen" : "dial") { SessionId = session, Transport = DiagnosticTransport.Quic });
try
{
    await using var listener = server ? await QuicProbe.ListenAsync(port, certificate.Certificate, timeout.Token) : null;
    if (listener is not null) { Console.WriteLine($"READY {listener.LocalEndPoint.Port}"); Console.Out.Flush(); }
    await using var connection = server
        ? await listener!.AcceptConnectionAsync(timeout.Token)
        : await QuicProbe.DialAsync(new(IPAddress.Parse(args[1]), port), certificate.Certificate, timeout.Token);
    byte[] remoteId = DeviceIdentity.FromCertificate(connection.RemoteCertificate!);
    if (connection.NegotiatedApplicationProtocol != PeerTls.Alpn) throw new InvalidDataException("Wrong ALPN.");
    log.Write(new("transport.authenticated", "tls") { SessionId = session, Transport = DiagnosticTransport.Quic });
    await using var stream = server ? await connection.AcceptInboundStreamAsync(timeout.Token)
        : await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token);
    if (server)
    {
        byte[] sessionBytes = new byte[16];
        await stream.ReadExactlyAsync(sessionBytes, timeout.Token);
        session = new Guid(sessionBytes);
    }
    else { await stream.WriteAsync(session.ToByteArray(), timeout.Token); }
    log.Write(new("session.attached", "transfer") { Level = DiagnosticLevel.Debug, SessionId = session, Transport = DiagnosticTransport.Quic });
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] buffer = new byte[1024 * 1024];
    long transferred = 0;
    var clock = Stopwatch.StartNew();
    if (server)
    {
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            transferred += count;
            if (transferred > expectedBytes) throw new InvalidDataException("Excess data.");
            hash.AppendData(buffer, 0, count);
        }
        if (transferred != expectedBytes) throw new InvalidDataException("Short transfer.");
        await stream.WriteAsync(hash.GetHashAndReset(), timeout.Token);
        stream.CompleteWrites();
        // Wait for the client to finish reading our digest before disposing the connection.
        await stream.WritesClosed.WaitAsync(timeout.Token);
    }
    else
    {
        RandomNumberGenerator.Fill(buffer);
        while (transferred < expectedBytes)
        {
            int count = (int)Math.Min(buffer.Length, expectedBytes - transferred);
            await stream.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            hash.AppendData(buffer, 0, count);
            transferred += count;
        }
        stream.CompleteWrites();
        byte[] digest = new byte[32];
        await stream.ReadExactlyAsync(digest, timeout.Token);
        if (!CryptographicOperations.FixedTimeEquals(digest, hash.GetHashAndReset())) throw new InvalidDataException("Hash mismatch.");
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
catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException or QuicException)
{
    log.Write(new("transfer.failed", "transfer") { Level = DiagnosticLevel.Error, SessionId = session,
        ErrorCode = ex.GetType().Name, NativeError = ex is SocketException socket ? socket.NativeErrorCode : ex.HResult, Transport = DiagnosticTransport.Quic });
    Console.Error.WriteLine($"FAILED {ex.GetType().Name} 0x{ex.HResult:X8}");
    return 1;
}
