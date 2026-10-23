using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PeerSync.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed partial class DiagnosticRunRetentionTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-LogRuns-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Path, bool Directory)> links = [];
    private string Root => Path.Combine(fixture, "logs");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void AppLogRootOpensBeneathOrdinaryLocalAppData()
    {
        string local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string root = Path.Combine(local, "PeerSync-LogProfileTest-" + Guid.NewGuid().ToString("N"));
        string resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(local.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-LogProfileTest-", StringComparison.Ordinal))
            throw new IOException("Unexpected profile-log cleanup path.");
        try
        {
            using var run = DiagnosticRunJournal.Open(Path.Combine(root, "logs", "app"), "app", debugEnabled: true);
            run.Journal.Write(new("app.started", "test"));
            File.ReadAllText(Path.Combine(run.DirectoryPath, "debug.jsonl")).ShouldContain("app.started");
        }
        finally
        {
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
    [Fact]
    public void FinishedSequentialRunsRespectTheBudgetWithoutAnotherProcessOrManualSweep()
    {
        for (int i = 0; i < 12; i++) { using var run = Open(); run.Journal.Write(new("run.complete", "test")); }
        Directory.GetDirectories(Root).Length.ShouldBe(8);
    }
    [Fact]
    public void RepeatedRunsHaveASharedCountBoundAndActiveWritersKeepWorking()
    {
        using var active = Open(maximumRuns: 2);
        for (int i = 0; i < 12; i++) { using var run = Open(maximumRuns: 2); run.Journal.Write(new("run.complete", "test")); }
        var result = active.SweepInactiveRuns(); result.RetainedRuns.ShouldBe(2); result.Pressure.ShouldBeFalse();
        Directory.GetDirectories(Root).Length.ShouldBe(3); active.Journal.Write(new("still.active", "test"));
        File.ReadAllText(Path.Combine(active.DirectoryPath, "debug.jsonl")).ShouldContain("still.active");
        Should.Throw<IOException>(() => Directory.Move(active.DirectoryPath, active.DirectoryPath + "-moved"));
        Should.Throw<IOException>(() => Directory.Move(Root, Root + "-moved"));
    }
    [Fact]
    public void SharedByteBudgetAccountsForEveryRetainedInactiveLog()
    {
        using var active = Open(maximumRuns: 20, maximumBytes: 6000);
        for (int i = 0; i < 6; i++) { using var run = Open(maximumRuns: 20, maximumBytes: 6000); for (int row = 0; row < 8; row++) run.Journal.Write(new("run.progress", "test") { Bytes = row }); }
        var result = active.SweepInactiveRuns(); result.RetainedRuns.ShouldBeGreaterThan(0); result.RetainedBytes.ShouldBeLessThanOrEqualTo(6000); result.Pressure.ShouldBeFalse();
        long measured = Directory.GetDirectories(Root).Where(path => path != active.DirectoryPath).Sum(path => Directory.GetFiles(path, "debug*.jsonl").Sum(file => new FileInfo(file).Length));
        measured.ShouldBe(result.RetainedBytes);
    }
    [Fact]
    public void PeriodicExpiryRemovesOldInactiveRunsWithoutTouchingTheActiveOne()
    {
        var clock = new Clock(); using var active = Open(clock: clock); string old;
        using (var run = Open(clock: clock)) { old = run.DirectoryPath; run.Journal.Write(new("old.run", "test")); }
        clock.Advance(TimeSpan.FromHours(25)); clock.Fire(); Directory.Exists(old).ShouldBeFalse(); Directory.Exists(active.DirectoryPath).ShouldBeTrue();
        active.Journal.Write(new("active.run", "test")); File.ReadAllText(Path.Combine(active.DirectoryPath, "debug.jsonl")).ShouldContain("active.run");
    }
    [Fact]
    public void UnknownUnmarkedAndUnexpectedContentsArePreserved()
    {
        string legacy = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(legacy); File.WriteAllText(Path.Combine(legacy, "debug.jsonl"), "unmarked bytes");
        string owned;
        using (var run = Open()) { owned = run.DirectoryPath; run.Journal.Write(new("owned.run", "test")); }
        File.WriteAllText(Path.Combine(owned, "user.txt"), "unknown user bytes");
        using var active = Open(maximumRuns: 0); var result = active.SweepInactiveRuns();
        File.ReadAllText(Path.Combine(legacy, "debug.jsonl")).ShouldBe("unmarked bytes"); File.ReadAllText(Path.Combine(owned, "user.txt")).ShouldBe("unknown user bytes");
        File.Exists(Path.Combine(owned, "debug.jsonl")).ShouldBeTrue(); result.ProtectedRuns.ShouldBeGreaterThanOrEqualTo(3);
    }
    [Fact]
    public void ALockedInactiveFileIsDeferredAndRetiresAfterTheLockIsReleased()
    {
        string old; using (var run = Open()) { old = run.DirectoryPath; run.Journal.Write(new("locked.run", "test")); }
        using var active = Open();
        using (var locked = new FileStream(Path.Combine(old, "debug.jsonl"), FileMode.Open, FileAccess.Read, FileShare.Read))
        { using var sweeper = Open(maximumRuns: 0); sweeper.SweepInactiveRuns().ProtectedRuns.ShouldBeGreaterThanOrEqualTo(3); File.Exists(Path.Combine(old, "debug.jsonl")).ShouldBeTrue(); }
        using var retry = Open(maximumRuns: 0); retry.SweepInactiveRuns(); Directory.Exists(old).ShouldBeFalse();
        active.Journal.Write(new("still.active", "test"));
    }
    [Fact]
    public void ReparseRunAndLogNamesAndLinkedRootsNeverRedirectDeletion()
    {
        string outside = Path.Combine(fixture, "outside"); Directory.CreateDirectory(outside); string user = Path.Combine(outside, "user.txt"); File.WriteAllText(user, "outside bytes");
        Directory.CreateDirectory(Root); string linkedRun = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateSymbolicLink(linkedRun, outside); links.Add((linkedRun, true));
        string owned; using (var run = Open()) { owned = run.DirectoryPath; run.Journal.Write(new("owned.run", "test")); }
        string log = Path.Combine(owned, "debug.jsonl"); File.Delete(log); File.CreateSymbolicLink(log, user); links.Add((log, false));
        using var active = Open(maximumRuns: 0); active.SweepInactiveRuns(); File.ReadAllText(user).ShouldBe("outside bytes"); Directory.Exists(linkedRun).ShouldBeTrue(); File.Exists(log).ShouldBeTrue();
        string rootLink = Path.Combine(fixture, "root-link"); Directory.CreateSymbolicLink(rootLink, outside); links.Add((rootLink, true));
        Exception? rejected = null; try { using var invalid = DiagnosticRunJournal.Open(rootLink, "test"); } catch (Exception error) { rejected = error; }
        (rejected is IOException or Win32Exception).ShouldBeTrue(); Directory.GetDirectories(outside).ShouldBeEmpty(); File.ReadAllText(user).ShouldBe("outside bytes");
    }
    [Fact]
    public void HardlinkedLogEntriesAreNotReclassifiedAsOwnedDeletions()
    {
        string old; using (var run = Open()) { old = run.DirectoryPath; run.Journal.Write(new("owned.run", "test")); }
        string log = Path.Combine(old, "debug.jsonl"), other = Path.Combine(fixture, "outside.log");
        if (!CreateHardLink(other, log, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError()); string bytes = File.ReadAllText(other);
        using var active = Open(maximumRuns: 0); active.SweepInactiveRuns(); File.ReadAllText(log).ShouldBe(bytes); File.ReadAllText(other).ShouldBe(bytes);
    }
    [Fact]
    public async Task ActiveWriterLeaseSurvivesAnotherProcessSweep()
    {
        const string rootVariable = "PEERSYNC_TEST_LOG_RUN_ROOT";
        if (Environment.GetEnvironmentVariable(rootVariable) is { Length: > 0 } childRoot)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(childRoot))!;
            if (Path.GetDirectoryName(parent) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(parent).StartsWith("PeerSync-LogRuns-", StringComparison.Ordinal)) throw new IOException("Unexpected child fixture.");
            using var run = DiagnosticRunJournal.Open(childRoot, "test"); File.WriteAllText(Path.Combine(parent, "child.ready"), run.DirectoryPath);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(Path.Combine(parent, "child.release"))) await Task.Delay(10, timeout.Token);
            run.Journal.Write(new("child.still_active", "test")); return;
        }
        using var active = Open(maximumRuns: 0); string repository = Repository();
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "test", "--project", Path.Combine(repository, "tests/PeerSync.Core.Tests/PeerSync.Core.Tests.csproj"), "-c", "Release", "--no-build", "--filter-method", "*ActiveWriterLeaseSurvivesAnotherProcessSweep*", "--report-trx", "--results-directory", Path.Combine(fixture, "child-results") }) info.ArgumentList.Add(argument);
        info.Environment[rootVariable] = Root; using var child = Process.Start(info)!; var stdout = child.StandardOutput.ReadToEndAsync(Token); var stderr = child.StandardError.ReadToEndAsync(Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            string ready = Path.Combine(fixture, "child.ready"); while (!File.Exists(ready)) { child.HasExited.ShouldBeFalse(); await Task.Delay(10, timeout.Token); }
            string childDirectory = File.ReadAllText(ready); active.SweepInactiveRuns(); Directory.Exists(childDirectory).ShouldBeTrue();
            File.WriteAllText(Path.Combine(fixture, "child.release"), "release"); await child.WaitForExitAsync(timeout.Token); child.ExitCode.ShouldBe(0);
            File.ReadAllText(Path.Combine(childDirectory, "debug.jsonl")).ShouldContain("child.still_active");
            active.SweepInactiveRuns(); Directory.Exists(childDirectory).ShouldBeFalse();
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); }
            string evidence = Path.Combine(repository, "artifacts", "tests", "run-retention-child"); Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "stdout.log"), await stdout, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(evidence, "stderr.log"), await stderr, CancellationToken.None);
            if (Directory.Exists(Path.Combine(fixture, "child-results")))
                foreach (string result in Directory.GetFiles(Path.Combine(fixture, "child-results"), "*.trx")) File.Copy(result, Path.Combine(evidence, Path.GetFileName(result)), overwrite: true);
        }
    }
    [Fact]
    public async Task ConcurrentRunsCoordinateSweepsAndLeaveTheFinalInactiveBudgetBounded()
    {
        using var active = Open(maximumRuns: 2);
        await Parallel.ForEachAsync(Enumerable.Range(0, 16), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = Token }, (value, token) =>
        { token.ThrowIfCancellationRequested(); using var run = Open(maximumRuns: 2); run.Journal.Write(new("concurrent.run", "test") { Bytes = value }); return ValueTask.CompletedTask; });
        active.SweepInactiveRuns().RetainedRuns.ShouldBeLessThanOrEqualTo(2); Directory.GetDirectories(Root).Length.ShouldBeLessThanOrEqualTo(3);
        active.Journal.Write(new("active.run", "test"));
    }
    private DiagnosticRunJournal Open(int maximumRuns = 8, long maximumBytes = 100_000_000, TimeProvider? clock = null) =>
        DiagnosticRunJournal.Open(Root, "test", maximumInactiveRuns: maximumRuns, maximumInactiveBytes: maximumBytes, timeProvider: clock);
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "plan.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new IOException("Cannot locate child test project.");
    }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-LogRuns-", StringComparison.Ordinal)) throw new IOException("Unexpected fixture root.");
        foreach (var link in links)
        {
            if (!Path.GetFullPath(link.Path).StartsWith(resolved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected link cleanup path.");
            if (link.Directory) Directory.Delete(link.Path); else File.Delete(link.Path);
        }
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow; private readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan duration) => now += duration;
        internal void Fire() { foreach (var timer in timers.ToArray()) timer.Fire(); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(callback, state); timers.Add(timer); return timer; }
    }
    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool disposed; public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
        internal void Fire() { if (!disposed) callback(state); } public void Dispose() => disposed = true; public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newName, string existingName, nint security);
}
