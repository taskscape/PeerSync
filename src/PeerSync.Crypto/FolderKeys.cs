using System.Security.Cryptography;
using System.Text.Json.Serialization;
using NSec.Cryptography;
using PeerSync.Core.Keys;

namespace PeerSync.Crypto;

/// <summary>Folder capabilities. Key exports are explicit; string/JSON diagnostics never contain key material.</summary>
public sealed class FolderKeys : IDisposable
{
    private readonly object gate = new();
    private readonly byte[]? readWrite;
    private readonly byte[]? content;
    private readonly byte[] publicBytes;
    private readonly NSec.Cryptography.PublicKey publicKey;
    private readonly Key? signing;
    private bool disposed;

    private FolderKeys(FolderKeyKind kind, byte[]? readWrite, byte[]? content,
        NSec.Cryptography.PublicKey publicKey, Key? signing)
    {
        Kind = kind;
        this.readWrite = readWrite;
        this.content = content;
        this.publicKey = publicKey;
        this.signing = signing;
        publicBytes = publicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    public FolderKeyKind Kind { get; }
    public bool CanWrite => Kind == FolderKeyKind.ReadWrite;
    public bool CanRead => Kind != FolderKeyKind.Encrypted;

    [JsonIgnore]
    public byte[] PublicKey { get { lock (gate) { EnsureAlive(); return publicBytes.ToArray(); } } }
    [JsonIgnore]
    public byte[]? ReadWriteSecret { get { lock (gate) { EnsureAlive(); return readWrite?.ToArray(); } } }
    [JsonIgnore]
    public byte[]? ContentKey { get { lock (gate) { EnsureAlive(); return content?.ToArray(); } } }
    [JsonIgnore]
    public byte[] AuthKey { get { lock (gate) { EnsureAlive(); return Derive(publicBytes, "psp1 auth"u8); } } }
    [JsonIgnore]
    public byte[]? ReadOnlyAuthKey { get { lock (gate) { EnsureAlive(); return content is null ? null : Derive(content, "psp1 ro-auth"u8); } } }

    public static FolderKeys CreateNew()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        try { return FromReadWrite(secret); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public static FolderKeys FromReadWrite(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != 32) throw new ArgumentException("An RW secret must contain 32 bytes.", nameof(secret));
        byte[] owned = secret.ToArray();
        byte[]? seed = null, contentKey = null;
        Key? signingKey = null;
        try
        {
            seed = Derive(owned, "psp1 sign"u8);
            signingKey = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
            contentKey = Derive(owned, "psp1 content"u8);
            return new(FolderKeyKind.ReadWrite, owned, contentKey, signingKey.PublicKey, signingKey);
        }
        catch
        {
            signingKey?.Dispose();
            CryptographicOperations.ZeroMemory(owned);
            if (contentKey is not null) CryptographicOperations.ZeroMemory(contentKey);
            throw;
        }
        finally { if (seed is not null) CryptographicOperations.ZeroMemory(seed); }
    }

    public static FolderKeys FromReadOnly(ReadOnlySpan<byte> material)
    {
        if (material.Length != 64) throw new ArgumentException("An RO key must contain 64 bytes.", nameof(material));
        var publicKey = NSec.Cryptography.PublicKey.Import(SignatureAlgorithm.Ed25519, material[..32], KeyBlobFormat.RawPublicKey);
        byte[] contentKey = material[32..].ToArray();
        try { return new(FolderKeyKind.ReadOnly, null, contentKey, publicKey, null); }
        catch { CryptographicOperations.ZeroMemory(contentKey); throw; }
    }

    public static FolderKeys FromEncrypted(ReadOnlySpan<byte> publicMaterial)
    {
        if (publicMaterial.Length != 32) throw new ArgumentException("An encrypted-peer key must contain 32 bytes.", nameof(publicMaterial));
        var publicKey = NSec.Cryptography.PublicKey.Import(SignatureAlgorithm.Ed25519, publicMaterial, KeyBlobFormat.RawPublicKey);
        return new(FolderKeyKind.Encrypted, null, null, publicKey, null);
    }

    public static FolderKeys FromText(string text)
    {
        using var decoded = KeyText.Decode(text);
        return decoded.Kind switch
        {
            FolderKeyKind.ReadWrite => FromReadWrite(decoded.Payload),
            FolderKeyKind.ReadOnly => FromReadOnly(decoded.Payload),
            FolderKeyKind.Encrypted => FromEncrypted(decoded.Payload),
            _ => throw new FormatException("Invalid folder key."),
        };
    }

    public byte[] FolderId()
    {
        lock (gate)
        {
            EnsureAlive();
            Span<byte> input = stackalloc byte[43];
            "psp1 folder"u8.CopyTo(input);
            publicBytes.CopyTo(input[11..]);
            return SHA256.HashData(input);
        }
    }

    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        lock (gate)
        {
            EnsureAlive();
            if (signing is null) throw new InvalidOperationException("A read-write key is required to sign.");
            return SignatureAlgorithm.Ed25519.Sign(signing, message);
        }
    }

    public bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        lock (gate) { EnsureAlive(); return SignatureAlgorithm.Ed25519.Verify(publicKey, message, signature); }
    }

    public string ExportText() => Kind switch
    {
        FolderKeyKind.ReadWrite => ExportReadWriteText(),
        FolderKeyKind.ReadOnly => ExportReadOnlyText(),
        _ => ExportEncryptedText(),
    };

    public string ExportReadWriteText()
    {
        lock (gate)
        {
            EnsureAlive();
            if (readWrite is null) throw new InvalidOperationException("This peer has no read-write secret.");
            return KeyText.Encode(FolderKeyKind.ReadWrite, readWrite);
        }
    }

    public string ExportReadOnlyText()
    {
        lock (gate)
        {
            EnsureAlive();
            if (content is null) throw new InvalidOperationException("This peer has no content key.");
            Span<byte> material = stackalloc byte[64];
            try
            {
                publicBytes.CopyTo(material);
                content.CopyTo(material[32..]);
                return KeyText.Encode(FolderKeyKind.ReadOnly, material);
            }
            finally { CryptographicOperations.ZeroMemory(material); }
        }
    }

    public string ExportEncryptedText()
    {
        lock (gate) { EnsureAlive(); return KeyText.Encode(FolderKeyKind.Encrypted, publicBytes); }
    }

    public override string ToString() => $"Folder keys ({Kind}, redacted)";

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { signing?.Dispose(); }
            finally
            {
                if (readWrite is not null) CryptographicOperations.ZeroMemory(readWrite);
                if (content is not null) CryptographicOperations.ZeroMemory(content);
            }
        }
    }

    private void EnsureAlive() => ObjectDisposedException.ThrowIf(disposed, this);

    private static byte[] Derive(ReadOnlySpan<byte> input, ReadOnlySpan<byte> info)
    {
        byte[] output = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, input, output, salt: [], info: info);
        return output;
    }
}
