using System.Text.Json;
using PeerSync.Core.Keys;
using PeerSync.TestFixtures;
using Shouldly;
using Xunit;

namespace PeerSync.Crypto.Tests;

public sealed class FolderKeysTests
{
    [Fact]
    public void DerivationAndSignaturesMatchIndependentOpenSslVectors()
    {
        foreach (var vector in FolderKeyVectors.All())
        {
            using var keys = FolderKeys.FromReadWrite(vector.Bytes("readWrite"));
            keys.PublicKey.ShouldBe(vector.Bytes("publicKey"));
            keys.ContentKey.ShouldBe(vector.Bytes("contentKey"));
            keys.AuthKey.ShouldBe(vector.Bytes("authKey"));
            keys.ReadOnlyAuthKey.ShouldBe(vector.Bytes("readOnlyAuthKey"));
            keys.FolderId().ShouldBe(vector.Bytes("folderId"));
            keys.Sign(vector.Bytes("message")).ShouldBe(vector.Bytes("signature"));
            keys.ExportReadWriteText().ShouldBe(vector.Text("readWriteText"));
            keys.ExportReadOnlyText().ShouldBe(vector.Text("readOnlyText"));
            keys.ExportEncryptedText().ShouldBe(vector.Text("encryptedText"));
        }
    }

    [Fact]
    public void ImportsPreserveCapabilitiesAndPublicIdentity()
    {
        foreach (var vector in FolderKeyVectors.All())
        foreach (var (property, kind) in new[] { ("readWriteText", FolderKeyKind.ReadWrite), ("readOnlyText", FolderKeyKind.ReadOnly), ("encryptedText", FolderKeyKind.Encrypted) })
        {
            using var keys = FolderKeys.FromText(vector.Text(property));
            keys.Kind.ShouldBe(kind);
            keys.CanRead.ShouldBe(kind != FolderKeyKind.Encrypted);
            keys.CanWrite.ShouldBe(kind == FolderKeyKind.ReadWrite);
            keys.ExportText().ShouldBe(vector.Text(property));
            keys.PublicKey.ShouldBe(vector.Bytes("publicKey"));
            keys.AuthKey.ShouldBe(vector.Bytes("authKey"));
            keys.FolderId().ShouldBe(vector.Bytes("folderId"));
            keys.Verify(vector.Bytes("message"), vector.Bytes("signature")).ShouldBeTrue();
            if (!keys.CanWrite)
            {
                keys.ReadWriteSecret.ShouldBeNull();
                Should.Throw<InvalidOperationException>(() => keys.Sign([1]));
                Should.Throw<InvalidOperationException>(() => keys.ExportReadWriteText());
            }
            if (!keys.CanRead)
            {
                keys.ContentKey.ShouldBeNull();
                keys.ReadOnlyAuthKey.ShouldBeNull();
                Should.Throw<InvalidOperationException>(() => keys.ExportReadOnlyText());
            }
            else keys.ExportReadOnlyText().ShouldBe(vector.Text("readOnlyText"));
        }
    }

    [Fact]
    public void VerificationRejectsChangedMessagesSignaturesAndForeignFolders()
    {
        var vector = FolderKeyVectors.All().First();
        using var keys = FolderKeys.FromText(vector.Text("readWriteText"));
        using var copy = FolderKeys.FromText(keys.ExportText());
        byte[] message = vector.Bytes("message"), signature = keys.Sign(message);
        copy.Sign(message).ShouldBe(signature);
        for (int i = 0; i < signature.Length; i++)
        {
            signature[i] ^= 1;
            keys.Verify(message, signature).ShouldBeFalse();
            signature[i] ^= 1;
        }
        for (int i = 0; i < message.Length; i++)
        {
            message[i] ^= 1;
            keys.Verify(message, signature).ShouldBeFalse();
            message[i] ^= 1;
        }
        using var foreign = FolderKeys.FromText(FolderKeyVectors.All().Last().Text("encryptedText"));
        foreign.Verify(message, signature).ShouldBeFalse();
        keys.Verify(message, []).ShouldBeFalse();
        keys.Verify(message, new byte[65]).ShouldBeFalse();
    }

    [Fact]
    public void SecretsAreOwnedAndExportsAreDefensiveCopies()
    {
        var vector = FolderKeyVectors.All().Last();
        byte[] input = vector.Bytes("readWrite");
        using var keys = FolderKeys.FromReadWrite(input);
        Array.Clear(input);
        Array.Clear(keys.ReadWriteSecret!);
        Array.Clear(keys.ContentKey!);
        Array.Clear(keys.PublicKey);
        keys.ExportReadWriteText().ShouldBe(vector.Text("readWriteText"));
        keys.ExportReadOnlyText().ShouldBe(vector.Text("readOnlyText"));
        byte[] roInput = [.. vector.Bytes("publicKey"), .. vector.Bytes("contentKey")];
        using var ro = FolderKeys.FromReadOnly(roInput);
        Array.Clear(roInput);
        ro.ExportText().ShouldBe(vector.Text("readOnlyText"));
    }

    [Fact]
    public void FreshKeysRoundTripAndDisposalDeniesFurtherAccess()
    {
        using var first = FolderKeys.CreateNew();
        using var second = FolderKeys.CreateNew();
        first.PublicKey.SequenceEqual(second.PublicKey).ShouldBeFalse();
        using var copy = FolderKeys.FromText(first.ExportText());
        copy.PublicKey.ShouldBe(first.PublicKey);
        string json = JsonSerializer.Serialize(first);
        json.ShouldBe("{\"Kind\":0,\"CanWrite\":true,\"CanRead\":true}");
        first.ToString().ShouldBe("Folder keys (ReadWrite, redacted)");
        first.Dispose();
        first.Dispose();
        Should.Throw<ObjectDisposedException>(() => first.ExportText());
        Should.Throw<ObjectDisposedException>(() => first.Sign([]));
        Should.Throw<ObjectDisposedException>(() => first.PublicKey);
        Should.Throw<ObjectDisposedException>(() => first.AuthKey);
        Should.Throw<ObjectDisposedException>(() => first.Verify([], []));
        JsonSerializer.Serialize(first).ShouldBe(json);
    }
}
