using System.Runtime.CompilerServices;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

/// <summary>Serializes index-reference mutations. Maintenance takes the
/// filesystem gate first, then this gate. Index handlers take only this gate;
/// they must never wait for filesystem work while holding it.</summary>
internal static class FolderReferenceGate
{
    private static readonly ConditionalWeakTable<FolderIndex, SemaphoreSlim> Gates = new();
    internal static async ValueTask<IDisposable> AcquireAsync(FolderIndex index, CancellationToken token)
    {
        var gate = Gates.GetValue(index, _ => new(1, 1)); await gate.WaitAsync(token).ConfigureAwait(false); return new Lease(gate);
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? held = gate;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
    }
}
