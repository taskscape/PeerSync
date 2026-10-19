using System.Globalization;
using PeerSync.Core.Paths;

namespace PeerSync.Engine.Sync;

public static class ConflictNames
{
    /// <summary>Candidate only. The applier must reserve without overwriting and increment on collision.</summary>
    public static string CopyPath(string originalPath, DateTimeOffset at, ReadOnlySpan<byte> modifyingDevice, int collision = 1)
    {
        if (modifyingDevice.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(modifyingDevice));
        string suffix = ".sync-conflict-" + at.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + Convert.ToHexString(modifyingDevice[..2]);
        return WithSuffix(originalPath, suffix, collision);
    }

    public static string CasePath(string originalPath, int collision = 1) => WithSuffix(originalPath, ".case-conflict", collision);

    public static string CaseSourcePath(string originalPath, ReadOnlySpan<byte> sourceId, int collision = 1)
    {
        if (sourceId.Length != 32) throw new ArgumentException("Source identifier must contain 32 bytes.", nameof(sourceId));
        return WithSuffix(originalPath, ".case-conflict-" + Convert.ToHexString(sourceId), collision);
    }

    private static string WithSuffix(string path, string suffix, int collision)
    {
        if (!PathRules.IsValid(path)) throw new ArgumentException("Invalid conflict path.", nameof(path));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(collision);
        if (collision > 1) suffix += "-" + collision.ToString(CultureInfo.InvariantCulture);
        int slash = path.LastIndexOf('/');
        string parent = slash < 0 ? "" : path[..(slash + 1)];
        string name = path[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        string stem = dot <= 0 ? name : name[..dot], extension = dot <= 0 ? "" : name[dot..];
        int budget = Math.Min(255, 4096 - parent.Length);
        if (budget < suffix.Length + 1) throw new PathTooLongException("No syncable sibling conflict name fits this directory.");
        extension = Prefix(extension, budget - suffix.Length - 1).TrimEnd(' ', '.');
        stem = Prefix(stem, budget - suffix.Length - extension.Length).TrimEnd(' ', '.');
        if (stem.Length == 0) stem = "f";
        string result = parent + stem + suffix + extension;
        if (!PathRules.IsValid(result)) result = parent + "f" + suffix + extension;
        if (!PathRules.IsValid(result)) throw new PathTooLongException("Conflict name cannot be represented safely.");
        return result;
    }
    private static string Prefix(string value, int length)
    {
        if (value.Length <= length) return value;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
}
