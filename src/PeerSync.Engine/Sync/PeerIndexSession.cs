using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Protocol.Handshake;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

/// <summary>Use only after the transport authenticates this device and folder.
/// Identity is fixed by that context; Hello supplies time, never authority.
/// Caller stops sessions before disposing their coordinator/index.</summary>
public sealed class PeerIndexSession
{
    private readonly Puller owner;
    private readonly FolderIndex index;
    private readonly NeededSet needed;
    private readonly byte[] peer;
    private readonly ClockGuard clock;
    private readonly DiagnosticJournal? journal;
    private readonly Guid session;

    internal PeerIndexSession(Puller owner, FolderIndex index, NeededSet needed, byte[] peer, ClockGuard clock, DiagnosticJournal? journal, Guid session)
    { this.owner = owner; this.index = index; this.needed = needed; this.peer = peer.ToArray(); this.clock = clock; this.journal = journal; this.session = session; }

    public byte[] RemoteDeviceId => peer.ToArray();
    public PeerClockStatus ClockStatus => clock.GetStatus();
    public PeerClockStatus ObserveHello(Hello hello) => clock.ObserveHello(hello);
    public void Disconnect() => clock.Reset();

    public async Task<RemoteIndexResult> ReceiveAsync(PeerIndexPage page, CancellationToken cancellationToken = default)
    {
        using var activity = new Activity("PeerSync.PeerIndexReceive").SetIdFormat(ActivityIdFormat.W3C).Start();
        clock.RequireIndexExchangeAllowed(); ArgumentNullException.ThrowIfNull(page);
        try
        {
            var result = await needed.ApplyAdmittedIndexUpdateAsync(peer, page.AfterSequence, page.LastSequence, page.Records,
                clock.RequireIndexExchangeAllowedWithoutDiagnostics, cancellationToken).ConfigureAwait(false);
            owner.NotifyWorkAvailable();
            journal?.TryWrite(new(result.Applied ? "peer.index_receive_complete" : "peer.index_replay", "index")
            { SessionId = session, ItemsVisited = page.Records.Count, ItemsChanged = result.AffectedPaths });
            return result;
        }
        catch (Exception error) { if (error is PeerClockException) _ = clock.GetStatus(); Failure("peer.index_receive_failed", error); throw; }
    }

    public async Task<RemoteIndexRange> AnnounceAsync(long targetSequence, CancellationToken cancellationToken = default)
    {
        using var activity = new Activity("PeerSync.PeerIndexRange").SetIdFormat(ActivityIdFormat.W3C).Start();
        clock.RequireIndexExchangeAllowed();
        try
        {
            var result = await needed.AnnounceAdmittedIndexRangeAsync(peer, targetSequence,
                clock.RequireIndexExchangeAllowedWithoutDiagnostics, cancellationToken).ConfigureAwait(false);
            owner.NotifyWorkAvailable(); return result;
        }
        catch (Exception error) { if (error is PeerClockException) _ = clock.GetStatus(); Failure("peer.index_range_failed", error); throw; }
    }

    public PeerIndexPage ReadIndex(long afterSequence, int limit = 1000)
    {
        using var activity = new Activity("PeerSync.PeerIndexRead").SetIdFormat(ActivityIdFormat.W3C).Start();
        clock.RequireIndexExchangeAllowed();
        try
        {
            var batch = index.ReadIndexBatch(afterSequence, limit);
            var page = new PeerIndexPage(afterSequence, batch.LastSequence, batch.Records.Select(row => row.Record!.WithLocalSequence((ulong)row.LocalSequence)).ToArray());
            clock.RequireIndexExchangeAllowed();
            journal?.TryWrite(new("peer.index_ready", "index") { SessionId = session, ItemsVisited = page.Records.Count });
            return page;
        }
        catch (Exception error) { Failure("peer.index_read_failed", error); throw; }
    }

    private void Failure(string name, Exception error) => journal?.TryWrite(new(name, "index")
    { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult,
        NativeError = error switch { SqliteException sqlite => sqlite.SqliteExtendedErrorCode, Win32Exception native => native.NativeErrorCode, _ => null } });
}
