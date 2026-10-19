using CsCheck;
using PeerSync.Core.Ignore;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed class IgnoreMatcherTests
{
    [Theory]
    [InlineData("*.pdf", "x/FILE.PDF", false, true)]
    [InlineData("abc?.txt", "ABC1.txt", false, true)]
    [InlineData("abc?.txt", "abc12.txt", false, false)]
    [InlineData("My Test", "x/My Test/file", false, true)]
    [InlineData("ABC\\CDE F", "ABC/CDE F", true, true)]
    [InlineData("ABC\\CDE F", "123/ABC/CDE F", true, false)]
    [InlineData("FOO\\*", "FOO/file", false, true)]
    [InlineData("FOO\\*", "FOO/QWER/file", false, true)]
    [InlineData("FOO\\*", "other/FOO/file", false, false)]
    [InlineData("\\FOO", "FOO/file", false, true)]
    [InlineData("\\FOO", "other/FOO/file", false, false)]
    [InlineData("?recycle", "#recycle", true, true)]
    [InlineData("a/**/b", "a/b", true, true)]
    [InlineData("a/**/b", "a/x/b/file", false, true)]
    [InlineData("a/**/b", "a/x/y/b", true, true)]
    public void ResilioExamplesMatchTheSpecifiedWindowsGlobSubset(string pattern, string path, bool directory, bool expected) =>
        new IgnoreMatcher(pattern, includeDefaults: false).IsIgnored(path, directory).ShouldBe(expected);

    [Theory]
    [InlineData("/foo", "foo/bar", false, true)]
    [InlineData("/foo", "a/foo", true, false)]
    [InlineData("foo/", "foo", false, false)]
    [InlineData("foo/", "a/foo", true, true)]
    [InlineData("foo/", "a/foo/bar", false, true)]
    [InlineData("**/foo", "x/y/foo", false, true)]
    [InlineData("a/**", "a", true, false)]
    [InlineData("a/**", "a/file", false, true)]
    [InlineData("a/**", "a/x/y/file", false, true)]
    [InlineData("a/*/b", "a/b", false, false)]
    [InlineData("a/*/b", "a/x/b", false, true)]
    [InlineData("a/*/b", "a/x/y/b", false, false)]
    [InlineData("file?.txt", "file😀.txt", false, true)]
    [InlineData("café", "CAFÉ/file", false, true)]
    [InlineData("\uD801\uDC28.txt", "\uD801\uDC00.txt", false, true)]
    [InlineData("# ignored\n*.pdf", "ignored", false, false)]
    [InlineData("\\#literal", "x/#literal", false, true)]
    [InlineData("\\!literal", "!literal", false, true)]
    [InlineData("*.tmp\n!keep.tmp", "keep.tmp", false, false)]
    [InlineData("cache/\n!cache/file", "cache/file", false, true)]
    [InlineData("cache/\n!cache/\ncache/*\n!cache/keep", "cache/keep", false, false)]
    [InlineData("a/**/x/**/b", "a/x/b", false, true)]
    [InlineData("a/**/x/**/b", "a/m/x/n/o/b", false, true)]
    public void AnchorsDirectoriesUnicodeCommentsAndNegationHaveStableSemantics(string pattern, string path, bool directory, bool expected) =>
        new IgnoreMatcher(pattern, includeDefaults: false).IsIgnored(path, directory).ShouldBe(expected);

    [Fact]
    public void DefaultsAndInternalPathsAreExcludedAndCanBeConfigured()
    {
        var defaults = new IgnoreMatcher();
        foreach (string path in new[] { "Thumbs.db", "nested/DESKTOP.INI", "~$book.xlsx", "a/file.tmp", "$RECYCLE.BIN/file", "System Volume Information/file", ".psp/ignore", "a.!psp", "../escape" })
            defaults.IsIgnored(path).ShouldBeTrue();
        defaults.IsIgnored("ordinary.txt").ShouldBeFalse();
        new IgnoreMatcher("!*.tmp").IsIgnored("file.tmp").ShouldBeFalse();
        new IgnoreMatcher(includeDefaults: false).IsIgnored("file.tmp").ShouldBeFalse();
        new IgnoreMatcher("!.psp\n!*\n!*.!psp").IsIgnored(".psp/id").ShouldBeTrue();
        new IgnoreMatcher("!.psp\n!*\n!*.!psp").IsIgnored("file.!psp").ShouldBeTrue();
        new IgnoreMatcher("\uFEFF#comment\r\nMy Test\r\n").IsIgnored("My Test/file").ShouldBeTrue();
    }

    [Fact]
    public void ParserLimitsAndInvalidUnicodeFailWithRedactedErrors()
    {
        Should.Throw<ArgumentException>(() => new IgnoreMatcher(new string('a', IgnoreMatcher.MaxRuleTextLength + 1)));
        Should.Throw<ArgumentException>(() => new IgnoreMatcher(new string('a', IgnoreMatcher.MaxPatternLength + 1)));
        Should.Throw<ArgumentException>(() => new IgnoreMatcher(string.Join('\n', Enumerable.Repeat("a", IgnoreMatcher.MaxRules + 1))));
        Should.Throw<ArgumentException>(() => new IgnoreMatcher("private\uD800"))
            .Message.ShouldNotContain("private");
        var adversarial = new IgnoreMatcher(string.Concat(Enumerable.Repeat("*a", 500)) + "b", includeDefaults: false);
        adversarial.IsIgnored(new string('a', 255)).ShouldBeFalse();
    }

    [Fact]
    public void LiteralAndRecursiveRulesMatchGeneratedTreesConsistently()
    {
        Gen.Int[0, 100000].Sample(id =>
        {
            string component = $"folder{id}";
            var literal = new IgnoreMatcher(component, includeDefaults: false);
            literal.IsIgnored($"parent/{component}/file").ShouldBeTrue();
            literal.IsIgnored($"parent/{component}-other/file").ShouldBeFalse();
            var recursive = new IgnoreMatcher("a/**/b", includeDefaults: false);
            recursive.IsIgnored("a/" + string.Concat(Enumerable.Repeat(component + "/", id % 8)) + "b/file").ShouldBeTrue();
        }, iter: 10000);
    }
}
