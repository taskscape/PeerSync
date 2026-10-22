using System.Net;
using System.Security.Authentication;
using System.Text.Json;

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

        ProbeResult tcp = await CheckAsync(origin, HttpVersion.Version20).ConfigureAwait(false);
        ProbeResult udp = await CheckAsync(origin, HttpVersion.Version30).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            server = origin.Authority,
            tcp = new { reachable = tcp.Reachable, httpVersion = tcp.HttpVersion, error = tcp.Error },
            udp = new { reachable = udp.Reachable, httpVersion = udp.HttpVersion, error = udp.Error },
            observedAddress = udp.ObservedAddress ?? tcp.ObservedAddress,
            observedPort = udp.ObservedPort ?? tcp.ObservedPort,
        }));
        return tcp.Reachable && udp.Reachable ? 0 : 1;
    }

    private static async Task<ProbeResult> CheckAsync(Uri origin, Version requestedVersion)
    {
        try
        {
            using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) };
            handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls13;
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "/probe"))
            { Version = requestedVersion, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Version != requestedVersion)
                return new(false, response.Version.ToString(), "http_" + (int)response.StatusCode, null, null);
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            JsonElement root = document.RootElement;
            string? observed = root.TryGetProperty("observedAddress", out JsonElement address)
                ? address.GetString() : null;
            int? port = root.TryGetProperty("observedPort", out JsonElement observedPort)
                && observedPort.TryGetInt32(out int value) ? value : null;
            return new(true, response.Version.ToString(), null, observed, port);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException
            or JsonException or AuthenticationException or IOException)
        {
            return new(false, null, error.GetType().Name, null, null);
        }
    }

    private sealed record ProbeResult(bool Reachable, string? HttpVersion, string? Error,
        string? ObservedAddress, int? ObservedPort);
}
