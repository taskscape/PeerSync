using System.Security.Cryptography;
using System.Text.Json;
using PeerSync.Core.Keys;
using Shouldly;
using Xunit;

namespace PeerSync.Crypto.Tests;

public sealed class FolderKeyProtectorTests
{
    [Fact]
    public void AllRolesRoundTripThroughMachineProtectionWithoutExposingPlaintext()
    {
        using var rw = FolderKeys.CreateNew();
        using var ro = FolderKeys.FromText(rw.ExportReadOnlyText());
        using var encrypted = FolderKeys.FromText(rw.ExportEncryptedText());
        foreach (var keys in new[] { rw, ro, encrypted })
        {
            using var stored = FolderKeyProtector.Protect(keys);
            byte[] blob = stored.ExportCiphertext();
            byte[] identity = stored.ExportFolderId();
            using var opened = FolderKeyProtector.Unprotect(blob, identity, keys.Kind);
            string.Equals(opened.ExportText(), keys.ExportText(), StringComparison.Ordinal).ShouldBeTrue();
            JsonSerializer.Serialize(stored).ShouldBe($"{{\"Kind\":{(int)keys.Kind}}}");
            stored.ToString().ShouldContain("redacted");
            if (keys.ReadWriteSecret is { } secret)
            {
                try { blob.AsSpan().IndexOf(secret).ShouldBe(-1); }
                finally { CryptographicOperations.ZeroMemory(secret); }
            }
            Array.Clear(blob); Array.Clear(identity);
            using var stillOwned = FolderKeyProtector.Unprotect(stored.ExportCiphertext(), stored.ExportFolderId());
            stillOwned.Kind.ShouldBe(keys.Kind);
        }
    }

    [Fact]
    public void TamperingWrongFolderAndWrongRoleFailClosed()
    {
        using var keys = FolderKeys.CreateNew();
        using var other = FolderKeys.CreateNew();
        using var stored = FolderKeyProtector.Protect(keys);
        byte[] blob = stored.ExportCiphertext();
        byte[] identity = stored.ExportFolderId();
        Should.Throw<CryptographicException>(() => FolderKeyProtector.Unprotect(blob, other.FolderId()));
        Should.Throw<CryptographicException>(() => FolderKeyProtector.Unprotect(blob, identity, FolderKeyKind.ReadOnly));
        blob[blob.Length / 2] ^= 1;
        Should.Throw<CryptographicException>(() => FolderKeyProtector.Unprotect(blob, identity));
        Should.Throw<CryptographicException>(() => FolderKeyProtector.Unprotect([], identity));
        Should.Throw<CryptographicException>(() => FolderKeyProtector.Unprotect(new byte[4097], identity));
    }

    [Fact]
    public void VersionAndEmbeddedIdentityAreValidatedEvenForAValidDpapiBlob()
    {
        using var keys = FolderKeys.CreateNew();
        byte[] payload = new byte[66];
        byte[] secret = keys.ReadWriteSecret!;
        try
        {
            payload[0] = 2; // Unknown future frame version.
            keys.FolderId().CopyTo(payload, 2);
            secret.CopyTo(payload, 34);
            byte[] blob = ProtectedData.Protect(payload, DataProtectionScope.LocalMachine, "PeerSync folder key v1"u8);
            Should.Throw<CryptographicException>(() => FolderKeyProtector.Unprotect(blob, keys.FolderId()));
            payload[0] = 1;
            payload[34] ^= 1; // Header identity no longer matches derived material.
            blob = ProtectedData.Protect(payload, DataProtectionScope.LocalMachine, "PeerSync folder key v1"u8);
            Should.Throw<CryptographicException>(() => FolderKeyProtector.Unprotect(blob, keys.FolderId()));
        }
        finally { CryptographicOperations.ZeroMemory(payload); CryptographicOperations.ZeroMemory(secret); }
    }

    [Fact]
    public void DisposedProtectedObjectsDenyFurtherExports()
    {
        using var keys = FolderKeys.CreateNew();
        var stored = FolderKeyProtector.Protect(keys);
        stored.Dispose(); stored.Dispose();
        Should.Throw<ObjectDisposedException>(() => stored.ExportCiphertext());
        Should.Throw<ObjectDisposedException>(() => stored.ExportFolderId());
        JsonSerializer.Serialize(stored).ShouldBe("{\"Kind\":0}");
    }
}
