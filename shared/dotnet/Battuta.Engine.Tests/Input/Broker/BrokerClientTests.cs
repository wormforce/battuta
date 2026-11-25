using System.Buffers.Binary;
using Battuta.Engine.Input.Broker;

namespace Battuta.Engine.Tests.Input.Broker;

/// <summary>
/// Client handshake, sequencing, and reconnect semantics over scripted
/// in-memory streams (the plan's synthetic source for PR 4).
/// </summary>
public sealed class BrokerClientTests
{
    [Fact]
    public async Task HandshakeSendsHelloAndPumpsEvents()
    {
        var stream = new ScriptedServerStream(
            BrokerTestMessages.HelloAck,
            BrokerTestMessages.KeyboardPress,
            new BrokerKeyboardEvent(2, 100_000_300_000, 30, 0, BrokerEventOrigin.Hardware));
        var (client, delivered) = CreateClient(stream, new BrokerClientOptions { AutoReconnect = false });

        await client.RunAsync(delivered.Add);

        var helloFrame = Assert.Single(stream.WrittenByClient);
        var hello = Assert.IsType<BrokerHello>(BrokerMessageCodec.DecodeFrame(helloFrame));
        Assert.Equal(BrokerProtocol.ProtocolMajor, hello.ProtocolMajor);
        Assert.Equal(BrokerProtocol.ProtocolMinor, hello.ProtocolMinor);
        Assert.Equal("battuta", hello.ClientName);

        Assert.Equal(
            [
                BrokerTestMessages.KeyboardPress,
                new BrokerKeyboardEvent(2, 100_000_300_000, 30, 0, BrokerEventOrigin.Hardware),
            ],
            delivered.Messages);
        Assert.Equal(2UL, client.LastSequence);
        Assert.Equal(BrokerTestMessages.HelloAck.Seat, client.LastHelloAck!.Seat);
    }

    [Fact]
    public async Task HandshakeErrorIsSurfacedWithoutReconnect()
    {
        var stream = new ScriptedServerStream(BrokerTestMessages.Error);
        var factoryCalls = 0;
        await using var client = new BrokerClient(
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return ValueTask.FromResult<Stream>(stream);
            });

        var exception = await Assert.ThrowsAsync<BrokerHandshakeException>(
            () => client.RunAsync((_, _) => ValueTask.CompletedTask));

        Assert.Equal(BrokerErrorCode.Busy, exception.Code);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task VersionMismatchInHandshakeIsRejected()
    {
        // An encoder refuses to build a major-2 ack, so patch the wire bytes.
        var ackBytes = BrokerMessageCodec.Encode(BrokerTestMessages.HelloAck);
        BinaryPrimitives.WriteUInt16LittleEndian(ackBytes.AsSpan(BrokerProtocol.HeaderLength), 2);
        var stream = new ScriptedServerStream(ackBytes);
        await using var client = new BrokerClient(_ => ValueTask.FromResult<Stream>(stream));

        var exception = await Assert.ThrowsAsync<BrokerHandshakeException>(
            () => client.RunAsync((_, _) => ValueTask.CompletedTask));

        Assert.Equal(BrokerErrorCode.VersionMismatch, exception.Code);
    }

    [Fact]
    public async Task NonHelloAckFrameDuringHandshakeIsRejected()
    {
        var stream = new ScriptedServerStream(BrokerTestMessages.KeyboardPress);
        await using var client = new BrokerClient(_ => ValueTask.FromResult<Stream>(stream));

        var exception = await Assert.ThrowsAsync<BrokerProtocolException>(
            () => client.RunAsync((_, _) => ValueTask.CompletedTask));

        Assert.Equal(BrokerErrorCode.InvalidPayload, exception.Code);
    }

    [Fact]
    public async Task EofDuringHandshakeIsRejected()
    {
        var stream = new ScriptedServerStream(Array.Empty<byte>());
        await using var client = new BrokerClient(_ => ValueTask.FromResult<Stream>(stream));

        var exception = await Assert.ThrowsAsync<BrokerProtocolException>(
            () => client.RunAsync((_, _) => ValueTask.CompletedTask));

        Assert.Equal(BrokerErrorCode.InvalidPayload, exception.Code);
    }

    [Fact]
    public async Task SequenceGapSynthesizesResetBeforeNewerEvent()
    {
        var stream = new ScriptedServerStream(
            BrokerTestMessages.HelloAck,
            new BrokerKeyboardEvent(1, 10, 30, 1, BrokerEventOrigin.Hardware),
            new BrokerKeyboardEvent(2, 11, 30, 0, BrokerEventOrigin.Hardware),
            // Broker dropped events 3..4 and keeps only the newest:
            new BrokerPointerButtonEvent(5, 12, BrokerProtocol.EvdevBtnLeft, 1, BrokerEventOrigin.Hardware));
        var (client, delivered) = CreateClient(stream, new BrokerClientOptions { AutoReconnect = false });

        await client.RunAsync(delivered.Add);

        Assert.Equal(
            [
                new BrokerKeyboardEvent(1, 10, 30, 1, BrokerEventOrigin.Hardware),
                new BrokerKeyboardEvent(2, 11, 30, 0, BrokerEventOrigin.Hardware),
                new BrokerReset(0, 0, BrokerResetReason.SequenceGap),
                new BrokerPointerButtonEvent(5, 12, BrokerProtocol.EvdevBtnLeft, 1, BrokerEventOrigin.Hardware),
            ],
            delivered.Messages);
        Assert.Equal(5UL, client.LastSequence);
    }

    [Fact]
    public async Task SequenceRegressionIsFatal()
    {
        var stream = new ScriptedServerStream(
            BrokerTestMessages.HelloAck,
            new BrokerKeyboardEvent(1, 10, 30, 1, BrokerEventOrigin.Hardware),
            new BrokerKeyboardEvent(1, 11, 30, 0, BrokerEventOrigin.Hardware));
        await using var client = new BrokerClient(_ => ValueTask.FromResult<Stream>(stream));

        var exception = await Assert.ThrowsAsync<BrokerProtocolException>(
            () => client.RunAsync((_, _) => ValueTask.CompletedTask));

        Assert.Equal(BrokerErrorCode.SequenceRegression, exception.Code);
    }

    [Fact]
    public async Task ReconnectRehandshakesAndSynthesizesReset()
    {
        var firstSession = new ScriptedServerStream(
            BrokerTestMessages.HelloAck,
            new BrokerKeyboardEvent(1, 10, 30, 1, BrokerEventOrigin.Hardware));
        var secondSession = new ScriptedServerStream(
            BrokerTestMessages.HelloAck,
            new BrokerKeyboardEvent(1, 20, 30, 1, BrokerEventOrigin.Hardware));
        var queue = new Queue<ScriptedServerStream>([firstSession, secondSession]);
        var stall = new StallingStream();
        var (client, delivered) = CreateClient(
            _ => ValueTask.FromResult<Stream>(queue.Count > 0 ? queue.Dequeue() : stall),
            new BrokerClientOptions { ReconnectDelay = TimeSpan.FromMilliseconds(1) });
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(delivered.Add, cts.Token);

        await delivered.WaitForCountAsync(3, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(
            [
                new BrokerKeyboardEvent(1, 10, 30, 1, BrokerEventOrigin.Hardware),
                new BrokerReset(0, 0, BrokerResetReason.Reconnect),
                new BrokerKeyboardEvent(1, 20, 30, 1, BrokerEventOrigin.Hardware),
            ],
            delivered.Messages);
    }

    [Fact]
    public async Task HeartbeatTimeoutDropsStalledSessionAndRecovers()
    {
        var stalled = new StallingStream();
        var recovered = new ScriptedServerStream(
            BrokerTestMessages.HelloAck,
            new BrokerHeartbeat(1, 5));
        var stallAgain = new StallingStream();
        var queue = new Queue<Stream>([stalled, recovered, stallAgain]);
        var (client, delivered) = CreateClient(
            _ => ValueTask.FromResult<Stream>(queue.Count > 0 ? queue.Dequeue() : stallAgain),
            new BrokerClientOptions
            {
                HandshakeTimeout = TimeSpan.FromMilliseconds(100),
                HeartbeatTimeout = TimeSpan.FromMilliseconds(50),
                ReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(delivered.Add, cts.Token);

        await delivered.WaitForCountAsync(2, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // Session 1 stalls; session 2 re-handshakes (reset) and delivers the heartbeat.
        Assert.Equal(
            [
                new BrokerReset(0, 0, BrokerResetReason.Reconnect),
                new BrokerHeartbeat(1, 5),
            ],
            delivered.Messages);
    }

    private static (BrokerClient Client, MessageCollector Collector) CreateClient(
        Func<CancellationToken, ValueTask<Stream>> connect,
        BrokerClientOptions? options = null) =>
        (new BrokerClient(connect, options), new MessageCollector());

    private static (BrokerClient Client, MessageCollector Collector) CreateClient(
        ScriptedServerStream stream,
        BrokerClientOptions? options = null) =>
        CreateClient(_ => ValueTask.FromResult<Stream>(stream), options);

    private sealed class MessageCollector
    {
        public List<BrokerMessage> Messages { get; } = [];

        public ValueTask Add(BrokerMessage message, CancellationToken cancellationToken)
        {
            lock (Messages)
            {
                Messages.Add(message);
            }

            return ValueTask.CompletedTask;
        }

        public async Task WaitForCountAsync(int count, CancellationToken cancellationToken)
        {
            while (CountLocked() < count)
            {
                await Task.Delay(20, cancellationToken);
            }
        }

        private int CountLocked()
        {
            lock (Messages)
            {
                return Messages.Count;
            }
        }
    }
}
