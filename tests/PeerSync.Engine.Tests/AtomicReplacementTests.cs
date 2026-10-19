using System.ComponentModel;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class AtomicReplacementTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PeerSync-AtomicReplace-" + Guid.NewGuid().ToString("N"));
    public AtomicReplacementTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuardedPosixReplacementEitherPreservesBothVersionsOrFailsWithoutChangingThem(bool readOnly)
    {
        string path = Path.Combine(root, "target"); File.WriteAllText(path, "original bytes");
        if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
        bool supported;
        using (var tree = new SafeFileTree(root))
        using (var parent = tree.Directory(""))
        using (var archive = tree.Directory(".psp/archive", create: true, privatePath: true))
        using (var original = parent.OpenFile("target", mutation: true))
        using (var temporary = parent.CreateFile("new.!psp", temporary: true))
        {
            RandomAccess.Write(temporary, "replacement bytes"u8, 0); RandomAccess.FlushToDisk(temporary);
            var oldIdentity = FileSnapshot.Read(original); var newIdentity = FileSnapshot.Read(temporary);
            archive.LinkHere(original, "preserved");
            if (readOnly) Should.Throw<UnauthorizedAccessException>(() => File.OpenWrite(path));
            else Should.Throw<IOException>(() => File.OpenWrite(path));
            Should.Throw<IOException>(() => File.Delete(path));
            try { parent.ReplaceHere(temporary, original, "target"); supported = true; }
            catch (Win32Exception error) when (error.NativeErrorCode == 32) { supported = false; }
            var oldAfter = FileSnapshot.Read(original); oldAfter.IdLow.ShouldBe(oldIdentity.IdLow); oldAfter.IdHigh.ShouldBe(oldIdentity.IdHigh);
            using var unchanged = parent.OpenFile("target");
            var actual = FileSnapshot.Read(unchanged); var expected = supported ? newIdentity : oldIdentity;
            actual.IdLow.ShouldBe(expected.IdLow); actual.IdHigh.ShouldBe(expected.IdHigh);
            byte[] old = new byte[14]; RandomAccess.Read(original, old, 0).ShouldBe(14); old.ShouldBe("original bytes"u8.ToArray());
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"guardedAtomicReplacementSupported={supported};readOnly={readOnly}");
        File.ReadAllText(path).ShouldBe(supported ? "replacement bytes" : "original bytes");
        File.ReadAllText(Path.Combine(root, ".psp", "archive", "preserved")).ShouldBe("original bytes");
        if (supported) File.Exists(Path.Combine(root, "new.!psp")).ShouldBeFalse();
        else File.ReadAllText(Path.Combine(root, "new.!psp")).ShouldBe("replacement bytes");
    }

    [Fact]
    public void WrongTargetGuardAndOccupiedArchiveNamesCannotClobberEitherFile()
    {
        File.WriteAllText(Path.Combine(root, "target"), "target"); File.WriteAllText(Path.Combine(root, "other"), "other");
        using (var tree = new SafeFileTree(root))
        using (var parent = tree.Directory(""))
        using (var target = parent.OpenFile("target", mutation: true))
        using (var wrong = parent.OpenFile("other", mutation: true))
        using (var temporary = parent.CreateFile("new.!psp", temporary: true))
        {
            Should.Throw<IOException>(() => parent.ReplaceHere(temporary, wrong, "target"));
            Should.Throw<Win32Exception>(() => parent.LinkHere(target, "other"));
        }
        File.ReadAllText(Path.Combine(root, "target")).ShouldBe("target"); File.ReadAllText(Path.Combine(root, "other")).ShouldBe("other");
    }

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
