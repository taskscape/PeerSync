namespace PeerSync.Engine.Scanning;

/// <summary>Process lifetime budget for shared disks and unknown volume mappings.</summary>
internal static class FolderScanBudget
{
    private static readonly SemaphoreSlim Budget = new(1);
    internal static async ValueTask<IDisposable> AcquireAsync(CancellationToken token)
    {
        await Budget.WaitAsync(token).ConfigureAwait(false); return new Lease();
    }
    private sealed class Lease : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) Budget.Release(); }
    }
}
