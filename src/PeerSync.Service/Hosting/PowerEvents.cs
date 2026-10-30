using PeerSync.Core.Diagnostics;

namespace PeerSync.Service.Hosting;

/// <summary>Delivers Windows suspend/resume notifications to service-owned runtime components.</summary>
public sealed class PowerEvents(DiagnosticJournal? diagnostics = null)
{
    public event Action? Suspending;
    public event Action? Resumed;

    public void RaiseSuspending() => Raise(Suspending, "service.power_suspending");
    public void RaiseResumed() => Raise(Resumed, "service.power_resumed");

    private void Raise(Action? callbacks, string eventName)
    {
        if (callbacks is null) return;
        foreach (Action callback in callbacks.GetInvocationList())
        {
            try { callback(); }
            catch (Exception error)
            {
                diagnostics?.TryWrite(new(eventName, "service_power")
                    { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult });
            }
        }
    }
}
