using Google.Protobuf;
using PeerSync.Crypto;
using PeerSync.Protocol.Framing;

namespace PeerSync.Protocol.Validation;

public interface IProtocolViolationSink
{
    void AddStrike(ReadOnlySpan<byte> certificateDeviceId, MessageFailure failure);
    ValueTask CloseAsync(MessageFailure failure);
}

/// <summary>Identity is bound by the TLS owner, never supplied by Hello.</summary>
public sealed class PeerMessageGuard
{
    private readonly byte[] device;
    private readonly IProtocolViolationSink sink;
    private int rejected;
    public PeerMessageGuard(ReadOnlySpan<byte> certificateDeviceId, IProtocolViolationSink sink)
    {
        if (certificateDeviceId.Length != 32) throw new ArgumentException("Certificate device identity must contain 32 bytes.", nameof(certificateDeviceId));
        ArgumentNullException.ThrowIfNull(sink); device = certificateDeviceId.ToArray(); this.sink = sink;
    }

    public async ValueTask<IMessage> DecodeAsync(ProtocolFrame frame, FolderKeys? authenticatedFolder = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref rejected) != 0, this);
        try
        {
            var message = WireMessageCodec.Decode(frame);
            MessageValidator.Validate(message, authenticatedFolder);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref rejected) != 0, this);
            return message;
        }
        catch (MessageValidationException error) { await RejectAsync(error.Failure).ConfigureAwait(false); throw; }
        catch (ProtocolException) { await RejectAsync(MessageFailure.InvalidFraming).ConfigureAwait(false); throw; }
    }

    public async ValueTask RejectAsync(MessageFailure failure)
    {
        if (Interlocked.Exchange(ref rejected, 1) != 0) return;
        try { sink.AddStrike(device, failure); }
        finally { await sink.CloseAsync(failure).ConfigureAwait(false); }
    }
}
