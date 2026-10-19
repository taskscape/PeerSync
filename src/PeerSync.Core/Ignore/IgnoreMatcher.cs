using System.Text;
using PeerSync.Core.Paths;

namespace PeerSync.Core.Ignore;

/// <summary>Immutable, local gitignore-style glob subset. No filesystem access or regex backtracking.</summary>
public sealed class IgnoreMatcher
{
    public const int MaxRuleTextLength = 65536;
    public const int MaxRules = 1024;
    public const int MaxPatternLength = 4096;
    private static readonly string[] Defaults =
        ["Thumbs.db", "desktop.ini", "~$*", "*.tmp", "$RECYCLE.BIN", "System Volume Information"];
    public static IReadOnlyList<string> DefaultPatterns { get; } = Array.AsReadOnly(Defaults);
    private readonly Rule[] rules;

    public IgnoreMatcher(string ruleText = "", bool includeDefaults = true)
    {
        ArgumentNullException.ThrowIfNull(ruleText);
        if (ruleText.Length > MaxRuleTextLength) throw new ArgumentException("Ignore rules exceed text limit.", nameof(ruleText));
        var parsed = new List<Rule>();
        if (includeDefaults) foreach (string pattern in Defaults) Add(pattern, parsed);
        using var reader = new StringReader(ruleText.TrimStart('\uFEFF'));
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > MaxPatternLength) throw new ArgumentException("Ignore pattern exceeds length limit.", nameof(ruleText));
            Add(line, parsed);
            if (parsed.Count > MaxRules) throw new ArgumentException("Too many ignore rules.", nameof(ruleText));
        }
        rules = parsed.ToArray();
    }

    public bool IsIgnored(string recordPath, bool isDirectory = false)
    {
        // Includes internal metadata/temp paths, even if a negation attempts to expose them.
        if (!PathRules.IsValid(recordPath)) return true;
        Rune[][] components = recordPath.ToUpperInvariant().Split('/')
            .Select(part => part.EnumerateRunes().ToArray()).ToArray();
        bool[] ignored = new bool[components.Length];
        foreach (var rule in rules)
        {
            bool[] matches = MatchPrefixes(rule, components);
            for (int i = 0; i < components.Length; i++)
                if (matches[i + 1] && (!rule.DirectoryOnly || i != components.Length - 1 || isDirectory))
                    ignored[i] = !rule.Negated;
        }
        // An excluded ancestor is pruned; a child negation cannot undo that exclusion.
        return ignored.Any(value => value);
    }

    private static void Add(string line, List<Rule> output)
    {
        line = line.TrimEnd(' ', '\t');
        if (line.Length == 0 || line[0] == '#') return;
        bool literalInitial = line.StartsWith("\\#", StringComparison.Ordinal) || line.StartsWith("\\!", StringComparison.Ordinal);
        if (literalInitial) line = line[1..];
        bool negated = !literalInitial && line.StartsWith('!');
        if (negated) line = line[1..];
        line = line.Replace('\\', '/');
        bool anchored = line.StartsWith('/');
        bool directoryOnly = line.EndsWith('/');
        if (anchored) line = line[1..];
        if (directoryOnly && line.Length != 0) line = line[..^1];
        if (line.Length == 0) return;
        anchored |= line.Contains('/');
        try { line = line.Normalize(NormalizationForm.FormC).ToUpperInvariant(); }
        catch (ArgumentException) { throw new ArgumentException("Ignore pattern contains invalid Unicode.", nameof(line)); }
        var segments = line.Split('/').Select(part => new Segment(part == "**", part.EnumerateRunes().ToArray())).ToArray();
        output.Add(new(segments, anchored, directoryOnly, negated));
    }

    private static bool[] MatchPrefixes(Rule rule, Rune[][] path)
    {
        bool[] current = new bool[path.Length + 1];
        if (!rule.Anchored)
        {
            for (int i = 0; i < path.Length; i++) current[i + 1] = MatchComponent(rule.Segments[0].Values, path[i]);
            return current;
        }
        current[0] = true;
        bool[] next = new bool[path.Length + 1];
        for (int segmentIndex = 0; segmentIndex < rule.Segments.Length; segmentIndex++)
        {
            Array.Clear(next);
            var segment = rule.Segments[segmentIndex];
            if (segment.DoubleStar)
            {
                bool trailing = segmentIndex == rule.Segments.Length - 1 && segmentIndex != 0;
                next[0] = !trailing && current[0];
                for (int i = 1; i <= path.Length; i++)
                    next[i] = next[i - 1] || (trailing ? current[i - 1] : current[i]);
            }
            else
                for (int i = 1; i <= path.Length; i++)
                    next[i] = current[i - 1] && MatchComponent(segment.Values, path[i - 1]);
            (current, next) = (next, current);
        }
        return current;
    }

    private static bool MatchComponent(Rune[] pattern, Rune[] value)
    {
        int p = 0, v = 0, star = -1, retry = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p].Value == '?' || pattern[p] == value[v])) { p++; v++; }
            else if (p < pattern.Length && pattern[p].Value == '*') { star = p++; retry = v; }
            else if (star >= 0) { p = star + 1; v = ++retry; }
            else return false;
        }
        while (p < pattern.Length && pattern[p].Value == '*') p++;
        return p == pattern.Length;
    }

    private sealed record Segment(bool DoubleStar, Rune[] Values);
    private sealed record Rule(Segment[] Segments, bool Anchored, bool DirectoryOnly, bool Negated);
}
