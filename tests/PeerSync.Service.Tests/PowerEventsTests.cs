using PeerSync.Service.Control;
using PeerSync.Service.Hosting;
using Shouldly;
using Xunit;

namespace PeerSync.Service.Tests;

public sealed class PowerEventsTests
{
    [Fact]
    public void SuspendAndResumeNotificationsReachRuntimeSubscribersAndIsolateFailures()
    {
        var runtime = new ControlStatusRuntime();
        var events = new PowerEvents();
        int suspendCount = 0, resumeCount = 0;
        events.Suspending += runtime.NotifySuspending;
        events.Resumed += runtime.NotifyResumed;
        runtime.Suspending += () => throw new InvalidOperationException("test-only runtime callback failure");
        runtime.Suspending += () => suspendCount++;
        runtime.Resumed += () => resumeCount++;
        events.Suspending += () => throw new InvalidOperationException("test-only callback failure");
        events.Suspending += () => suspendCount++;

        events.RaiseSuspending();
        events.RaiseResumed();

        suspendCount.ShouldBe(2);
        resumeCount.ShouldBe(1);
    }
}
