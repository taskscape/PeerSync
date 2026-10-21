namespace PeerSync.Protocol.Validation;

public enum MessageFailure
{
    UnsupportedVersion = 1, InvalidHello = 2, InvalidAuthentication = 3,
    FolderNotAuthorized = 4, TooManyRecords = 5, InvalidSequence = 6,
    InvalidSignedRecord = 7, InvalidRecord = 8, InvalidPath = 9,
    TooManyBlockRequests = 10, InvalidBlockRequest = 11, InvalidBlockResponse = 12,
    InvalidMessage = 13, InvalidFraming = 14,
}

public sealed class MessageValidationException(MessageFailure failure) : IOException("PSP/1 message rejected: " + failure)
{
    public MessageFailure Failure { get; } = failure;
}
