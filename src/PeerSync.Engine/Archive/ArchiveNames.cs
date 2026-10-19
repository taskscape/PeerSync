using System.Globalization;
using PeerSync.Core.Paths;

namespace PeerSync.Engine.Archive;

internal static class ArchiveNames
{
    // A candidate only: callers durably record and move without overwriting.
    internal static string Candidate(string original, DateTimeOffset at, int collision = 1)
    {
        if (!PathRules.IsValid(original)) throw new ArgumentException("Invalid original archive path.", nameof(original));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(collision);
        int slash = original.LastIndexOf('/');
        string parent = ".psp/archive" + (slash < 0 ? "" : "/" + original[..slash]);
        string name = original[(slash + 1)..]; int dot = name.LastIndexOf('.');
        string stem = dot <= 0 ? name : name[..dot], extension = dot <= 0 ? "" : name[dot..];
        string suffix = "~" + at.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + (collision == 1 ? "" : "-" + collision.ToString(CultureInfo.InvariantCulture));
        int budget = Math.Min(255, 4109 - parent.Length - 1);
        if (budget <= suffix.Length) throw new PathTooLongException("No archive slot fits this directory.");
        extension = Prefix(extension, budget - suffix.Length - 1).TrimEnd(' ', '.');
        stem = Prefix(stem, budget - suffix.Length - extension.Length).TrimEnd(' ', '.');
        if (stem.Length == 0) stem = "f";
        string path = parent + "/" + stem + suffix + extension;
        if (!PathRules.IsValid(path[13..])) throw new PathTooLongException("Archive path cannot be represented.");
        return path;
    }

    private static string Prefix(string value, int count)
    {
        if (value.Length <= count) return value;
        if (count > 0 && char.IsHighSurrogate(value[count - 1])) count--;
        return value[..count];
    }
}
