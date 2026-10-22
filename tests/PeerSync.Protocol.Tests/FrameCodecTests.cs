using System.Buffers;
using System.IO.Pipelines;
using PeerSync.Protocol.Framing;
using Shouldly;
using Xunit;

namespace PeerSync.Protocol.Tests;

public sealed class FrameCodecTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(127)] [InlineData(128)]
    [InlineData(16384)] [InlineData(FrameCodec.MaximumBodyBytes)]
    public void CompleteFramesRoundTripWithoutEatingTheNextFrame(int size)
    {
        byte[] body = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
        var bytes = new ArrayBufferWriter<byte>();
        FrameCodec.Write(bytes, 42, body); FrameCodec.Write(bytes, 7, [99]);
        var input = new ReadOnlySequence<byte>(bytes.WrittenMemory);
        FrameCodec.TryRead(ref input, out var frame).ShouldBeTrue();
        frame.Type.ShouldBe((byte)42); frame.Body.ToArray().ShouldBe(body);
        FrameCodec.TryRead(ref input, out frame).ShouldBeTrue();
        frame.Type.ShouldBe((byte)7); frame.Body.ToArray().ShouldBe(new byte[] { 99 });
        input.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void HeaderAndBodyCanSpanIndependentSequenceSegments()
    {
        var encoded = new ArrayBufferWriter<byte>(); byte[] body = Enumerable.Range(0, 130).Select(i => (byte)i).ToArray();
        FrameCodec.Write(encoded, 8, body);
        var first = new Segment(encoded.WrittenMemory[..1]); var last = first;
        for (int index = 1; index < encoded.WrittenCount; index++) last = last.Append(encoded.WrittenMemory.Slice(index, 1));
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        FrameCodec.TryRead(ref sequence, out var frame).ShouldBeTrue();
        frame.Type.ShouldBe((byte)8); frame.Body.ToArray().ShouldBe(body); sequence.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void EveryIncompletePrefixTypeAndPayloadPreservesTheOriginalSequence()
    {
        var bytes = new ArrayBufferWriter<byte>(); FrameCodec.Write(bytes, 3, new byte[130]);
        for (int count = 0; count < bytes.WrittenCount; count++)
        {
            var input = new ReadOnlySequence<byte>(bytes.WrittenMemory[..count]); var original = input;
            FrameCodec.TryRead(ref input, out _).ShouldBeFalse();
            input.Start.ShouldBe(original.Start); input.End.ShouldBe(original.End);
        }
    }

    [Theory]
    [InlineData(new byte[] { 128, 0 })]
    [InlineData(new byte[] { 128, 128, 128, 128, 128 })]
    [InlineData(new byte[] { 128, 128, 128, 128, 16, 1 })]
    [InlineData(new byte[] { 255, 255, 255, 255, 127, 1 })]
    public void OverlongAndOverflowingVarintsCannotAliasSmallLengths(byte[] bytes)
    {
        var input = new ReadOnlySequence<byte>(bytes);
        Should.Throw<ProtocolException>(() => FrameCodec.TryRead(ref input, out _)).Code.ShouldBe(ProtocolError.InvalidLengthPrefix);
    }

    [Fact]
    public void OversizedLengthIsRejectedBeforeAnyPayloadArrivesOrOutputIsWritten()
    {
        var input = new ReadOnlySequence<byte>(new byte[] { 128, 128, 128, 9 });
        Should.Throw<ProtocolException>(() => FrameCodec.TryRead(ref input, out _)).Code.ShouldBe(ProtocolError.FrameTooLarge);
        var writer = new ArrayBufferWriter<byte>();
        Should.Throw<ProtocolException>(() => FrameCodec.Write(writer, 1, new byte[FrameCodec.MaximumBodyBytes + 1])).Code.ShouldBe(ProtocolError.FrameTooLarge);
        writer.WrittenCount.ShouldBe(0);
    }

    [Fact]
    public async Task FragmentedReadsWaitForMoreDataAndDoNotSpin()
    {
        var pipe = new Pipe(); var reader = new CountingReader(pipe.Reader);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var frames = FrameCodec.ReadFramesAsync(reader, deadline.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            await pipe.Writer.WriteAsync(new byte[] { 128 }, deadline.Token);
            Task<bool> pending = frames.MoveNextAsync().AsTask();
            await Task.Delay(25, deadline.Token);
            pending.IsCompleted.ShouldBeFalse(); reader.ReadCalls.ShouldBeLessThan(4);
            await pipe.Writer.WriteAsync(new byte[] { 1, 12 }, deadline.Token);
            await Task.Delay(25, deadline.Token); pending.IsCompleted.ShouldBeFalse();
            await pipe.Writer.WriteAsync(new byte[128], deadline.Token);
            (await pending).ShouldBeTrue(); frames.Current.Type.ShouldBe((byte)12); frames.Current.Body.Length.ShouldBe(128);
            await pipe.Writer.CompleteAsync(); (await frames.MoveNextAsync()).ShouldBeFalse();
        }
        finally { await frames.DisposeAsync(); await reader.CompleteAsync(); await pipe.Writer.CompleteAsync(); }
    }

    [Fact]
    public async Task MalformedPrefixSplitAcrossReadsKeepsTheProtocolError()
    {
        var pipe = new Pipe(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var frames = FrameCodec.ReadFramesAsync(pipe.Reader, deadline.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            await pipe.Writer.WriteAsync(new byte[] { 0x80 }, deadline.Token);
            Task<bool> pending = frames.MoveNextAsync().AsTask();
            await Task.Delay(25, deadline.Token); pending.IsCompleted.ShouldBeFalse();
            await pipe.Writer.WriteAsync(new byte[] { 0x00 }, deadline.Token);
            var error = await Should.ThrowAsync<ProtocolException>(async () => await pending);
            error.Code.ShouldBe(ProtocolError.InvalidLengthPrefix);
        }
        finally { await frames.DisposeAsync(); await pipe.Reader.CompleteAsync(); await pipe.Writer.CompleteAsync(); }
    }

    [Theory]
    [InlineData(new byte[] { 128 })] [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 2, 1, 1 })]
    public async Task EofInAnyFramePartIsAProtocolViolation(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); var reader = PipeReader.Create(stream);
        try
        {
            var error = await Should.ThrowAsync<ProtocolException>(async () =>
            { await foreach (var _ in FrameCodec.ReadFramesAsync(reader, TestContext.Current.CancellationToken)) { } });
            error.Code.ShouldBe(ProtocolError.TruncatedFrame);
        }
        finally { await reader.CompleteAsync(); }
    }

    [Fact]
    public async Task DisposingAfterOneFrameLeavesTheUnexaminedNextFrameReadable()
    {
        var pipe = new Pipe(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        FrameCodec.Write(pipe.Writer, 1, [10]); FrameCodec.Write(pipe.Writer, 2, [20]); await pipe.Writer.FlushAsync(deadline.Token);
        try
        {
            await using (var first = FrameCodec.ReadFramesAsync(pipe.Reader, deadline.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken))
            { (await first.MoveNextAsync()).ShouldBeTrue(); first.Current.Type.ShouldBe((byte)1); }
            await using var next = FrameCodec.ReadFramesAsync(pipe.Reader, deadline.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            (await next.MoveNextAsync()).ShouldBeTrue(); next.Current.Type.ShouldBe((byte)2);
        }
        finally { await pipe.Writer.CompleteAsync(); await pipe.Reader.CompleteAsync(); }
    }

    [Fact]
    public async Task CancellationStopsAnIncompleteFrameWithoutInventingProtocolFailure()
    {
        var pipe = new Pipe(); using var stop = new CancellationTokenSource();
        await using var frames = FrameCodec.ReadFramesAsync(pipe.Reader, stop.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            await pipe.Writer.WriteAsync(new byte[] { 128 }, TestContext.Current.CancellationToken);
            Task<bool> pending = frames.MoveNextAsync().AsTask(); stop.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        }
        finally { await frames.DisposeAsync(); await pipe.Reader.CompleteAsync(); await pipe.Writer.CompleteAsync(); }
    }

    private sealed class CountingReader(PipeReader inner) : PipeReader
    {
        public int ReadCalls { get; private set; }
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (++ReadCalls > 20) throw new InvalidOperationException("Incomplete frame caused repeated reads without new data.");
            return inner.ReadAsync(cancellationToken);
        }
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) { Memory = memory; }
        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length }; Next = next; return next;
        }
    }
}
