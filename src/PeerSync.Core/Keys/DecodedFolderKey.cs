using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace PeerSync.Core.Keys;

/// <summary>Owns decoded key material until the caller imports it. Do not retain its span after disposal.</summary>
public sealed class DecodedFolderKey : IDisposable
{
    private readonly byte[] payload;
    private bool disposed;

    internal DecodedFolderKey(FolderKeyKind kind, byte[] payload)
    {
        Kind = kind;
        this.payload = payload;
    }

    public FolderKeyKind Kind { get; }
    public int PayloadLength => payload.Length;

    [JsonIgnore]
    public ReadOnlySpan<byte> Payload
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return payload;
        }
    }

    public override string ToString() => $"Folder key ({Kind}, redacted)";

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CryptographicOperations.ZeroMemory(payload);
    }
}
