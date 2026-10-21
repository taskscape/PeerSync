namespace PeerSync.Protocol;

public enum ProtocolError
{
    InvalidLengthPrefix = 1,
    FrameTooLarge = 2,
    TruncatedFrame = 3,
    UnknownMessageType = 4,
    MalformedMessage = 5,
}

/// <summary>A safe reason code, without peer-controlled message contents.</summary>
public sealed class ProtocolException(ProtocolError code) : IOException("PSP/1 protocol violation: " + code)
{
    public ProtocolError Code { get; } = code;
}
