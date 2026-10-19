using System.Text.Json.Serialization;

namespace PeerSync.Core.Sync;

/// <summary>The exact signed body. Parsed messages are views, never replacements for these bytes.</summary>
public sealed class SignedRecord
{
    public const int MaxBodyBytes = 16 * 1024 * 1024;
    private readonly byte[] body;
    private readonly byte[] signature;

    public SignedRecord(ReadOnlySpan<byte> body, ReadOnlySpan<byte> signature, ulong localSequence = 0)
    {
        if (body.Length > MaxBodyBytes) throw new ArgumentException("Record body exceeds limit.", nameof(body));
        if (signature.Length != 64) throw new ArgumentException("Record signature must contain 64 bytes.", nameof(signature));
        this.body = body.ToArray();
        this.signature = signature.ToArray();
        LocalSequence = localSequence;
    }

    private SignedRecord(byte[] body, byte[] signature, ulong localSequence)
    {
        this.body = body;
        this.signature = signature;
        LocalSequence = localSequence;
    }

    [JsonIgnore] public ReadOnlySpan<byte> Body => body;
    [JsonIgnore] public ReadOnlySpan<byte> Signature => signature;
    public ulong LocalSequence { get; }
    public int BodyLength => body.Length;
    public SignedRecord WithLocalSequence(ulong sequence) => new(body, signature, sequence);
    public override string ToString() => $"Signed record ({BodyLength} bytes, sequence {LocalSequence})";
}
