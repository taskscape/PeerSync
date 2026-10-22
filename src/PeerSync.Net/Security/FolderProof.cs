using System.Security.Cryptography;
using Google.Protobuf;
using PeerSync.Crypto;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Wire;

namespace PeerSync.Net.Security;

public static class FolderProof
{
    public static FolderAuth Create(FolderKeys keys, PeerAuthenticationContext context, ulong requestId)
    {
        ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(context);
        byte[] folder = keys.FolderId(), auth = keys.AuthKey; byte[]? read = keys.ReadOnlyAuthKey;
        try
        {
            var proof = new FolderAuth { FolderId = ByteString.CopyFrom(folder), RequestId = requestId,
                Access = keys.CanWrite ? FolderAccess.ReadWrite : keys.CanRead ? FolderAccess.ReadOnly : FolderAccess.Encrypted,
                Proof = ByteString.CopyFrom(HMACSHA256.HashData(auth, Input("psp1 auth"u8, context, folder, incoming: false))) };
            if (read is not null) proof.ReadOnlyProof = ByteString.CopyFrom(HMACSHA256.HashData(read, Input("psp1 ro-auth"u8, context, folder, incoming: false)));
            if (keys.CanWrite) proof.Signature = ByteString.CopyFrom(keys.Sign(Input("psp1 rw-auth"u8, context, folder, incoming: false)));
            return proof;
        }
        finally { CryptographicOperations.ZeroMemory(auth); if (read is not null) CryptographicOperations.ZeroMemory(read); }
    }

    public static bool TryVerifyRemote(FolderAuth proof, FolderKeys keys, PeerAuthenticationContext context, out FolderAccess access)
    {
        ArgumentNullException.ThrowIfNull(proof); ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(context);
        access = FolderAccess.Unknown;
        try { MessageValidator.Validate(proof); }
        catch (MessageValidationException) { return false; }
        byte[] folder = keys.FolderId();
        if (!CryptographicOperations.FixedTimeEquals(folder, proof.FolderId.Span)) return false;
        byte[] auth = keys.AuthKey; byte[]? read = keys.ReadOnlyAuthKey;
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(proof.Proof.Span, HMACSHA256.HashData(auth, Input("psp1 auth"u8, context, folder, incoming: true)))) return false;
            if (proof.Access != FolderAccess.Encrypted && (read is null || !CryptographicOperations.FixedTimeEquals(proof.ReadOnlyProof.Span,
                HMACSHA256.HashData(read, Input("psp1 ro-auth"u8, context, folder, incoming: true))))) return false;
            if (proof.Access == FolderAccess.ReadWrite && !keys.Verify(Input("psp1 rw-auth"u8, context, folder, incoming: true), proof.Signature.Span)) return false;
            access = proof.Access; return true;
        }
        finally { CryptographicOperations.ZeroMemory(auth); if (read is not null) CryptographicOperations.ZeroMemory(read); }
    }

    private static byte[] Input(ReadOnlySpan<byte> label, PeerAuthenticationContext context, ReadOnlySpan<byte> folder, bool incoming) =>
        [.. label, .. (incoming ? context.Remote : context.Local), .. (incoming ? context.Local : context.Remote),
         .. (incoming ? context.PeerNonce : context.OwnNonce), .. (incoming ? context.OwnNonce : context.PeerNonce), .. folder];
}
