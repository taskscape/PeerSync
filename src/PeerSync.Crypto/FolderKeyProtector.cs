using System.Security.Cryptography;
using PeerSync.Core.Keys;

namespace PeerSync.Crypto;

/// <summary>Windows machine-scope DPAPI. State ACLs remain the access-control boundary.</summary>
public static class FolderKeyProtector
{
    private const int HeaderLength = 34;
    // This is the plan's fixed v1 entropy. Never change it without a migration.
    private static ReadOnlySpan<byte> Entropy => "PeerSync folder key v1"u8;

    public static ProtectedFolderKey Protect(FolderKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        byte[] id = keys.FolderId();
        Span<byte> plaintext = stackalloc byte[HeaderLength + 64];
        byte[]? material = null, content = null;
        bool transferred = false;
        try
        {
            plaintext[0] = 1;
            plaintext[1] = (byte)keys.Kind;
            id.CopyTo(plaintext[2..HeaderLength]);
            int payloadLength;
            switch (keys.Kind)
            {
                case FolderKeyKind.ReadWrite:
                    material = keys.ReadWriteSecret!;
                    material.CopyTo(plaintext[HeaderLength..]);
                    payloadLength = 32;
                    break;
                case FolderKeyKind.ReadOnly:
                    material = keys.PublicKey;
                    content = keys.ContentKey!;
                    material.CopyTo(plaintext[HeaderLength..]);
                    content.CopyTo(plaintext[(HeaderLength + 32)..]);
                    payloadLength = 64;
                    break;
                case FolderKeyKind.Encrypted:
                    material = keys.PublicKey;
                    material.CopyTo(plaintext[HeaderLength..]);
                    payloadLength = 32;
                    break;
                default: throw new ArgumentException("Invalid folder-key role.", nameof(keys));
            }
            byte[] ciphertext = ProtectedData.Protect(plaintext[..(HeaderLength + payloadLength)],
                DataProtectionScope.LocalMachine, Entropy);
            var result = new ProtectedFolderKey(keys.Kind, id, ciphertext);
            transferred = true;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (material is not null) CryptographicOperations.ZeroMemory(material);
            if (content is not null) CryptographicOperations.ZeroMemory(content);
            if (!transferred) CryptographicOperations.ZeroMemory(id);
        }
    }

    public static FolderKeys Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> expectedFolderId,
        FolderKeyKind? expectedKind = null)
    {
        if (expectedFolderId.Length != 32) throw new ArgumentException("Folder identity must contain 32 bytes.", nameof(expectedFolderId));
        if (expectedKind.HasValue && !Enum.IsDefined(expectedKind.Value)) throw new ArgumentOutOfRangeException(nameof(expectedKind));
        if (ciphertext.Length is 0 or > 4096) throw new CryptographicException("Invalid protected folder key.");
        byte[] plaintext = ProtectedData.Unprotect(ciphertext, DataProtectionScope.LocalMachine, Entropy);
        FolderKeys? keys = null;
        try
        {
            if (plaintext.Length is not (HeaderLength + 32 or HeaderLength + 64) || plaintext[0] != 1)
                throw new CryptographicException("Invalid protected folder key.");
            var kind = (FolderKeyKind)plaintext[1];
            if (!Enum.IsDefined(kind) || (expectedKind.HasValue && expectedKind.Value != kind)
                || plaintext.Length != HeaderLength + (kind == FolderKeyKind.ReadOnly ? 64 : 32)
                || !CryptographicOperations.FixedTimeEquals(plaintext.AsSpan(2, 32), expectedFolderId))
                throw new CryptographicException("Protected folder key context mismatch.");
            var payload = plaintext.AsSpan(HeaderLength);
            try
            {
                keys = kind switch
                {
                    FolderKeyKind.ReadWrite => FolderKeys.FromReadWrite(payload),
                    FolderKeyKind.ReadOnly => FolderKeys.FromReadOnly(payload),
                    _ => FolderKeys.FromEncrypted(payload),
                };
            }
            catch (Exception error) when (error is ArgumentException or FormatException)
            { throw new CryptographicException("Invalid protected folder key material.", error); }
            if (!CryptographicOperations.FixedTimeEquals(keys.FolderId(), expectedFolderId))
                throw new CryptographicException("Protected folder key identity mismatch.");
            var result = keys;
            keys = null;
            return result;
        }
        finally
        {
            keys?.Dispose();
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
