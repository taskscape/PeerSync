using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Net.InMemory;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Benchmarks;

/// <summary>Fresh-peer materialization of a real scanner corpus. The generated
/// peer and its database are intentionally retained for failure inspection.</summary>
internal static class MillionPeerBenchmark
{
    public static async Task<bool> SyncAsync(string fixture, string sourceRoot, FolderIndex sourceIndex,
        FolderKeys keys, int expectedFiles)
    {
        string runId = Guid.NewGuid().ToString("N");
        string peerRoot = Path.Combine(fixture, "peer-" + runId);
        string peerDatabase = Path.Combine(fixture, "peer-index-" + runId + ".db");
        string peerLogs = Path.Combine(fixture, "peer-logs-" + runId);
        Directory.CreateDirectory(peerRoot);
        byte[] sourceId = new byte[32];
        byte[] peerId = new byte[32]; peerId[0] = 1;
        var elapsed = Stopwatch.StartNew();
        string phase = "open";
        int round = 0;
        // Source publication alone takes thousands of bounded 1,000-member
        // passes at million-file scale; this is a ceiling, not a target time.
        using var deadline = new CancellationTokenSource(TimeSpan.FromHours(12));
        using var heartbeatStop = new CancellationTokenSource();
        Task heartbeat = HeartbeatAsync(() => phase, () => round, elapsed, heartbeatStop.Token);
        try
        {
            await using var peerIndex = await FolderIndex.OpenAsync(peerDatabase, deadline.Token).ConfigureAwait(false);
            using var sourceJournal = new DiagnosticJournal(Path.Combine(fixture, "source-peer-logs-" + runId),
                "million-peer-source", debugEnabled: true);
            using var peerJournal = new DiagnosticJournal(peerLogs, "million-peer-destination", debugEnabled: true);
            await using var source = new TransportFolder(sourceRoot, sourceIndex, keys, sourceId, journal: sourceJournal);
            await using var peer = new TransportFolder(peerRoot, peerIndex, keys, peerId, journal: peerJournal);

            phase = "source-publication";
            source.RequestScan();
            for (int pass = 0; pass < 100_000; pass++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                PullPassResult result = await source.RunPassAsync(deadline.Token).ConfigureAwait(false);
                if (pass % 10 == 0) Report(phase, elapsed, pass, sourceIndex, peerIndex, result);
                if (Settled(sourceIndex, result)) break;
                if (pass == 99_999) throw new InvalidDataException("Source publication did not settle.");
                if (!result.MoreWork) await Task.Delay(10, deadline.Token).ConfigureAwait(false);
            }

            phase = "connect";
            var network = new InMemoryNetwork();
            var sourceEndpoint = network.Register(sourceId, source.CreateHandler);
            var peerEndpoint = network.Register(peerId, peer.CreateHandler);
            await using var sourceLink = await source.ConnectAsync(sourceEndpoint.Connect(peerId), deadline.Token).ConfigureAwait(false);
            await using var peerLink = await peer.ConnectAsync(peerEndpoint.Connect(sourceId), deadline.Token).ConfigureAwait(false);

            phase = "converge";
            long lastHigh = -1;
            int stable = 0;
            for (round = 0; round < 100_000; round++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await sourceLink.SynchronizeAsync(maxPages: 8, cancellationToken: deadline.Token).ConfigureAwait(false);
                await peerLink.SynchronizeAsync(maxPages: 8, cancellationToken: deadline.Token).ConfigureAwait(false);
                PullPassResult sourcePass = await source.RunPassAsync(deadline.Token).ConfigureAwait(false);
                PullPassResult peerPass = await peer.RunPassAsync(deadline.Token).ConfigureAwait(false);
                if (round % 10 == 0) Report(phase, elapsed, round, sourceIndex, peerIndex, peerPass);
                long high = peerIndex.ReadIndexHighWater();
                bool ready = high == lastHigh && Settled(sourceIndex, sourcePass) && Settled(peerIndex, peerPass)
                    && peerIndex.LocalFiles.Count() == expectedFiles + (expectedFiles + 999) / 1000;
                stable = ready ? stable + 1 : 0;
                if (stable >= 2) break;
                lastHigh = high;
                if (round == 99_999) throw new InvalidDataException("Fresh peer did not converge.");
                if (!sourcePass.MoreWork && !peerPass.MoreWork)
                    await Task.Delay(10, deadline.Token).ConfigureAwait(false);
            }

            phase = "verify-native";
            int verified = VerifyNative(peerRoot, expectedFiles, elapsed, deadline.Token);
            long peerRows = peerIndex.LocalFiles.Count();
            bool correct = verified == expectedFiles && peerRows == expectedFiles + (expectedFiles + 999) / 1000;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                target = "P5 million-file fresh peer", expectedFiles, verifiedFiles = verified,
                peerRows, rounds = round + 1, seconds = elapsed.Elapsed.TotalSeconds,
                peerRoot, peerDatabase, peerLogs, correct,
                scope = "two production TransportFolder coordinators over the trusted in-memory test transport; every fresh-peer native byte and pathname checked"
            }));
            return correct;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                target = "P5 million-file fresh peer", status = "failed", phase,
                rounds = round, seconds = elapsed.Elapsed.TotalSeconds,
                peerRoot, peerDatabase, peerLogs, error = error.ToString()
            }));
            return false;
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
    }

    private static bool Settled(FolderIndex index, PullPassResult pass) => !pass.MoreWork && pass.Failed == 0
        && index.Needed.Count() == 0 && !index.ReadApplyIntents().Any()
        && index.ReadPendingCasePreservations().Count == 0 && !index.HasPendingDirectoryPublications();

    private static void Report(string phase, Stopwatch elapsed, int number, FolderIndex source, FolderIndex peer,
        PullPassResult pass) => Console.WriteLine(JsonSerializer.Serialize(new
        {
            target = "P5 million-file fresh peer", phase, number, seconds = elapsed.Elapsed.TotalSeconds,
            sourceHighWater = source.ReadIndexHighWater(), peerHighWater = peer.ReadIndexHighWater(),
            peerRows = peer.LocalFiles.Count(), needed = peer.Needed.Count(), pass.Applied, pass.Failed, pass.MoreWork
        }));

    private static int VerifyNative(string peerRoot, int expectedFiles, Stopwatch elapsed, CancellationToken token)
    {
        int buckets = (expectedFiles + 999) / 1000;
        string[] actualDirectories = Directory.GetDirectories(peerRoot)
            .Where(path => Path.GetFileName(path) != ".psp").ToArray();
        if (actualDirectories.Length != buckets) throw new InvalidDataException("Fresh peer has unexpected directory count.");
        int verified = 0;
        for (int bucket = 0; bucket < buckets; bucket++)
        {
            token.ThrowIfCancellationRequested();
            string directory = Path.Combine(peerRoot, bucket.ToString("D4", CultureInfo.InvariantCulture));
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Fresh peer is missing an ordinary corpus directory.");
            int expectedInBucket = Math.Min(1000, expectedFiles - bucket * 1000);
            if (Directory.GetFiles(directory).Length != expectedInBucket)
                throw new InvalidDataException("Fresh peer has unexpected file count in a corpus directory.");
            for (int offset = 0; offset < expectedInBucket; offset++)
            {
                int number = bucket * 1000 + offset;
                string path = Path.Combine(directory, number.ToString("D7", CultureInfo.InvariantCulture));
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Fresh peer contains a reparse point.");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != 1 || stream.ReadByte() != number % 251)
                    throw new InvalidDataException($"Fresh peer differs at file {number}.");
                verified++;
            }
            if (bucket % 100 == 0)
                Console.WriteLine(JsonSerializer.Serialize(new
                { target = "P5 million-file fresh peer", phase = "verify-native", verified, seconds = elapsed.Elapsed.TotalSeconds }));
        }
        return verified;
    }

    private static async Task HeartbeatAsync(Func<string> phase, Func<int> round, Stopwatch elapsed, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                Console.WriteLine(JsonSerializer.Serialize(new
                { target = "P5 million-file fresh peer", phase = phase(), round = round(), seconds = elapsed.Elapsed.TotalSeconds }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
