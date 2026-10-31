namespace PeerSync.Rendezvous.Hosting;

public sealed record RendezvousOptions
{
    public int Port { get; init; } = 99;
    public string CertificateSubject { get; init; } = "sync.taskscape.com";
    public string PublicUrl { get; init; } = "https://sync.taskscape.com:99";
    public bool DebugLogging { get; init; } = true;
    public bool SendDiagnosticsToAzure { get; init; } = true;
    public bool FullDiagnostics { get; init; }
    public string? LogDirectory { get; init; }
    public int RelayPerDeviceBytesPerSecond { get; init; } = 2_000_000;
    public int RelayTotalBytesPerSecond { get; init; } = 8_000_000;
    public int MaximumRelaySessionsPerDevice { get; init; } = 4;
    public string? OtlpEndpoint { get; init; }

    public void Validate()
    {
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (string.IsNullOrWhiteSpace(CertificateSubject) || CertificateSubject.Contains('*'))
            throw new ArgumentException("A specific server certificate DNS name is required.", nameof(CertificateSubject));
        if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Port is < 1 or > 65535
            || endpoint.AbsolutePath != "/" || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("PublicUrl must be an HTTPS origin.", nameof(PublicUrl));
        if (RelayPerDeviceBytesPerSecond is < 1 or > 100_000_000)
            throw new ArgumentOutOfRangeException(nameof(RelayPerDeviceBytesPerSecond));
        if (RelayTotalBytesPerSecond < RelayPerDeviceBytesPerSecond
            || RelayTotalBytesPerSecond > 1_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(RelayTotalBytesPerSecond));
        if (MaximumRelaySessionsPerDevice is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(MaximumRelaySessionsPerDevice));
        if (LogDirectory is not null && !Path.IsPathFullyQualified(LogDirectory))
            throw new ArgumentException("LogDirectory must be an absolute path.", nameof(LogDirectory));
        if (OtlpEndpoint is not null && (!Uri.TryCreate(OtlpEndpoint, UriKind.Absolute, out Uri? otlp)
            || otlp.Scheme != Uri.UriSchemeHttp && otlp.Scheme != Uri.UriSchemeHttps
            || otlp.Scheme == Uri.UriSchemeHttp && !otlp.IsLoopback))
            throw new ArgumentException("OtlpEndpoint must use HTTPS or loopback HTTP.", nameof(OtlpEndpoint));
    }
}
