using Google.Protobuf;
using PeerSync.Crypto;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using Shouldly;
using Xunit;
using Envelope = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Protocol.Tests;

public sealed class SignedRecordTests
{
    [Fact]
    public void SignaturesBindEveryBodyByteAndEverySignatureByte()
    {
        using var keys = FolderKeys.CreateNew();
        var record = SignedRecordFactory.Create(Sample(), keys, 4);
        byte[] body = record.Body.ToArray(), signature = record.Signature.ToArray();
        for (int index = 0; index < body.Length; index++)
        {
            body[index] ^= 1;
            SignedRecordFactory.TryOpen(new Envelope(body, signature), keys, out _).ShouldBeFalse();
            body[index] ^= 1;
        }
        for (int index = 0; index < signature.Length; index++)
        {
            signature[index] ^= 1;
            SignedRecordFactory.TryOpen(new Envelope(body, signature), keys, out _).ShouldBeFalse();
            signature[index] ^= 1;
        }
        SignedRecordFactory.TryOpen(record, keys, out var opened).ShouldBeTrue();
        opened!.Path.ShouldBe("folder/é.txt");
    }

    [Fact]
    public void ForwardingChangesOnlySequenceAndPreservesUnknownFieldsAndOriginalOrder()
    {
        using var rw = FolderKeys.CreateNew();
        using var ro = FolderKeys.FromText(rw.ExportReadOnlyText());
        using var encrypted = FolderKeys.FromText(rw.ExportEncryptedText());
        // Unknown field 99 precedes the known fields. Parse/reserialize moves it.
        byte[] original = [0x98, 0x06, 0x01, .. Sample().ToByteArray()];
        byte[] signature = rw.Sign(Input(rw, original));
        var record = new Envelope(original, signature, 5);
        foreach (var keys in new[] { rw, ro, encrypted })
        {
            SignedRecordFactory.TryOpen(record, keys, out var parsed).ShouldBeTrue();
            parsed!.ToByteArray().SequenceEqual(original).ShouldBeFalse();
            parsed.Path = "changed-view.txt";
            var forwarded = SignedRecordFactory.FromWire(SignedRecordFactory.ToWire(record.WithLocalSequence(999)));
            forwarded.LocalSequence.ShouldBe(999ul);
            forwarded.Body.ToArray().ShouldBe(original);
            forwarded.Signature.ToArray().ShouldBe(signature);
            SignedRecordFactory.TryOpen(forwarded, keys, out var reopened).ShouldBeTrue();
            reopened!.Path.ShouldBe("folder/é.txt");
        }
        Array.Clear(original);
        Array.Clear(signature);
        SignedRecordFactory.TryOpen(record, rw, out _).ShouldBeTrue();
    }

    [Fact]
    public void RecordSignaturesCannotBeReplayedInAnotherFolderOrSigningDomain()
    {
        using var first = FolderKeys.CreateNew();
        using var other = FolderKeys.CreateNew();
        var record = SignedRecordFactory.Create(Sample(), first);
        SignedRecordFactory.TryOpen(record, other, out _).ShouldBeFalse();
        byte[] wrongDomain = first.Sign([.. "psp1 other"u8, .. first.FolderId(), .. record.Body]);
        SignedRecordFactory.TryOpen(new Envelope(record.Body, wrongDomain), first, out _).ShouldBeFalse();
    }

    [Fact]
    public void SignedMalformedProtobufHostilePathsAndNoncanonicalVectorsAreRejected()
    {
        using var keys = FolderKeys.CreateNew();
        foreach (byte[] body in new[]
        {
            new byte[] { 0x0a, 0x7f }, new FileRecordBody { Path = "../escape" }.ToByteArray(),
            new FileRecordBody { Path = "file", Vv = { new VvEntry { Device = 1, Counter = 0 } } }.ToByteArray(),
            new FileRecordBody { Path = "file", Vv = { new VvEntry { Device = 2, Counter = 1 }, new VvEntry { Device = 1, Counter = 1 } } }.ToByteArray(),
        })
        {
            var record = new Envelope(body, keys.Sign(Input(keys, body)));
            SignedRecordFactory.TryOpen(record, keys, out var parsed).ShouldBeFalse();
            parsed.ShouldBeNull();
        }
        Should.Throw<ArgumentException>(() => SignedRecordFactory.Create(new FileRecordBody { Path = "../escape" }, keys));
        Should.Throw<ArgumentException>(() => new Envelope([], new byte[63]));
        Should.Throw<ArgumentException>(() => new Envelope(new byte[Envelope.MaxBodyBytes + 1], new byte[64]));
    }

    [Fact]
    public void CreationSortsVectorsWithoutMutatingTheCallerAndOnlyRwCanSign()
    {
        using var rw = FolderKeys.CreateNew();
        using var same = FolderKeys.FromText(rw.ExportReadWriteText());
        using var ro = FolderKeys.FromText(rw.ExportReadOnlyText());
        using var encrypted = FolderKeys.FromText(rw.ExportEncryptedText());
        var body = Sample();
        body.Vv.Clear();
        body.Vv.Add(new VvEntry { Device = ulong.MaxValue, Counter = 128 });
        body.Vv.Add(new VvEntry { Device = 1, Counter = 1 });
        var first = SignedRecordFactory.Create(body, rw);
        body.Vv[0].Device.ShouldBe(ulong.MaxValue);
        var second = SignedRecordFactory.Create(body, same, 70);
        first.Body.ToArray().ShouldBe(second.Body.ToArray());
        first.Signature.ToArray().ShouldBe(second.Signature.ToArray());
        SignedRecordFactory.TryOpen(first, ro, out var parsed).ShouldBeTrue();
        parsed!.Vv.Select(v => v.Device).ShouldBe(new ulong[] { 1, ulong.MaxValue });
        Should.Throw<InvalidOperationException>(() => SignedRecordFactory.Create(body, ro));
        Should.Throw<InvalidOperationException>(() => SignedRecordFactory.Create(body, encrypted));
        body.Vv.Add(new VvEntry { Device = 1, Counter = 3 });
        Should.Throw<ArgumentException>(() => SignedRecordFactory.Create(body, rw));
    }

    private static byte[] Input(FolderKeys keys, ReadOnlySpan<byte> body) => [.. "psp1 record"u8, .. keys.FolderId(), .. body];

    private static FileRecordBody Sample() => new()
    {
        Path = "folder/é.txt", Size = 1, MtimeTicks = 638000000000000000,
        ChangedTicks = 638000000000000000, Attributes = 2,
        Vv = { new VvEntry { Device = 10, Counter = 3 } },
        FileHash = ByteString.CopyFrom(new byte[32]), ModifiedBy = ByteString.CopyFrom(new byte[32]),
        Chunks = { new Chunk { Length = 1, Hash = ByteString.CopyFrom(new byte[32]) } },
    };
}
