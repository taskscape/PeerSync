namespace PeerSync.Core.Diagnostics;

public static class ProbeCorrelation
{
    public const string HeaderName = "X-PeerSync-Probe-Id";

    public static string Format(Guid session) => session.ToString("N");

    public static bool TryParse(string? value, out Guid session)
    {
        if (value is { Length: 32 }) return Guid.TryParseExact(value, "N", out session);
        session = default;
        return false;
    }
}
