namespace PeerSync.Core.Diagnostics;

public static class ProbeCorrelation
{
    public const string HeaderName = "X-PeerSync-Probe-Id";

    // Match the diagnostic journal's Guid JSON form for direct bundle searches.
    public static string Format(Guid session) => session.ToString("D");

    public static bool TryParse(string? value, out Guid session)
    {
        if (value is { Length: 36 }) return Guid.TryParseExact(value, "D", out session);
        session = default;
        return false;
    }
}
