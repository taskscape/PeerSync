using PeerSync.Core.Diagnostics;
using PeerSync.Protocol;
using PeerSync.Protocol.Framing;
using PeerSync.Protocol.Validation;

namespace PeerSync.Net.Security;

internal static class ProtocolViolationStrike
{
    internal static bool TryGetFailure(Exception error, out MessageFailure failure)
    {
        switch (error)
        {
            case MessageValidationException invalid:
                failure = invalid.Failure;
                return true;
            case ProtocolException:
                failure = MessageFailure.InvalidFraming;
                return true;
            default:
                failure = default;
                return false;
        }
    }

    internal static void Record(Action<byte[], MessageFailure>? addStrike, ReadOnlySpan<byte> deviceId,
        MessageFailure failure, DiagnosticJournal? diagnostics, DiagnosticTransport transport,
        Guid? sessionId, string operation)
    {
        if (addStrike is null) return;
        try { addStrike(deviceId.ToArray(), failure); }
        catch (Exception error)
        {
            diagnostics?.TryWrite(new("transport.protocol_strike_failed", operation)
            {
                SessionId = sessionId,
                Transport = transport,
                Level = DiagnosticLevel.Warning,
                ErrorCode = error.GetType().Name,
                HResult = error.HResult,
            });
        }
    }
}
