using System.Runtime.CompilerServices;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

/// <summary>Shared by the one FolderIndex owner for a configured root. Values follow key lifetime;
/// no wait handle is exposed/created, and callers must stop work before disposing their index.</summary>
internal static class FolderOperationGate
{
    private static readonly ConditionalWeakTable<FolderIndex, SemaphoreSlim> Gates = new();
    public static async ValueTask<IDisposable> AcquireAsync(FolderIndex index, CancellationToken cancellationToken)
    {
        var gate = Gates.GetValue(index, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? held = gate;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
    }
}
