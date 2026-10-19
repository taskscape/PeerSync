using System.Text.Json;
using PeerSync.Core.Keys;
using PeerSync.TestFixtures;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed class KeyTextTests
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    [Fact]
    public void IndependentVectorsRoundTripAndNormalize()
    {
        foreach (var vector in FolderKeyVectors.All())
        foreach (var (kind, text, payload) in Forms(vector))
        {
            KeyText.Encode(kind, payload).ShouldBe(text);
            using var decoded = KeyText.Decode(text);
            decoded.Kind.ShouldBe(kind);
            decoded.Payload.ToArray().ShouldBe(payload);
            string decorated = " \t" + string.Join("-\r\n", text.ToLowerInvariant().Chunk(7).Select(c => new string(c))) + "\u2003";
            using var normalized = KeyText.Decode(decorated);
            normalized.Payload.ToArray().ShouldBe(payload);
            normalized.Kind.ShouldBe(kind);
        }
    }

    [Fact]
    public void EverySingleSymbolReplacementInsertionAndDeletionIsRejected()
    {
        foreach (var vector in FolderKeyVectors.All())
        foreach (var (_, text, _) in Forms(vector))
        {
            char[] candidate = text.ToCharArray();
            for (int position = 0; position < text.Length; position++)
            {
                foreach (char replacement in Alphabet)
                {
                    if (replacement == text[position]) continue;
                    candidate[position] = replacement;
                    KeyText.TryDecode(candidate, out var decoded).ShouldBeFalse();
                    decoded.ShouldBeNull();
                }
                candidate[position] = text[position];
                KeyText.TryDecode(text.Remove(position, 1), out _).ShouldBeFalse();
            }
            for (int position = 0; position <= text.Length; position++)
                KeyText.TryDecode(text.Insert(position, "A"), out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void NonzeroPaddingCannotAliasAValidKey()
    {
        foreach (var vector in FolderKeyVectors.All())
        foreach (var (_, text, _) in Forms(vector))
        {
            char[] candidate = text.ToCharArray();
            int position = text.Length - 3;
            candidate[position] = Alphabet[Alphabet.IndexOf(candidate[position]) | 1];
            KeyText.TryDecode(candidate, out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void InvalidInputHasBoundedParsingAndRedactedErrors()
    {
        foreach (string? input in new string?[] { null, "", "--- \t", "secret not a key", new('A', 4097), "\uFF21" + new string('A', 54) })
            KeyText.TryDecode(input, out _).ShouldBeFalse();
        Should.Throw<FormatException>(() => KeyText.Decode("secret not a key"))
            .Message.ShouldBe("Invalid folder key.");
        Should.Throw<ArgumentException>(() => KeyText.Encode(FolderKeyKind.ReadWrite, new byte[31]));
        Should.Throw<ArgumentOutOfRangeException>(() => KeyText.Encode((FolderKeyKind)99, new byte[32]));
    }

    [Fact]
    public void DecodedPayloadIsRedactedAndClearedOnDispose()
    {
        string text = FolderKeyVectors.All().Last().Text("readWriteText");
        var decoded = KeyText.Decode(text);
        ReadOnlySpan<byte> retained = decoded.Payload;
        decoded.ToString().ShouldNotContain(text);
        JsonSerializer.Serialize(decoded).ShouldNotContain("Payload\"");
        decoded.Dispose();
        foreach (byte value in retained) value.ShouldBe((byte)0);
        Should.Throw<ObjectDisposedException>(() => { _ = decoded.Payload.Length; });
        decoded.Dispose();
    }

    private static IEnumerable<(FolderKeyKind Kind, string Text, byte[] Payload)> Forms(JsonElement vector)
    {
        yield return (FolderKeyKind.ReadWrite, vector.Text("readWriteText"), vector.Bytes("readWrite"));
        yield return (FolderKeyKind.ReadOnly, vector.Text("readOnlyText"), [.. vector.Bytes("publicKey"), .. vector.Bytes("contentKey")]);
        yield return (FolderKeyKind.Encrypted, vector.Text("encryptedText"), vector.Bytes("publicKey"));
    }
}
