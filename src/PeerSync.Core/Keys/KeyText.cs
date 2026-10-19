using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace PeerSync.Core.Keys;

/// <summary>Canonical RFC 4648 Base32 folder keys with a two-symbol CRC-10/ATM typo check.</summary>
public static class KeyText
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int MaxEncodedLength = 106;
    private const int MaxInputLength = 4096;

    public static string Encode(FolderKeyKind kind, ReadOnlySpan<byte> payload)
    {
        int length = PayloadLength(kind);
        if (payload.Length != length) throw new ArgumentException("Invalid folder-key payload length.", nameof(payload));
        char prefix = Prefix(kind);
        Span<char> encoded = stackalloc char[MaxEncodedLength];
        try
        {
            encoded[0] = prefix;
            uint buffer = 0;
            int bits = 0, position = 1;
            foreach (byte value in payload)
            {
                buffer = (buffer << 8) | value;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    encoded[position++] = Alphabet[(int)(buffer >> bits) & 31];
                }
            }
            if (bits != 0) encoded[position++] = Alphabet[(int)(buffer << (5 - bits)) & 31];
            int check = Checksum(prefix, payload);
            encoded[position++] = Alphabet[check >> 5];
            encoded[position++] = Alphabet[check & 31];
            return new string(encoded[..position]);
        }
        finally { encoded.Clear(); }
    }

    public static DecodedFolderKey Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryDecode(text.AsSpan(), out var result)) throw new FormatException("Invalid folder key.");
        return result;
    }

    public static bool TryDecode(string? text, [NotNullWhen(true)] out DecodedFolderKey? result) =>
        TryDecode(text.AsSpan(), out result);

    public static bool TryDecode(ReadOnlySpan<char> text, [NotNullWhen(true)] out DecodedFolderKey? result)
    {
        result = null;
        if (text.Length is 0 or > MaxInputLength) return false;
        Span<char> canonical = stackalloc char[MaxEncodedLength];
        byte[]? payload = null;
        try
        {
            int count = 0;
            foreach (char value in text)
            {
                if (value == '-' || char.IsWhiteSpace(value)) continue;
                if (count == canonical.Length) return false;
                canonical[count++] = value is >= 'a' and <= 'z' ? (char)(value - 'a' + 'A') : value;
            }
            if (count == 0 || canonical[0] is not ('A' or 'B' or 'F')) return false;
            FolderKeyKind kind = canonical[0] switch
            {
                'A' => FolderKeyKind.ReadWrite,
                'B' => FolderKeyKind.ReadOnly,
                _ => FolderKeyKind.Encrypted,
            };
            int length = PayloadLength(kind);
            int symbols = (length * 8 + 4) / 5;
            if (count != symbols + 3) return false;
            payload = new byte[length];
            uint buffer = 0;
            int bits = 0, output = 0;
            for (int position = 1; position <= symbols; position++)
            {
                int value = Alphabet.IndexOf(canonical[position]);
                if (value < 0) return false;
                buffer = (buffer << 5) | (uint)value;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    payload[output++] = (byte)(buffer >> bits);
                }
            }
            // Different encodings of the same last byte must not bypass the typo check.
            if (output != length || (buffer & ((1u << bits) - 1)) != 0) return false;
            int high = Alphabet.IndexOf(canonical[count - 2]);
            int low = Alphabet.IndexOf(canonical[count - 1]);
            if (high < 0 || low < 0 || ((high << 5) | low) != Checksum(canonical[0], payload)) return false;
            result = new DecodedFolderKey(kind, payload);
            payload = null; // Ownership transferred; the decoded object's Dispose clears it.
            return true;
        }
        finally
        {
            canonical.Clear();
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static int PayloadLength(FolderKeyKind kind) => kind switch
    {
        FolderKeyKind.ReadWrite or FolderKeyKind.Encrypted => 32,
        FolderKeyKind.ReadOnly => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static char Prefix(FolderKeyKind kind) => kind switch
    {
        FolderKeyKind.ReadWrite => 'A',
        FolderKeyKind.ReadOnly => 'B',
        FolderKeyKind.Encrypted => 'F',
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static int Checksum(char prefix, ReadOnlySpan<byte> payload)
    {
        int crc = Append(0, (byte)prefix);
        foreach (byte value in payload) crc = Append(crc, value);
        return crc;

        static int Append(int crc, byte value)
        {
            crc ^= value << 2;
            for (int bit = 0; bit < 8; bit++) crc = ((crc << 1) ^ ((crc & 0x200) == 0 ? 0 : 0x233)) & 0x3ff;
            return crc;
        }
    }
}
