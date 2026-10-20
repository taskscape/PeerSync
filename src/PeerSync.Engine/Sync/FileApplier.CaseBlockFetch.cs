using System.Security.Cryptography;
using PeerSync.Core.Diagnostics;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

public sealed partial class FileApplier
{
    internal sealed class CaseBlockFetch : IDisposable
    {
        private readonly FileApplier owner;
        private readonly PendingCasePreservation pending;
        private readonly VersionView source;
        private readonly Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch;
        private readonly CancellationTokenSource deadline;
        private readonly Guid session = Guid.NewGuid();
        private CaseBlockRoutePage? page;
        private byte[]? preferred, remembered;

        internal CaseBlockFetch(FileApplier owner, PendingCasePreservation pending, VersionView source,
            Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch, CancellationToken token)
        {
            this.owner = owner; this.pending = pending; this.source = source; this.fetch = fetch;
            deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        }

        internal async ValueTask<ReadOnlyMemory<byte>> FetchAsync(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (page is null)
            {
                using var references = await FolderReferenceGate.AcquireAsync(owner.index, cancellation).ConfigureAwait(false);
                page = await owner.index.AdvanceCaseBlockRoutesAsync(pending, cancellation).ConfigureAwait(false);
                remembered = page.Hint; preferred = remembered ?? pending.Source.SourceDevice;
            }
            var candidates = new[] { preferred!, source.ModifiedBy.ToArray() }.Concat(page.Devices)
                .Where(candidate => !candidate.AsSpan().SequenceEqual(owner.ownDevice)).DistinctBy(Convert.ToHexString);
            foreach (var candidate in candidates)
            {
                cancellation.ThrowIfCancellationRequested(); if (deadline.IsCancellationRequested) break;
                using var routeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
                routeDeadline.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    var response = await fetch(candidate, hash, length, routeDeadline.Token).ConfigureAwait(false);
                    routeDeadline.Token.ThrowIfCancellationRequested();
                    if (response.Length != length) throw new InvalidDataException("Case payload response length mismatch.");
                    byte[] owned = response.ToArray();
                    if (!CryptographicOperations.FixedTimeEquals(Blake3.Hasher.Hash(owned).AsSpan(), hash.Span))
                        throw new CryptographicException("Case payload response hash mismatch.");
                    if (!candidate.AsSpan().SequenceEqual(remembered))
                    {
                        using var references = await FolderReferenceGate.AcquireAsync(owner.index, cancellation).ConfigureAwait(false);
                        await owner.index.RememberCaseBlockRouteAsync(pending, candidate, cancellation).ConfigureAwait(false);
                        if (!candidate.AsSpan().SequenceEqual(preferred)) Log("failover");
                        preferred = candidate.ToArray(); remembered = preferred;
                    }
                    return owned;
                }
                catch (Exception error) when (CaseSourceUnavailable(error, cancellation) && error is not OperationCanceledException || error is CryptographicException)
                { Log("unavailable", error); }
                catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
                { Log("timeout", error); }
            }
            cancellation.ThrowIfCancellationRequested();
            throw new IOException("No authenticated case payload route is available.");
        }

        private void Log(string stage, Exception? error = null) => owner.journal?.TryWrite(new("apply.case-route-" + stage, "case-route")
        {
            SessionId = session, Level = error is null ? DiagnosticLevel.Debug : DiagnosticLevel.Warning,
            ErrorCode = error is OperationCanceledException ? "timeout" : error?.GetType().Name, HResult = error?.HResult
        });
        public void Dispose() => deadline.Dispose();
    }
}
