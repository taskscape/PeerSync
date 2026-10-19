using System.Security;
using System.Text;
using CsCheck;
using PeerSync.Core.Paths;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed class PathRulesTests
{
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/root")]
    [InlineData("file/")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("C:/file")]
    [InlineData("C:file")]
    [InlineData("\\\\host\\share\\file")]
    [InlineData("\\\\?\\C:\\file")]
    [InlineData("a\\b")]
    [InlineData("file:stream")]
    [InlineData("a<")]
    [InlineData("a>")]
    [InlineData("a\"")]
    [InlineData("a|")]
    [InlineData("a?")]
    [InlineData("a*")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("dir /file")]
    [InlineData(".psp/index.db")]
    [InlineData(".PSP")]
    [InlineData("a.!psp")]
    [InlineData("dir.!PSP/file")]
    public void HostilePathsAreRejectedBeforeLocalMapping(string path)
    {
        PathRules.IsValid(path).ShouldBeFalse();
        Should.Throw<SecurityException>(() => PathRules.ToLocal(@"C:\Sync", path));
        Should.Throw<ArgumentException>(() => PathRules.Key(path));
    }

    [Fact]
    public void AllReservedDevicesAndControlsAreRejectedInEveryComponent()
    {
        string[] devices = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            .. Enumerable.Range(1, 9).Select(i => $"COM{i}"),
            .. Enumerable.Range(1, 9).Select(i => $"LPT{i}"),
            "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"];
        foreach (string device in devices)
        foreach (string name in new[] { device, device.ToLowerInvariant() + ".txt", device + " .tar.gz" })
        {
            PathRules.IsValid(name).ShouldBeFalse();
            PathRules.IsValid($"safe/{name}/file").ShouldBeFalse();
        }
        for (int value = 0; value < 32; value++)
            PathRules.IsValid($"safe/a{(char)value}b").ShouldBeFalse();
    }

    [Theory]
    [InlineData("hello.txt")]
    [InlineData("Żółć/東京/😀.txt")]
    [InlineData("café/é.txt")]
    [InlineData("file with spaces.txt")]
    [InlineData(".gitignore")]
    [InlineData("notcon.txt")]
    [InlineData("COM10.txt")]
    [InlineData("folder/.psp/file")]
    [InlineData("file.!psp.txt")]
    public void ValidUnicodeAndOrdinaryNamesPassWithoutChangingThePath(string path)
    {
        PathRules.IsValid(path).ShouldBeTrue();
        PathRules.ToLocal(@"C:\Sync", path).ShouldBe(@"C:\Sync\" + path.Replace('/', '\\'));
        PathRules.Key(path).ShouldBe(path.ToUpperInvariant());
    }

    [Fact]
    public void UnicodeMustBeWellFormedAndNfcAndLengthsAreBounded()
    {
        PathRules.IsValid(null).ShouldBeFalse();
        foreach (string path in new[] { "a\uD800", "\uDC00b", "é".Normalize(NormalizationForm.FormD) })
            PathRules.IsValid(path).ShouldBeFalse();
        PathRules.IsValid(new string('a', 255)).ShouldBeTrue();
        PathRules.IsValid(new string('a', 256)).ShouldBeFalse();
        string longest = string.Join('/', Enumerable.Repeat(new string('a', 240), 17));
        longest.Length.ShouldBe(4096);
        PathRules.IsValid(longest).ShouldBeTrue();
        PathRules.IsValid(longest + "b").ShouldBeFalse();
    }

    [Fact]
    public void MappingHandlesDriveRootsUncAndFolderBoundaryAndRequiresAnAbsoluteRoot()
    {
        PathRules.ToLocal(@"C:\", "a/b").ShouldBe(@"C:\a\b");
        PathRules.ToLocal(@"C:\Sync\", "a").ShouldBe(@"C:\Sync\a");
        PathRules.ToLocal(@"\\host\share\", "a/b").ShouldBe(@"\\host\share\a\b");
        Should.Throw<ArgumentException>(() => PathRules.ToLocal("relative", "file"));
        Should.Throw<SecurityException>(() => PathRules.ToLocal(@"C:\Sync", "../Sync-other/file"));
    }

    [Fact]
    public void ArbitraryUtf16CannotThrowOrEscapeTheLexicalRoot()
    {
        Gen.UShort.Array[0, 300].Sample(values =>
        {
            string path = new(values.Select(v => (char)v).ToArray());
            if (PathRules.IsValid(path))
                PathRules.ToLocal(@"C:\Sync", path).ShouldStartWith(@"C:\Sync\");
        }, iter: 10000);
    }
}
