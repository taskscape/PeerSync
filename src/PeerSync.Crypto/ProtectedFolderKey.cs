using System.Security.Cryptography;
using PeerSync.Core.Keys;

namespace PeerSync.Crypto;

/// <summary>Opaque machine-bound ciphertext. Export is explicit and diagnostics contain no blob or identity.</summary>
public sealed class ProtectedFolderKey : IDisposable
{
    private readonly object gate = new();
    private readonly byte[] folderId;
    private readonly byte[] ciphertext;
    private bool disposed;

    internal ProtectedFolderKey(FolderKeyKind kind, byte[] folderId, byte[] ciphertext)
    {
        Kind = kind;
        this.folderId = folderId;
        this.ciphertext = ciphertext;
    }

    public FolderKeyKind Kind { get; }
    public byte[] ExportFolderId() { lock (gate) { EnsureAlive(); return folderId.ToArray(); } }
    public byte[] ExportCiphertext() { lock (gate) { EnsureAlive(); return ciphertext.ToArray(); } }
    public override string ToString() => $"Protected folder key ({Kind}, redacted)";

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(folderId);
        }
    }

    private void EnsureAlive() => ObjectDisposedException.ThrowIf(disposed, this);
}
