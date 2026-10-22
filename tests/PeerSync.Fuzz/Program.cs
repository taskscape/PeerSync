using System.Diagnostics;
using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using PeerSync.Core.Ignore;
using PeerSync.Core.Keys;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto.Chunking;
using PeerSync.Protocol;
using PeerSync.Protocol.Framing;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Validation;

const string Targets = "FastCDC,PathRules,VersionVector,IgnoreMatcher,FrameCodec,MessageValidator,KeyText";

if (args is not ["--seconds", var duration])
{
    Console.Error.WriteLine("--seconds <duration>: FastCDC, path, vector, ignore, frame, message and key-text fuzz.");
    return 2;
}
int seconds = int.Parse(duration, CultureInfo.InvariantCulture);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
var clock = Stopwatch.StartNew();
int seed = 0;
int[] readSizes = [4093, 65536, 1000000];
while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
{
    var random = new Random(seed);
    byte[] corpus = new byte[random.Next(1, 1024 * 1024)];
    random.NextBytes(corpus);
    try
    {
        var arbitraryFrame = new ReadOnlySequence<byte>(corpus);
        try
        {
            while (true)
            {
                long before = arbitraryFrame.Length;
                if (!FrameCodec.TryRead(ref arbitraryFrame, out _)) break;
                if (arbitraryFrame.Length >= before) throw new InvalidDataException("Framing parser made no progress.");
            }
        }
        catch (ProtocolException) { /* Malformed untrusted input is an expected rejection. */ }
        var encoded = new ArrayBufferWriter<byte>(); FrameCodec.Write(encoded, (byte)WireType.Ping, corpus);
        var validFrame = new ReadOnlySequence<byte>(encoded.WrittenMemory);
        if (!FrameCodec.TryRead(ref validFrame, out var decoded) || !validFrame.IsEmpty
            || decoded.Type != (byte)WireType.Ping || !decoded.Body.FirstSpan.SequenceEqual(corpus))
            throw new InvalidDataException("Framing round trip changed bytes.");
        var arbitraryMessage = new ProtocolFrame((byte)random.Next(1, 13),
            new ReadOnlySequence<byte>(corpus.AsMemory(0, Math.Min(corpus.Length, 2048))));
        try { MessageValidator.Validate(WireMessageCodec.Decode(arbitraryMessage)); }
        catch (ProtocolException) { /* Malformed frame bodies are expected. */ }
        catch (MessageValidationException) { /* Structurally valid but inadmissible messages are expected. */ }
        byte[] helloDevice = new byte[32];
        corpus.AsSpan(0, Math.Min(32, corpus.Length)).CopyTo(helloDevice);
        var validHello = new Hello { ProtocolVersion = 1, DeviceName = "fuzz-peer", ClientVersion = "1",
            DeviceId = ByteString.CopyFrom(helloDevice),
            Nonce = ByteString.CopyFrom(new byte[32]), UtcTicks = DateTime.UtcNow.Ticks };
        (WireType helloType, byte[] helloBody) = WireMessageCodec.Encode(validHello);
        MessageValidator.Validate(WireMessageCodec.Decode(new ProtocolFrame((byte)helloType,
            new ReadOnlySequence<byte>(helloBody))));
        // Mix arbitrary UTF-16 (including unpaired surrogates) with valid paths
        // and hostile mutations, so this is not only an early-rejection target.
        int charCount = random.Next(0, 4100);
        char[] chars = new char[charCount];
        for (int index = 0; index < chars.Length; index++) chars[index] = (char)random.Next(65536);
        string arbitrary = new(chars);
        string valid = $"folder-{seed}/é-{seed}.txt";
        foreach (string candidate in new[] { arbitrary, valid, valid + "/../escape", ".psp/" + valid, valid + ":stream", valid + ".!psp" })
        {
            bool accepted = PathRules.IsValid(candidate);
            if (candidate == valid && !accepted) throw new InvalidDataException("Valid path rejected.");
            if (candidate != valid && candidate != arbitrary && accepted) throw new InvalidDataException("Hostile path accepted.");
            if (accepted && !PathRules.ToLocal(@"C:\PeerSyncFuzz", candidate).StartsWith(@"C:\PeerSyncFuzz\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Path escaped lexical root.");
        }
        var ignores = new IgnoreMatcher("**/*.tmp\n!keep.tmp", includeDefaults: false);
        if (!ignores.IsIgnored($"folder-{seed}/file.tmp") || ignores.IsIgnored("keep.tmp"))
            throw new InvalidDataException("Ignore rule mismatch.");
        IgnoreMatcher? arbitraryRules = null;
        try { arbitraryRules = new IgnoreMatcher(arbitrary[..Math.Min(arbitrary.Length, 128)], includeDefaults: false); }
        catch (ArgumentException) { /* Malformed Unicode is a rejected configuration. */ }
        _ = arbitraryRules?.IsIgnored(valid);
        if (KeyText.TryDecode(arbitrary, out DecodedFolderKey? arbitraryKey))
        {
            using (arbitraryKey)
            {
                if (!KeyText.TryDecode(KeyText.Encode(arbitraryKey.Kind, arbitraryKey.Payload), out var canonical))
                    throw new InvalidDataException("Accepted key failed canonical round trip.");
                canonical.Dispose();
            }
        }
        foreach (FolderKeyKind kind in new[] { FolderKeyKind.ReadWrite, FolderKeyKind.ReadOnly, FolderKeyKind.Encrypted })
        {
            byte[] material = new byte[kind == FolderKeyKind.ReadOnly ? 64 : 32];
            random.NextBytes(material);
            string key = KeyText.Encode(kind, material);
            if (!KeyText.TryDecode(key, out DecodedFolderKey? decodedKey))
                throw new InvalidDataException("Valid key rejected.");
            using (decodedKey)
                if (decodedKey.Kind != kind || !decodedKey.Payload.SequenceEqual(material))
                    throw new InvalidDataException("Folder key round trip changed material.");
            if (KeyText.TryDecode(key[..^1] + (key[^1] == 'A' ? 'B' : 'A'), out var badKey))
            {
                badKey.Dispose();
                throw new InvalidDataException("Invalid folder key checksum accepted.");
            }
        }
        if (VersionVector.TryDeserialize(corpus.AsSpan(0, Math.Min(corpus.Length, 256)), out var vector)
            && !VersionVector.Serialize(vector!).AsSpan().SequenceEqual(corpus.AsSpan(0, Math.Min(corpus.Length, 256))))
            throw new InvalidDataException("Noncanonical version vector accepted.");
        List<int>? baseline = null;
        foreach (int readSize in readSizes)
        {
            var boundaries = new List<int>();
            await using var stream = new MemoryStream(corpus, writable: false);
            int copied = 0;
            long total = await ChunkReader.ReadAsync(stream, 0, chunk =>
            {
                if (!chunk.SequenceEqual(corpus.AsSpan(copied, chunk.Length))) throw new InvalidDataException("Chunk contents changed.");
                copied += chunk.Length;
                boundaries.Add(copied);
            }, readSize);
            if (total != corpus.Length || (baseline is not null && !baseline.SequenceEqual(boundaries))) throw new InvalidDataException("Boundary mismatch.");
            baseline = boundaries;
        }
    }
    catch
    {
        Directory.CreateDirectory("artifacts/fuzz/crashes");
        await File.WriteAllBytesAsync($"artifacts/fuzz/crashes/boundary-path-vector-{seed}.bin", corpus);
        await File.WriteAllTextAsync($"artifacts/fuzz/crashes/seed-{seed}.json", JsonSerializer.Serialize(new { seed, targets = Targets, replay = "System.Random(seed), replay corpus generation before path generation" }));
        throw;
    }
    seed++;
}
Console.WriteLine(JsonSerializer.Serialize(new { target = Targets, seeds = seed, seconds = clock.Elapsed.TotalSeconds, crashes = 0 }));
return 0;
