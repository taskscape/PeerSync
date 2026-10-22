using System.Security.Cryptography;
using Google.Protobuf;
using PeerSync.Crypto;
using PeerSync.Net.Security;
using PeerSync.Protocol.Wire;
using Shouldly;
using Xunit;

namespace PeerSync.Net.Tests;

public sealed class FolderProofTests
{
    [Fact]
    public void ProofIsDirectionalAndBoundToBothCertificatesNoncesAndFolder()
    {
        using var keys = FolderKeys.CreateNew(); using var other = FolderKeys.CreateNew();
        byte[] a = RandomNumberGenerator.GetBytes(32), b = RandomNumberGenerator.GetBytes(32);
        byte[] an = RandomNumberGenerator.GetBytes(32), bn = RandomNumberGenerator.GetBytes(32);
        var sender = new PeerAuthenticationContext(a, b, an, bn);
        var receiver = new PeerAuthenticationContext(b, a, bn, an);
        var proof = FolderProof.Create(keys, sender, 7);
        FolderProof.TryVerifyRemote(proof, keys, receiver, out var role).ShouldBeTrue(); role.ShouldBe(FolderAccess.ReadWrite);
        sender.SessionId.ShouldBe(receiver.SessionId);
        FolderProof.TryVerifyRemote(proof, keys, sender, out _).ShouldBeFalse(); // reflected
        FolderProof.TryVerifyRemote(proof, other, receiver, out _).ShouldBeFalse();
        foreach (var altered in new[] {
            new PeerAuthenticationContext(RandomNumberGenerator.GetBytes(32), a, bn, an),
            new PeerAuthenticationContext(b, RandomNumberGenerator.GetBytes(32), bn, an),
            new PeerAuthenticationContext(b, a, RandomNumberGenerator.GetBytes(32), an),
            new PeerAuthenticationContext(b, a, bn, RandomNumberGenerator.GetBytes(32)) })
            FolderProof.TryVerifyRemote(proof, keys, altered, out _).ShouldBeFalse();
        a[0] ^= 1; an[0] ^= 1; sender.LocalDeviceId[0] ^= 1;
        FolderProof.TryVerifyRemote(proof, keys, receiver, out _).ShouldBeTrue();
    }

    [Fact]
    public void ReadOnlyAndPublicFolderKnowledgeCannotClaimWriteOrPlaintextPrivileges()
    {
        using var rw = FolderKeys.CreateNew(); using var ro = FolderKeys.FromText(rw.ExportReadOnlyText());
        using var encrypted = FolderKeys.FromText(rw.ExportEncryptedText());
        var a = new PeerAuthenticationContext(new byte[32], Enumerable.Repeat((byte)1, 32).ToArray(), new byte[32], new byte[32]);
        var b = new PeerAuthenticationContext(a.RemoteDeviceId, a.LocalDeviceId, new byte[32], new byte[32]);
        var proof = FolderProof.Create(ro, a, 1);
        FolderProof.TryVerifyRemote(proof, rw, b, out var access).ShouldBeTrue(); access.ShouldBe(FolderAccess.ReadOnly);
        proof.Access = FolderAccess.ReadWrite; proof.Signature = ByteString.CopyFrom(new byte[64]);
        FolderProof.TryVerifyRemote(proof, rw, b, out _).ShouldBeFalse();
        proof = FolderProof.Create(encrypted, a, 2);
        FolderProof.TryVerifyRemote(proof, rw, b, out access).ShouldBeTrue(); access.ShouldBe(FolderAccess.Encrypted);
        proof.Access = FolderAccess.ReadOnly; proof.ReadOnlyProof = ByteString.CopyFrom(new byte[32]);
        FolderProof.TryVerifyRemote(proof, rw, b, out _).ShouldBeFalse();
        proof = FolderProof.Create(rw, a, 3); byte[] signature = proof.Signature.ToByteArray(); signature[0] ^= 1;
        proof.Signature = ByteString.CopyFrom(signature); FolderProof.TryVerifyRemote(proof, ro, b, out _).ShouldBeFalse();
    }
}
