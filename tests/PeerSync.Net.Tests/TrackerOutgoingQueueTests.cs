using Google.Protobuf;
using Grpc.Core;
using PeerSync.Net.Rendezvous;
using PeerSync.Rendezvous.Contracts.V1;
using Shouldly;
using Xunit;

namespace PeerSync.Net.Tests;

public sealed class TrackerOutgoingQueueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FullCriticalQueueRejectsAnotherRequestAndStillSendsLatestAnnouncement()
    {
        var queue = new TrackerOutgoingQueue(capacity: 1);
        queue.TryEnqueue(Relay()).ShouldBeTrue();
        queue.TryEnqueue(Relay()).ShouldBeFalse();
        uint port = 1;
        queue.Refresh(); port = 2;
        queue.Refresh(); port = 3;

        var writer = new CaptureWriter(expected: 2);
        var announced = new List<uint>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task run = queue.WriteLoopAsync(writer, () => new Announce { ListenPort = port }, stop.Token,
            sent => announced.Add(sent.ListenPort));
        await writer.Completed.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => run);

        writer.Messages.Count.ShouldBe(2);
        writer.Messages[0].Announce.ListenPort.ShouldBe(3u);
        writer.Messages[1].RelayRequest.ShouldNotBeNull();
        announced.ShouldBe([3u]);
    }

    [Fact]
    public async Task ReconnectResetDiscardsOldRequestsAndIdleRefreshWakesWriter()
    {
        var queue = new TrackerOutgoingQueue(capacity: 2);
        queue.TryEnqueue(Relay()).ShouldBeTrue();
        queue.Refresh();
        queue.Reset();
        queue.Refresh();

        var writer = new CaptureWriter(expected: 1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task run = queue.WriteLoopAsync(writer, () => new Announce { ListenPort = 47821 }, stop.Token);
        await writer.Completed.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => run);

        writer.Messages.Count.ShouldBe(1);
        writer.Messages[0].Announce.ListenPort.ShouldBe(47821u);
    }

    private static ClientMessage Relay() => new() { RelayRequest = new RelayRequest
    { DeviceId = ByteString.CopyFrom(new byte[32]), FolderId = ByteString.CopyFrom(new byte[32]) } };

    private sealed class CaptureWriter(int expected) : IClientStreamWriter<ClientMessage>
    {
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<ClientMessage> Messages { get; } = [];
        internal Task Completed => done.Task;
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ClientMessage message)
        {
            Messages.Add(message);
            if (Messages.Count >= expected) done.TrySetResult();
            return Task.CompletedTask;
        }
        public Task WriteAsync(ClientMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }
        public Task CompleteAsync() => Task.CompletedTask;
    }
}
