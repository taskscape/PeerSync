using System.Buffers;
using System.Security;
using System.Text;

namespace PeerSync.Core.Paths;

public static class PathRules
{
    private static readonly SearchValues<char> Forbidden = SearchValues.Create("<>:\"|?*\\");
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    public static bool IsValid(string? recordPath)
    {
        if (recordPath is null || recordPath.Length is 0 or > 4096) return false;
        try { if (!recordPath.IsNormalized(NormalizationForm.FormC)) return false; }
        catch (ArgumentException) { return false; } // Invalid UTF-16 is not a filename.
        string[] parts = recordPath.Split('/');
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (part is "" or "." or ".." || part.Length > 255) return false;
            if (part.AsSpan().ContainsAny(Forbidden)) return false;
            foreach (char value in part) if (value < 32) return false;
            if (part.EndsWith('.') || part.EndsWith(' ')) return false;
            int dot = part.IndexOf('.');
            string stem = (dot < 0 ? part : part[..dot]).TrimEnd(' ');
            if (Reserved.Contains(stem)) return false;
            if (index == 0 && part.Equals(".psp", StringComparison.OrdinalIgnoreCase)) return false;
            if (part.EndsWith(".!psp", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Lexical containment only. Filesystem callers must also prohibit reparse traversal.</summary>
    public static string ToLocal(string folderRoot, string recordPath)
    {
        if (!IsValid(recordPath)) throw new SecurityException("Invalid record path.");
        ArgumentException.ThrowIfNullOrWhiteSpace(folderRoot);
        if (!Path.IsPathFullyQualified(folderRoot)) throw new ArgumentException("Folder root must be absolute.", nameof(folderRoot));
        string root = Path.GetFullPath(folderRoot);
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, recordPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new SecurityException("Record path escapes folder root.");
        return full;
    }

    public static string Key(string recordPath)
    {
        if (!IsValid(recordPath)) throw new ArgumentException("Invalid record path.", nameof(recordPath));
        return recordPath.ToUpperInvariant();
    }
}
