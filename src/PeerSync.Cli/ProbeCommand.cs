using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Cli;

internal static class ProbeCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var target] || !Uri.TryCreate("https://" + target, UriKind.Absolute, out Uri? origin)
            || origin.Port is < 1 or > 65535 || origin.UserInfo.Length != 0
            || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
        {
            Console.Error.WriteLine("Usage: psp probe <host:port>");
            return 2;
        }

        Guid session = Guid.NewGuid();
        ProbeResult tcp = await CheckAsync(origin, HttpVersion.Version20, session).ConfigureAwait(false);
        ProbeResult udp = await CheckAsync(origin, HttpVersion.Version30, session).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            server = origin.Authority,
            sessionId = ProbeCorrelation.Format(session),
            tcp = Output(tcp), udp = Output(udp),
            observedAddress = udp.ObservedAddress ?? tcp.ObservedAddress,
            observedPort = udp.ObservedPort ?? tcp.ObservedPort,
        }));
        return tcp.Reachable && udp.Reachable ? 0 : 1;
    }

    private static object Output(ProbeResult result) => new
    {
        reachable = result.Reachable, httpVersion = result.HttpVersion,
        correlated = result.Correlated, error = result.Error, category = result.Category,
        cause = result.Cause, httpRequestError = result.HttpRequestError,
        socketError = result.SocketError, nativeError = result.NativeError,
        quicError = result.QuicError, hResult = result.HResult,
    };

    private static async Task<ProbeResult> CheckAsync(Uri origin, Version requestedVersion, Guid session)
    {
        try
        {
            using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) };
            handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls13;
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "/probe"))
            { Version = requestedVersion, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            request.Headers.Add(ProbeCorrelation.HeaderName, ProbeCorrelation.Format(session));
            using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new() { HttpVersion = response.Version.ToString(), Error = "http_" + (int)response.StatusCode,
                    Category = "http-status" };
            if (response.Version != requestedVersion)
                return new() { HttpVersion = response.Version.ToString(), Error = "version-mismatch",
                    Category = "protocol" };
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("observedAddress", out JsonElement address)
                || address.ValueKind != JsonValueKind.String
                || !IPAddress.TryParse(address.GetString(), out _)
                || !root.TryGetProperty("observedPort", out JsonElement port)
                || port.ValueKind != JsonValueKind.Number
                || !port.TryGetInt32(out int observedPort) || observedPort is < 1 or > 65535)
                return new() { HttpVersion = response.Version.ToString(), Error = "invalid-observation",
                    Category = "response" };
            string? echoed = root.TryGetProperty("probeSessionId", out JsonElement probeId)
                && probeId.ValueKind == JsonValueKind.String ? probeId.GetString() : null;
            if (echoed is not null && !string.Equals(echoed, ProbeCorrelation.Format(session), StringComparison.OrdinalIgnoreCase))
                return new() { HttpVersion = response.Version.ToString(), Error = "session-mismatch",
                    Category = "response" };
            return new()
            {
                Reachable = true, HttpVersion = response.Version.ToString(), Correlated = echoed is not null,
                ObservedAddress = address.GetString(), ObservedPort = observedPort,
            };
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException
            or JsonException or AuthenticationException or IOException or QuicException)
        {
            return Failure(error);
        }
    }

    private static ProbeResult Failure(Exception error)
    {
        SocketException? socket = null;
        QuicException? quic = null;
        AuthenticationException? tls = null;
        HttpRequestException? http = null;
        Exception cause = error;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            cause = current;
            socket ??= current as SocketException;
            quic ??= current as QuicException;
            tls ??= current as AuthenticationException;
            http ??= current as HttpRequestException;
        }
        string category = socket?.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData => "dns",
            SocketError.ConnectionRefused => "refused",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable => "unreachable",
            SocketError.TimedOut => "timeout",
            not null => "socket",
            _ => tls is not null ? "tls" : quic is not null ? "quic"
                : error is OperationCanceledException ? "timeout"
                : error is JsonException ? "response" : "request",
        };
        return new()
        {
            Error = error.GetType().Name, Category = category, Cause = cause.GetType().Name,
            HttpRequestError = http?.HttpRequestError.ToString(), SocketError = socket?.SocketErrorCode.ToString(),
            NativeError = socket?.NativeErrorCode, QuicError = quic?.QuicError.ToString(), HResult = error.HResult,
        };
    }

    private sealed record ProbeResult
    {
        public bool Reachable { get; init; }
        public string? HttpVersion { get; init; }
        public bool? Correlated { get; init; }
        public string? Error { get; init; }
        public string? Category { get; init; }
        public string? Cause { get; init; }
        public string? HttpRequestError { get; init; }
        public string? SocketError { get; init; }
        public int? NativeError { get; init; }
        public string? QuicError { get; init; }
        public int? HResult { get; init; }
        public string? ObservedAddress { get; init; }
        public int? ObservedPort { get; init; }
    }
}
