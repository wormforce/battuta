namespace Battuta.Engine.Input.Broker;

/// <summary>Handshake rejected by the broker (or by version negotiation).</summary>
public sealed class BrokerHandshakeException(BrokerErrorCode code, string message) : Exception(message)
{
    public BrokerErrorCode Code { get; } = code;
}

/// <summary>Options for <see cref="BrokerClient"/>.</summary>
public sealed record BrokerClientOptions
{
    /// <summary>Informational client name sent in hello (max 64 UTF-8 bytes).</summary>
    public string ClientName { get; init; } = "battuta";

    public ushort ProtocolMajor { get; init; } = BrokerProtocol.ProtocolMajor;

    public ushort ProtocolMinor { get; init; } = BrokerProtocol.ProtocolMinor;

    /// <summary>Reconnect after connection loss. Never reconnects after
    /// authorization/version/busy errors, which would only repeat.</summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>Delay before a reconnect attempt.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Handshake deadline per session. Zero disables the timeout.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Treat a session as dead when no frame arrives within this
    /// window. Zero disables the timeout (contract default allows it).</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.Zero;
}

/// <summary>Receives decoded broker messages in order.</summary>
public delegate ValueTask BrokerMessageHandler(BrokerMessage message, CancellationToken cancellationToken);

/// <summary>
/// Connects to battuta-inputd, performs the hello/hello_ack handshake, and
/// pumps decoded messages to a handler. Sequence bookkeeping is per session:
/// forward gaps synthesize a <see cref="BrokerResetReason.SequenceGap"/> reset
/// before the newer event; regressions drop the session; every re-handshake
/// synthesizes a <see cref="BrokerResetReason.Reconnect"/> reset so pressed
/// state is cleared. The transport is any <see cref="Stream"/> factory, so
/// tests run over in-memory streams and Linux binds a socket stream.
/// </summary>
public sealed class BrokerClient(Func<CancellationToken, ValueTask<Stream>> connect, BrokerClientOptions? options = null)
    : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask<Stream>> _connect =
        connect ?? throw new ArgumentNullException(nameof(connect));

    private readonly BrokerClientOptions _options = options ?? new BrokerClientOptions();
    private volatile bool _connected;

    public bool IsConnected => _connected;

    /// <summary>Sequence of the last event, heartbeat, or reset in the current session; 0 before the first.</summary>
    public ulong LastSequence { get; private set; }

    public BrokerHelloAck? LastHelloAck { get; private set; }

    /// <summary>
    /// Runs until cancelled: connect, handshake, pump, and reconnect on
    /// transport loss (EOF, IO errors, heartbeat timeout). Wire-level protocol
    /// violations and handshake rejections (unauthorized, busy, version
    /// mismatch) are fatal and propagate. Handler exceptions propagate and
    /// stop the client.
    /// </summary>
    public async Task RunAsync(BrokerMessageHandler onMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onMessage);

        var firstSession = true;
        while (true)
        {
            try
            {
                await RunSingleSessionAsync(onMessage, firstSession, cancellationToken).ConfigureAwait(false);
            }
            catch (BrokerHandshakeException)
            {
                throw;
            }
            catch (BrokerProtocolException)
            {
                throw;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // Transport loss: fall through to the reconnect path.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            firstSession = false;

            if (!_options.AutoReconnect)
            {
                return;
            }

            if (_options.ReconnectDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunSingleSessionAsync(
        BrokerMessageHandler onMessage,
        bool firstSession,
        CancellationToken cancellationToken)
    {
        await using var stream = await _connect(cancellationToken).ConfigureAwait(false);
        using var heartbeatCts = _options.HeartbeatTimeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        var readToken = heartbeatCts?.Token ?? cancellationToken;

        // One decoder per session: bytes read during the handshake that belong
        // to later frames must not be discarded (stream transports may coalesce
        // frames into a single read).
        var decoder = new BrokerFrameDecoder();
        var ack = await HandshakeAsync(stream, decoder, cancellationToken).ConfigureAwait(false);
        LastHelloAck = ack;
        LastSequence = 0;
        _connected = true;
        try
        {
            if (!firstSession)
            {
                await onMessage(
                    new BrokerReset(0, 0, BrokerResetReason.Reconnect),
                    cancellationToken).ConfigureAwait(false);
            }

            var buffer = new byte[BrokerProtocol.MaxFrameLength];
            while (true)
            {
                if (heartbeatCts is not null)
                {
                    heartbeatCts.CancelAfter(_options.HeartbeatTimeout);
                }

                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, readToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (heartbeatCts is not null && !cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("Heartbeat timeout: no frame received in time.");
                }

                if (read == 0)
                {
                    await DrainDecoderAsync(decoder, onMessage, cancellationToken).ConfigureAwait(false);
                    return; // clean EOF; session over
                }

                decoder.Append(buffer.AsSpan(0, read));
                while (true)
                {
                    if (!decoder.TryRead(out var message, out _, out var error))
                    {
                        if (error is not null)
                        {
                            throw error;
                        }

                        break; // need more data
                    }

                    var (deliver, preceding) = NormalizeSequencing(message!);
                    if (preceding is not null)
                    {
                        await onMessage(preceding, cancellationToken).ConfigureAwait(false);
                    }

                    await onMessage(deliver, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _connected = false;
            heartbeatCts?.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Delivers every frame fully buffered before the EOF. Stream transports
    /// may coalesce frames into one read; those trailing frames are real
    /// messages, not garbage.
    /// </summary>
    private async Task DrainDecoderAsync(
        BrokerFrameDecoder decoder,
        BrokerMessageHandler onMessage,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!decoder.TryRead(out var message, out _, out var error))
            {
                if (error is not null)
                {
                    throw error;
                }

                return;
            }

            var (deliver, preceding) = NormalizeSequencing(message!);
            if (preceding is not null)
            {
                await onMessage(preceding, cancellationToken).ConfigureAwait(false);
            }

            await onMessage(deliver, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<BrokerHelloAck> HandshakeAsync(
        Stream stream,
        BrokerFrameDecoder decoder,
        CancellationToken cancellationToken)
    {
        using var handshakeCts = _options.HandshakeTimeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        var token = handshakeCts?.Token ?? cancellationToken;
        if (handshakeCts is not null)
        {
            handshakeCts.CancelAfter(_options.HandshakeTimeout);
        }

        var hello = new BrokerHello(
            _options.ProtocolMajor,
            _options.ProtocolMinor,
            Caps: 0,
            _options.ClientName);
        var helloFrame = BrokerMessageCodec.Encode(hello);
        try
        {
            await stream.WriteAsync(helloFrame, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (handshakeCts is not null && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Handshake timed out writing hello.");
        }

        var buffer = new byte[BrokerProtocol.MaxFrameLength];
        while (true)
        {
            if (decoder.TryRead(out var message, out _, out var error))
            {
                if (error is not null || message is null)
                {
                    throw error ?? new BrokerProtocolException(BrokerErrorCode.InvalidPayload, "Decoder reported no message.");
                }

                if (message is BrokerError handshakeError)
                {
                    throw new BrokerHandshakeException(
                        handshakeError.Code,
                        $"Broker rejected the handshake: {handshakeError.Message}");
                }

                if (message is not BrokerHelloAck ack)
                {
                    throw new BrokerProtocolException(
                        BrokerErrorCode.InvalidPayload,
                        $"Expected hello_ack, got {message.GetType().Name}.");
                }

                if (ack.ProtocolMajor != _options.ProtocolMajor)
                {
                    throw new BrokerHandshakeException(
                        BrokerErrorCode.VersionMismatch,
                        $"Broker protocol major {ack.ProtocolMajor} does not match client {_options.ProtocolMajor}.");
                }

                return ack with { ProtocolMinor = Math.Min(ack.ProtocolMinor, _options.ProtocolMinor) };
            }

            if (error is not null)
            {
                throw error;
            }

            int read;
            try
            {
                read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (handshakeCts is not null && !cancellationToken.IsCancellationRequested)
            {
                // An unresponsive broker is a transport loss, not a protocol
                // rejection: surface it as IO so the reconnect path applies.
                throw new IOException("Handshake timed out waiting for hello_ack.");
            }

            if (read == 0)
            {
                throw new BrokerProtocolException(BrokerErrorCode.InvalidPayload, "Broker closed the connection during handshake.");
            }

            decoder.Append(buffer.AsSpan(0, read));
        }
    }

    /// <summary>
    /// Tracks per-session sequencing. Returns the message to deliver plus an
    /// optional synthesized reset that must be delivered first: forward gaps
    /// (dropped broker backlog) yield a <see cref="BrokerResetReason.SequenceGap"/>
    /// reset before the newer event.
    /// </summary>
    private (BrokerMessage Deliver, BrokerMessage? Preceding) NormalizeSequencing(BrokerMessage message)
    {
        if (message is not (BrokerKeyboardEvent or BrokerPointerButtonEvent or BrokerReset or BrokerHeartbeat))
        {
            return (message, null); // hello/hello_ack/error carry no sequence
        }

        var sequence = message switch
        {
            BrokerKeyboardEvent keyboard => keyboard.Sequence,
            BrokerPointerButtonEvent pointer => pointer.Sequence,
            BrokerReset reset => reset.Sequence,
            BrokerHeartbeat heartbeat => heartbeat.Sequence,
            _ => 0UL,
        };

        if (sequence > LastSequence + 1)
        {
            LastSequence = sequence;
            return (WithSequence(message, sequence), new BrokerReset(0, 0, BrokerResetReason.SequenceGap));
        }

        if (sequence <= LastSequence)
        {
            throw new BrokerProtocolException(
                BrokerErrorCode.SequenceRegression,
                $"Sequence regression: {sequence} follows {LastSequence}.");
        }

        LastSequence = sequence;
        return (message, null);
    }

    private static BrokerMessage WithSequence(BrokerMessage message, ulong sequence) => message switch
    {
        BrokerKeyboardEvent keyboard => keyboard with { Sequence = sequence },
        BrokerPointerButtonEvent pointer => pointer with { Sequence = sequence },
        BrokerReset reset => reset with { Sequence = sequence },
        BrokerHeartbeat heartbeat => heartbeat with { Sequence = sequence },
        _ => message,
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
