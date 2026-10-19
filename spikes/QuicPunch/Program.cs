using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PeerSync.Spikes;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
if (args is ["--tcp"])
{
    using var tcpListenerA = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    using var tcpListenerB = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    using var clientA = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    using var clientB = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    foreach (var socket in new[] { tcpListenerA, tcpListenerB, clientA, clientB })
    {
        socket.ExclusiveAddressUse = false;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    }
    try
    {
        tcpListenerA.Bind(new IPEndPoint(IPAddress.Loopback, 0)); tcpListenerA.Listen(4);
        tcpListenerB.Bind(new IPEndPoint(IPAddress.Loopback, 0)); tcpListenerB.Listen(4);
        clientA.Bind(tcpListenerA.LocalEndPoint!); clientB.Bind(tcpListenerB.LocalEndPoint!);
        await Task.WhenAll(clientA.ConnectAsync(tcpListenerB.LocalEndPoint!, timeout.Token).AsTask(),
            clientB.ConnectAsync(tcpListenerA.LocalEndPoint!, timeout.Token).AsTask());
        await clientA.SendAsync(new byte[] { 80 }, SocketFlags.None, timeout.Token);
        byte[] received = new byte[1];
        int count = await clientB.ReceiveAsync(received, SocketFlags.None, timeout.Token);
        bool verified = count == 1 && received[0] == 80;
        Console.WriteLine(JsonSerializer.Serialize(new { option = "TCP.SO_REUSEADDR.simultaneous-open", topology = "loopback",
            success = verified, natMatrixVerified = false }));
        return verified ? 0 : 3;
    }
    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { option = "TCP.SO_REUSEADDR.simultaneous-open", topology = "loopback",
            success = false, error = ex.GetType().Name, nativeError = ex is SocketException socket ? socket.NativeErrorCode : ex.HResult, natMatrixVerified = false }));
        return 3;
    }
}
using var certificateA = new SpikeCertificate();
using var certificateB = new SpikeCertificate();
if (args is ["--native", var executable])
{
    using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
    store.Open(OpenFlags.ReadWrite);
    store.Add(certificateA.Certificate);
    try
    {
        var info = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "msquic.dll"));
        info.ArgumentList.Add(certificateA.Certificate.Thumbprint);
        using var process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Console.Write(await output);
        Console.Error.Write(await error);
        return process.ExitCode;
    }
    finally { store.Remove(certificateA.Certificate); }
}
await using var listenerA = await QuicProbe.ListenAsync(0, certificateA.Certificate, timeout.Token);
await using var listenerB = await QuicProbe.ListenAsync(0, certificateB.Certificate, timeout.Token);
var accept = listenerB.AcceptConnectionAsync(timeout.Token).AsTask();
try
{
    await using var outgoing = await QuicProbe.DialAsync(new(IPAddress.Loopback, listenerB.LocalEndPoint.Port),
        certificateA.Certificate, timeout.Token, listenerA.LocalEndPoint);
    await using var incoming = await accept;
    bool samePort = outgoing.LocalEndPoint.Port == listenerA.LocalEndPoint.Port;
    if (!samePort) throw new InvalidDataException("MsQuic selected a different source port.");
    await using var sent = await outgoing.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token);
    await sent.WriteAsync(new byte[] { 80, 83, 80, 49 }, timeout.Token);
    sent.CompleteWrites();
    await using var received = await incoming.AcceptInboundStreamAsync(timeout.Token);
    byte[] magic = new byte[4];
    await received.ReadExactlyAsync(magic, timeout.Token);
    if (!magic.AsSpan().SequenceEqual("PSP1"u8)) throw new InvalidDataException("Wrong bytes.");
    await received.WriteAsync(new byte[] { 1 }, timeout.Token);
    received.CompleteWrites();
    byte[] ack = new byte[1];
    await sent.ReadExactlyAsync(ack, timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { option = "System.Net.Quic.LocalEndPoint", topology = "loopback",
        listenerPort = listenerA.LocalEndPoint.Port, sourcePort = outgoing.LocalEndPoint.Port,
        certificateIdentityVerified = DeviceIdentity.FromCertificate(incoming.RemoteCertificate!).AsSpan()
            .SequenceEqual(DeviceIdentity.FromCertificate(certificateA.Certificate)), success = true,
        natMatrixVerified = false }));
    return 0;
}
catch (Exception ex) when (ex is QuicException or SocketException or OperationCanceledException)
{
    timeout.Cancel();
    try { await accept; } catch (OperationCanceledException) { }
    Console.WriteLine(JsonSerializer.Serialize(new { option = "System.Net.Quic.LocalEndPoint", topology = "loopback", success = false,
        error = ex.GetType().Name, hResult = ex.HResult,
        nativeError = (ex.GetBaseException() as SocketException)?.NativeErrorCode,
        quicError = (ex as QuicException)?.QuicError.ToString(),
        transportErrorCode = (ex as QuicException)?.TransportErrorCode, natMatrixVerified = false }));
    return 3; // Observed unsupported binding; not a passing hole-punch test.
}
